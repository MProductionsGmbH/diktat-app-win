using Diktat.Transcription.Engines;
using Diktat.Transcription.Models;

namespace Diktat.Transcription;

/// The speech model the app dictates with: holds the selected model, loads it on first use
/// (or on <see cref="WarmUpAsync"/>) and keeps it loaded until the selection changes.
///
/// Like the macOS app, a selected model that is not on disk falls back to the first
/// installed one, so dictation keeps working while another model downloads.
public sealed class LocalTranscriptionService : IDisposable
{
    /// Silence added before and after short recordings; models clip the first and last word less.
    public const double PaddingSeconds = 0.3;
    /// Longer recordings (files, meetings) start with room anyway.
    public const double MaximumPaddedSeconds = 600;

    private readonly ModelStore _store;
    private readonly Func<ModelInfo, ModelStore, ISpeechTranscriber> _loader;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly Lock _lock = new();
    private ISpeechTranscriber? _loaded;
    private string _selectedModelId;
    private bool _disposed;

    /// <param name="loader">Creates a transcriber for an installed model; tests pass a fake.</param>
    public LocalTranscriptionService(ModelStore store, string? selectedModelId = null, Func<ModelInfo, ModelStore, ISpeechTranscriber>? loader = null)
    {
        _store = store;
        _loader = loader ?? DefaultLoader;
        _selectedModelId = ModelCatalog.TranscriptionModelOrDefault(selectedModelId).Id;
    }

    /// Raised when a model was loaded or unloaded (on the thread that did it).
    public event Action? StateChanged;

    public ModelStore Store => _store;

    public string SelectedModelId
    {
        get { lock (_lock) return _selectedModelId; }
    }

    public ModelInfo SelectedModel => ModelCatalog.TranscriptionModelOrDefault(SelectedModelId);

    /// The model a dictation uses: the selected one when installed, else the first installed
    /// transcription model (recommended first), else null.
    public ModelInfo? ActiveModel
    {
        get
        {
            var selected = SelectedModel;
            if (_store.IsInstalled(selected)) return selected;
            return ModelCatalog.TranscriptionModels.FirstOrDefault(_store.IsInstalled);
        }
    }

    public bool IsSelectedModelInstalled => _store.IsInstalled(SelectedModel);

    /// Whether any transcription model can be used.
    public bool HasModel => ActiveModel is not null;

    /// True once the active model is loaded, so a transcription starts right away.
    public bool IsReady
    {
        get
        {
            ISpeechTranscriber? loaded;
            lock (_lock) loaded = _loaded;
            return loaded is not null && ActiveModel?.Id == loaded.ModelId;
        }
    }

    /// The id of the model in memory, or null.
    public string? LoadedModelId
    {
        get { lock (_lock) return _loaded?.ModelId; }
    }

    /// Selects a model (an id of <see cref="ModelCatalog"/>). A loaded model that is no longer
    /// the active one is unloaded in the background.
    public void SelectModel(string modelId)
    {
        lock (_lock) _selectedModelId = ModelCatalog.TranscriptionModelOrDefault(modelId).Id;
        UnloadIfStale();
    }

    /// Loads the active model now, e.g. at app start or when the hotkey goes down.
    public async Task WarmUpAsync(CancellationToken ct = default) => await GetTranscriberAsync(ct).ConfigureAwait(false);

    /// The loaded transcriber for the active model, loading it when needed.
    public async Task<ISpeechTranscriber> GetTranscriberAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var model = ActiveModel ?? throw TranscriptionException.NoModel();
        lock (_lock)
        {
            if (_loaded is { } ready && ready.ModelId == model.Id) return ready;
        }

        // One load at a time: a second dictation during a slow load waits for it.
        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ISpeechTranscriber? stale = null;
            lock (_lock)
            {
                if (_loaded is { } ready && ready.ModelId == model.Id) return ready;
                stale = _loaded;
                _loaded = null;
            }
            // Free the old model before the new one takes its memory.
            if (stale is not null) await Task.Run(stale.Dispose, CancellationToken.None).ConfigureAwait(false);

            ISpeechTranscriber transcriber;
            try
            {
                transcriber = await Task.Run(() => _loader(model, _store), ct).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not TranscriptionException and not OperationCanceledException)
            {
                throw new TranscriptionException(TranscriptionError.LoadFailed, TranscriptionException.LoadFailedMessage(model.DisplayName), error);
            }
            lock (_lock) _loaded = transcriber;
            StateChanged?.Invoke();
            return transcriber;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// Transcribes a dictation. Throws <see cref="TranscriptionException"/> with a German
    /// message when no model is installed or nothing was recognized.
    public async Task<TranscriptionResult> TranscribeAsync(float[] samples16kMono, string language, CancellationToken ct = default)
    {
        var result = await TranscribeAllowingEmptyAsync(samples16kMono, language, ct).ConfigureAwait(false);
        if (result.IsEmpty) throw TranscriptionException.NoText();
        return result;
    }

    /// Like <see cref="TranscribeAsync"/>, but returns an empty result instead of throwing
    /// when there was no speech.
    public async Task<TranscriptionResult> TranscribeAllowingEmptyAsync(float[] samples16kMono, string language, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(samples16kMono);
        var transcriber = await GetTranscriberAsync(ct).ConfigureAwait(false);
        var padding = samples16kMono.Length <= MaximumPaddedSeconds * 16_000 ? (int)(PaddingSeconds * 16_000) : 0;
        var input = padding > 0 ? Pad(samples16kMono, padding) : samples16kMono;
        var result = await transcriber.TranscribeAsync(input, language, ct).ConfigureAwait(false);

        var text = HallucinationFilter.RemovingTrailingCredits(result.Text);
        var shift = -padding / 16_000.0;
        var duration = samples16kMono.Length / 16_000.0;
        var words = padding == 0
            ? result.Words
            : result.Words.Select(w => new TimedWord(w.Text, Math.Clamp(w.Start + shift, 0, duration), Math.Clamp(w.End + shift, 0, duration))).ToList();
        return result with { Text = text, Words = words };
    }

    /// Frees the model's memory; the next transcription loads it again.
    public void Unload()
    {
        ISpeechTranscriber? loaded;
        lock (_lock)
        {
            loaded = _loaded;
            _loaded = null;
        }
        if (loaded is null) return;
        // Disposing waits for a running transcription; never block the caller (the UI) on it.
        _ = Task.Run(loaded.Dispose);
        StateChanged?.Invoke();
    }

    /// Call after a model was deleted or downloaded: unloads what is no longer the active model.
    public void UnloadIfStale()
    {
        var active = ActiveModel?.Id;
        string? loadedId;
        lock (_lock) loadedId = _loaded?.ModelId;
        if (loadedId is not null && loadedId != active) Unload();
    }

    /// Unloads the model if it is loaded and deletes its files.
    public void DeleteModel(ModelInfo model)
    {
        string? loadedId;
        lock (_lock) loadedId = _loaded?.ModelId;
        if (loadedId == model.Id)
        {
            ISpeechTranscriber? loaded;
            lock (_lock)
            {
                loaded = _loaded;
                _loaded = null;
            }
            // The files must be closed before they can go (Windows locks open files).
            loaded?.Dispose();
            StateChanged?.Invoke();
        }
        _store.Delete(model);
    }

    private static float[] Pad(float[] samples, int padding)
    {
        var padded = new float[samples.Length + 2 * padding];
        samples.CopyTo(padded, padding);
        return padded;
    }

    private static ISpeechTranscriber DefaultLoader(ModelInfo model, ModelStore store) => model.Engine switch
    {
        ModelEngine.Parakeet => ParakeetTranscriber.Load(store.ModelDirectory(model), model.Id),
        ModelEngine.Whisper => WhisperTranscriber.Load(store.FilePath(model, model.Files[0]), model.Id),
        _ => throw new ArgumentException($"{model.Id} is no transcription model.", nameof(model)),
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ISpeechTranscriber? loaded;
        lock (_lock)
        {
            loaded = _loaded;
            _loaded = null;
        }
        loaded?.Dispose();
    }
}
