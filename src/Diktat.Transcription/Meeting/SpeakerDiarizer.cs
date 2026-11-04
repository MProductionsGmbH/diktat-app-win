using Diktat.Transcription.Models;
using SherpaOnnx;

namespace Diktat.Transcription.Meeting;

/// "Who spoke when" in a recording.
public interface ISpeakerDiarizer
{
    /// Speaker turns in time order, speakers numbered 1, 2, ... by first appearance.
    /// <param name="speakerCount">The number of people when known, otherwise null.</param>
    Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(float[] samples16kMono, int? speakerCount = null, IProgress<double>? progress = null, CancellationToken ct = default);
}

/// Speaker diarization on this PC with sherpa-onnx: pyannote segmentation 3.0 finds who speaks
/// in 10-second windows, a WeSpeaker embedding tells the voices apart, and agglomerative
/// clustering groups them into people. The models are the "Sprecher-Erkennung" package of
/// <see cref="ModelCatalog"/>.
public sealed class SherpaSpeakerDiarizer : ISpeakerDiarizer, IDisposable
{
    /// Cosine distance under which two voices count as one person. Larger gives fewer people.
    public const float DefaultThreshold = 0.5f;

    private readonly string _segmentationModel;
    private readonly string _embeddingModel;
    private readonly float _threshold;
    private readonly int _threads;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OfflineSpeakerDiarization? _engine;
    private int? _engineSpeakerCount;
    private bool _disposed;

    public SherpaSpeakerDiarizer(string segmentationModelPath, string embeddingModelPath, float threshold = DefaultThreshold, int? threads = null)
    {
        _segmentationModel = segmentationModelPath;
        _embeddingModel = embeddingModelPath;
        _threshold = threshold;
        _threads = threads ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
    }

    /// The diarizer for the installed "Sprecher-Erkennung" package, or null when it is not installed.
    public static SherpaSpeakerDiarizer? FromStore(ModelStore store)
    {
        var model = ModelCatalog.SpeakerDiarization;
        if (!store.IsInstalled(model)) return null;
        return new SherpaSpeakerDiarizer(
            store.FilePath(model, ModelCatalog.SherpaFiles.Segmentation),
            store.FilePath(model, ModelCatalog.SherpaFiles.SpeakerEmbedding));
    }

    public async Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(float[] samples16kMono, int? speakerCount = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(samples16kMono);
        // Shorter than one segmentation step: nobody to tell apart.
        if (samples16kMono.Length < 16_000) return [];
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() => Diarize(samples16kMono, speakerCount, progress, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private IReadOnlyList<SpeakerTurn> Diarize(float[] samples, int? speakerCount, IProgress<double>? progress, CancellationToken ct)
    {
        var engine = Engine(speakerCount);
        OfflineSpeakerDiarizationSegment[] segments;
        if (progress is null)
        {
            segments = engine.Process(samples);
        }
        else
        {
            // Keep the delegate alive while native code calls it.
            OfflineSpeakerDiarizationProgressCallback callback = (done, total, _) =>
            {
                if (total > 0) progress.Report((double)done / total);
                return 0;
            };
            segments = engine.ProcessWithCallback(samples, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
        }
        ct.ThrowIfCancellationRequested();
        return SpeakerAlignment.NormalizeTurns(segments.Select(s => ((double)s.Start, (double)s.End, s.Speaker)));
    }

    private OfflineSpeakerDiarization Engine(int? speakerCount)
    {
        if (_engine is not null && _engineSpeakerCount == speakerCount) return _engine;
        foreach (var file in new[] { _segmentationModel, _embeddingModel })
        {
            if (!File.Exists(file)) throw new FileNotFoundException("Das Modell für die Sprecher-Erkennung fehlt.", file);
        }
        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = _segmentationModel;
        config.Segmentation.NumThreads = _threads;
        config.Embedding.Model = _embeddingModel;
        config.Embedding.NumThreads = _threads;
        config.Clustering.NumClusters = speakerCount is > 0 ? speakerCount.Value : -1;
        config.Clustering.Threshold = _threshold;
        config.MinDurationOn = 0.3f;
        config.MinDurationOff = 0.5f;
        if (_engine is not null)
        {
            _engine.SetConfig(config);
        }
        else
        {
            _engine = new OfflineSpeakerDiarization(config);
        }
        _engineSpeakerCount = speakerCount;
        return _engine;
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            _disposed = true;
            _engine?.Dispose();
            _engine = null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
