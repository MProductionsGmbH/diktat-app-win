using Diktat.Core.Settings;
using Diktat.Transcription.Models;

namespace Diktat.Transcription.Tests;

public class ModelCatalogTests
{
    [Fact]
    public void IdsAreUnique()
    {
        var ids = ModelCatalog.All.Select(m => m.Id.ToLowerInvariant()).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void EveryFileHasAnHttpsUrlSizeAndChecksum()
    {
        foreach (var model in ModelCatalog.All)
        {
            Assert.NotEmpty(model.Files);
            Assert.Equal(model.Files.Count, model.Files.Select(f => f.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            foreach (var file in model.Files)
            {
                Assert.True(Uri.TryCreate(file.Url, UriKind.Absolute, out var uri), file.Url);
                Assert.Equal("https", uri!.Scheme);
                Assert.True(file.SizeBytes > 0);
                Assert.Equal(file.FileName, Path.GetFileName(file.FileName));
                if (file.Sha256 is { } sha) Assert.Matches("^[0-9a-f]{64}$", sha);
            }
        }
    }

    [Fact]
    public void DefaultMatchesTheSettingsDefault()
    {
        Assert.Equal(ModelCatalog.DefaultTranscriptionModelId, new AppSettings().TranscriptionModel);
        Assert.Equal(ModelCatalog.ParakeetId, ModelCatalog.TranscriptionModelOrDefault(null).Id);
        Assert.Equal(ModelCatalog.ParakeetId, ModelCatalog.TranscriptionModelOrDefault("does-not-exist").Id);
        // The diarization package is no dictation model.
        Assert.Equal(ModelCatalog.ParakeetId, ModelCatalog.TranscriptionModelOrDefault(ModelCatalog.SpeakerDiarizationId).Id);
        Assert.Equal(ModelCatalog.WhisperSmallId, ModelCatalog.TranscriptionModelOrDefault(" WHISPER-SMALL ").Id);
    }

    [Fact]
    public void ExactlyOneRecommendedTranscriptionModel()
    {
        var recommended = Assert.Single(ModelCatalog.TranscriptionModels, m => m.Recommended);
        Assert.Equal(ModelEngine.Parakeet, recommended.Engine);
        Assert.Contains("empfohlen", recommended.DisplayName);
    }

    [Fact]
    public void SizesAreSensible()
    {
        Assert.InRange(ModelCatalog.Parakeet.SizeMB, 600, 720);
        Assert.InRange(ModelCatalog.WhisperSmall.SizeMB, 450, 500);
        Assert.InRange(ModelCatalog.SpeakerDiarization.SizeMB, 20, 60);
        Assert.Equal("488 MB", ModelCatalog.WhisperSmall.SizeLabel);
        Assert.Equal("Whisper Small (nicht installiert)", ModelCatalog.WhisperSmall.PickerLabel(false));
    }

    [Fact]
    public void TheFullWhisperModelsAreOffered()
    {
        // The Mac's two extra choices since 0.1.16, as ggml files of whisper.cpp.
        var turbo = ModelCatalog.TranscriptionModelOrDefault("whisper-large-v3-turbo");
        var large = ModelCatalog.TranscriptionModelOrDefault("whisper-large-v3");
        Assert.Equal(ModelCatalog.WhisperTurboFull, turbo);
        Assert.Equal(ModelCatalog.WhisperLarge, large);
        Assert.Equal("ggml-large-v3-turbo.bin", Assert.Single(turbo.Files).FileName);
        Assert.Equal("ggml-large-v3.bin", Assert.Single(large.Files).FileName);
        Assert.Equal("1,6 GB", turbo.SizeLabel);
        Assert.Equal("3,1 GB", large.SizeLabel);
        Assert.All([turbo, large], m => Assert.NotNull(m.Files[0].Sha256));
        // Too big for a PC with little memory.
        Assert.NotNull(turbo.MemoryProblem(4));
        Assert.Null(turbo.MemoryProblem(8));
        Assert.NotNull(large.MemoryProblem(8));
        Assert.Null(large.MemoryProblem(16));
    }

    [Fact]
    public void WhisperModelsHaveOneFileAndParakeetHasItsFour()
    {
        Assert.All(ModelCatalog.All.Where(m => m.Engine == ModelEngine.Whisper), m => Assert.Single(m.Files));
        Assert.Equal(
            ["encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt"],
            ModelCatalog.Parakeet.Files.Select(f => f.FileName));
    }
}
