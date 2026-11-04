using Diktat.Transcription.Models;

namespace Diktat.Transcription.Tests;

public class LocalTranscriptionServiceTests
{
    [Fact]
    public async Task WithoutAModelItSaysWhereToGetOne()
    {
        var store = new ModelStore(TestAudio.TempDirectory());
        using var service = new LocalTranscriptionService(store, ModelCatalog.ParakeetId, (_, _) => throw new InvalidOperationException("must not load"));

        Assert.False(service.HasModel);
        Assert.False(service.IsReady);
        var error = await Assert.ThrowsAsync<TranscriptionException>(() => service.TranscribeAsync(new float[16_000], "auto"));
        Assert.Equal(TranscriptionError.NoModel, error.Error);
        Assert.Equal("Kein Sprachmodell geladen. Bitte in den Einstellungen unter Modelle eines laden.", error.Message);
    }

    [Fact]
    public async Task LoadsLazilyOnceAndKeepsTheModel()
    {
        var store = StoreWith(ModelCatalog.WhisperSmall);
        var loads = 0;
        using var service = new LocalTranscriptionService(store, ModelCatalog.WhisperSmallId, (model, _) =>
        {
            Interlocked.Increment(ref loads);
            return new FakeTranscriber(model.Id, "Hallo Welt.");
        });

        Assert.False(service.IsReady);
        await Task.WhenAll(service.WarmUpAsync(), service.WarmUpAsync());
        Assert.True(service.IsReady);
        var first = await service.TranscribeAsync(new float[16_000], "de");
        var second = await service.TranscribeAsync(new float[16_000], "de");

        Assert.Equal(1, loads);
        Assert.Equal("Hallo Welt.", first.Text);
        Assert.Equal("Hallo Welt.", second.Text);
    }

    [Fact]
    public async Task FallsBackToAnInstalledModelAndSwitchesWhenTheSelectionChanges()
    {
        var store = StoreWith(ModelCatalog.WhisperSmall);
        var created = new List<FakeTranscriber>();
        using var service = new LocalTranscriptionService(store, ModelCatalog.ParakeetId, (model, _) =>
        {
            var fake = new FakeTranscriber(model.Id, model.Id);
            created.Add(fake);
            return fake;
        });

        // Parakeet is selected but not on disk: Whisper Small, the installed one, works meanwhile.
        Assert.False(service.IsSelectedModelInstalled);
        Assert.Equal(ModelCatalog.WhisperSmallId, service.ActiveModel?.Id);
        Assert.Equal(ModelCatalog.WhisperSmallId, (await service.TranscribeAsync(new float[100], "auto")).Text);

        Install(store, ModelCatalog.Parakeet);
        service.UnloadIfStale();
        await WaitFor(() => created[0].Disposed);
        Assert.False(service.IsReady);
        Assert.Equal(ModelCatalog.ParakeetId, (await service.TranscribeAsync(new float[100], "auto")).Text);

        service.SelectModel(ModelCatalog.WhisperSmallId);
        await WaitFor(() => created[1].Disposed);
        Assert.Null(service.LoadedModelId);
        Assert.Equal(ModelCatalog.WhisperSmallId, (await service.TranscribeAsync(new float[100], "auto")).Text);
        Assert.Equal(3, created.Count);
    }

    [Fact]
    public async Task EmptyTextIsAnErrorForDictation()
    {
        var store = StoreWith(ModelCatalog.WhisperSmall);
        using var service = new LocalTranscriptionService(store, ModelCatalog.WhisperSmallId, (model, _) => new FakeTranscriber(model.Id, "  "));

        var error = await Assert.ThrowsAsync<TranscriptionException>(() => service.TranscribeAsync(new float[16_000], "auto"));
        Assert.Equal("Das lokale Modell hat keinen Text erkannt.", error.Message);
        Assert.True((await service.TranscribeAllowingEmptyAsync(new float[16_000], "auto")).IsEmpty);
    }

    [Fact]
    public async Task PadsShortRecordingsAndShiftsTheWordTimesBack()
    {
        var store = StoreWith(ModelCatalog.WhisperSmall);
        FakeTranscriber? fake = null;
        using var service = new LocalTranscriptionService(store, ModelCatalog.WhisperSmallId, (model, _) => fake = new FakeTranscriber(model.Id, "Hallo. Untertitel im Auftrag des ZDF, 2021.")
        {
            Words = [new TimedWord("Hallo.", 0.5, 0.9)],
        });

        var result = await service.TranscribeAsync(new float[16_000], "auto");

        Assert.Equal(16_000 + 2 * 4_800, fake!.LastInputLength);
        Assert.Equal("Hallo.", result.Text); // trailing credits go for every engine
        var word = Assert.Single(result.Words);
        Assert.Equal(0.2, word.Start, 6);
        Assert.Equal(0.6, word.End, 6);
    }

    [Fact]
    public async Task ALoadErrorBecomesAGermanMessage()
    {
        var store = StoreWith(ModelCatalog.WhisperSmall);
        using var service = new LocalTranscriptionService(store, ModelCatalog.WhisperSmallId, (_, _) => throw new DllNotFoundException("whisper"));
        var error = await Assert.ThrowsAsync<TranscriptionException>(() => service.WarmUpAsync());
        Assert.Equal(TranscriptionError.LoadFailed, error.Error);
        Assert.StartsWith("Das Sprachmodell Whisper Small ließ sich nicht starten.", error.Message);
    }

    [Fact]
    public async Task DeleteModelUnloadsItFirst()
    {
        var store = StoreWith(ModelCatalog.WhisperSmall);
        FakeTranscriber? fake = null;
        using var service = new LocalTranscriptionService(store, ModelCatalog.WhisperSmallId, (model, _) => fake = new FakeTranscriber(model.Id, "x"));
        await service.WarmUpAsync();

        service.DeleteModel(ModelCatalog.WhisperSmall);

        Assert.True(fake!.Disposed);
        Assert.False(store.IsInstalled(ModelCatalog.WhisperSmall));
        Assert.False(service.HasModel);
    }

    /// Files with the right length (sparse where the file system allows), no real model.
    internal static ModelStore StoreWith(params ModelInfo[] models)
    {
        var store = new ModelStore(TestAudio.TempDirectory());
        foreach (var model in models) Install(store, model);
        return store;
    }

    internal static void Install(ModelStore store, ModelInfo model)
    {
        Directory.CreateDirectory(store.ModelDirectory(model));
        foreach (var file in model.Files)
        {
            using var stream = File.Create(store.FilePath(model, file));
            stream.SetLength(file.SizeBytes);
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}

internal sealed class FakeTranscriber(string modelId, string text) : ISpeechTranscriber
{
    public string ModelId => modelId;
    public bool Disposed { get; private set; }
    public int LastInputLength { get; private set; }
    public IReadOnlyList<TimedWord> Words { get; init; } = [];
    public Func<float[], TranscriptionResult>? Handler { get; init; }

    public Task<TranscriptionResult> TranscribeAsync(float[] samples16kMono, string language, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        LastInputLength = samples16kMono.Length;
        return Task.FromResult(Handler?.Invoke(samples16kMono) ?? new TranscriptionResult(text, TranscriptionLanguage.Code(language), Words));
    }

    public void Dispose() => Disposed = true;
}
