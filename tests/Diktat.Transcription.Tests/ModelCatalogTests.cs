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
    public void WhisperModelsHaveOneFileAndParakeetHasItsFour()
    {
        Assert.All(ModelCatalog.All.Where(m => m.Engine == ModelEngine.Whisper), m => Assert.Single(m.Files));
        Assert.Equal(
            ["encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt"],
            ModelCatalog.Parakeet.Files.Select(f => f.FileName));
    }
}
