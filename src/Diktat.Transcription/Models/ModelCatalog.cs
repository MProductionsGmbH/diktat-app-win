using System.Globalization;

namespace Diktat.Transcription.Models;

public enum ModelEngine
{
    /// NVIDIA Parakeet TDT via sherpa-onnx.
    Parakeet,
    /// OpenAI Whisper (ggml) via Whisper.net / whisper.cpp.
    Whisper,
    /// Speaker segmentation and embedding models for meetings, via sherpa-onnx.
    SpeakerDiarization,
}

/// One file of a model: where it comes from and how large it must be.
/// <param name="FileName">Name on disk inside the model folder.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the file, or null when not published.</param>
public sealed record ModelFile(string FileName, string Url, long SizeBytes, string? Sha256 = null);

/// A downloadable model.
/// <param name="Id">Stable id, saved in the settings (AppSettings.TranscriptionModel).</param>
/// <param name="DisplayName">German name for the settings and the tray menu; the property gives it in the UI language.</param>
/// <param name="Languages">German note on the languages the model handles; the property gives it in the UI language.</param>
/// <param name="Description">German one-liner for the model list; the property gives it in the UI language.</param>
/// <param name="Recommended">Shown first and preselected.</param>
/// <param name="Attribution">Credit the license requires, or null; the property gives it in the UI language.</param>
public sealed record ModelInfo(
    string Id,
    string DisplayName,
    ModelEngine Engine,
    string Languages,
    string Description,
    IReadOnlyList<ModelFile> Files,
    bool Recommended = false,
    string? PageUrl = null,
    string? Attribution = null)
{
    // The catalog keeps the German texts (also for equality); the properties translate when read,
    // so the texts follow the UI language.
    private readonly string _displayName = DisplayName;
    private readonly string _languages = Languages;
    private readonly string _description = Description;
    private readonly string? _attribution = Attribution;

    public string DisplayName { get => ModelTexts.Shown(_displayName); init => _displayName = value; }
    public string Languages { get => ModelTexts.Shown(_languages); init => _languages = value; }
    public string Description { get => ModelTexts.Shown(_description); init => _description = value; }
    public string? Attribution { get => _attribution is null ? null : ModelTexts.Shown(_attribution); init => _attribution = value; }

    public long SizeBytes => Files.Sum(f => f.SizeBytes);

    /// Rounded size in MB (10^6 bytes) for the UI.
    public int SizeMB => (int)Math.Round(SizeBytes / 1_000_000.0);

    public string SizeLabel => SizeBytes < 1_000_000_000 ? $"{SizeMB} MB"
        : L.Current == UiLanguage.English ? (SizeBytes / 1_000_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
        : $"{SizeBytes / 1_000_000_000.0:0.0} GB".Replace('.', ',');

    /// Whether the model turns speech into text (and can be picked for dictation).
    public bool IsTranscriptionModel => Engine is ModelEngine.Parakeet or ModelEngine.Whisper;

    /// The settings label, like the macOS picker: "Whisper Small (nicht installiert)".
    public string PickerLabel(bool installed) => installed ? DisplayName : L.F("{0} (nicht installiert)", DisplayName);
}

/// The catalog texts in the UI language. The keys must be literals here (the localization test
/// scans for them), so every German text of the catalog has its own arm; others stay as they are.
internal static class ModelTexts
{
    public static string Shown(string german) => german switch
    {
        "Parakeet v3 (empfohlen)" => L.T("Parakeet v3 (empfohlen)"),
        "Whisper Large v3 Turbo (hohe Qualität)" => L.T("Whisper Large v3 Turbo (hohe Qualität)"),
        "Sprecher-Erkennung" => L.T("Sprecher-Erkennung"),
        "Erkennt die Sprache selbst, rund 100 Sprachen." => L.T("Erkennt die Sprache selbst, rund 100 Sprachen."),
        "Erkennt die Sprache selbst: Deutsch, Englisch und 23 weitere europäische Sprachen." => L.T("Erkennt die Sprache selbst: Deutsch, Englisch und 23 weitere europäische Sprachen."),
        "Unabhängig von der Sprache." => L.T("Unabhängig von der Sprache."),
        "Sehr schnell und genau, auch ohne Grafikkarte." => L.T("Sehr schnell und genau, auch ohne Grafikkarte."),
        "Klein und schnell, etwas ungenauer." => L.T("Klein und schnell, etwas ungenauer."),
        "Genau und für viele Sprachen, langsamer als Parakeet." => L.T("Genau und für viele Sprachen, langsamer als Parakeet."),
        "Weniger stark komprimiert: minimal genauer, größer und langsamer." => L.T("Weniger stark komprimiert: minimal genauer, größer und langsamer."),
        "Unterscheidet in Meetings die Stimmen (Person 1, Person 2, ...)." => L.T("Unterscheidet in Meetings die Stimmen (Person 1, Person 2, ...)."),
        "Parakeet TDT 0.6B v3 von NVIDIA, CC BY 4.0 (https://creativecommons.org/licenses/by/4.0/), via sherpa-onnx." => L.T("Parakeet TDT 0.6B v3 von NVIDIA, CC BY 4.0 (https://creativecommons.org/licenses/by/4.0/), via sherpa-onnx."),
        "pyannote segmentation 3.0 (MIT) und WeSpeaker ResNet34 (Apache-2.0), via sherpa-onnx." => L.T("pyannote segmentation 3.0 (MIT) und WeSpeaker ResNet34 (Apache-2.0), via sherpa-onnx."),
        _ => german,
    };
}

/// The models Diktat for Windows offers. URLs are pinned to a repository revision, so a
/// change upstream cannot break the size and checksum checks.
public static class ModelCatalog
{
    public const string ParakeetId = "parakeet-tdt-0.6b-v3";
    public const string WhisperSmallId = "whisper-small";
    public const string WhisperTurboId = "whisper-large-v3-turbo-q5_0";
    public const string WhisperTurboQ8Id = "whisper-large-v3-turbo-q8_0";
    public const string SpeakerDiarizationId = "speaker-diarization";

    /// What a new installation starts with (same id as on the Mac).
    public const string DefaultTranscriptionModelId = ParakeetId;

    // sherpa-onnx export of nvidia/parakeet-tdt-0.6b-v3 by the sherpa-onnx author.
    private const string ParakeetBase = "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/2bda32ec70b097a55adaa07d9a7173915b43cc78/";
    private const string WhisperBase = "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/";
    private const string SegmentationBase = "https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/9403a6902bb58e3d5ae8c7e77c3422de279db2e0/";
    private const string SpeakerEmbeddingBase = "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/";

    private const string WhisperLanguages = "Erkennt die Sprache selbst, rund 100 Sprachen.";

    public static readonly ModelInfo Parakeet = new(
        ParakeetId,
        "Parakeet v3 (empfohlen)",
        ModelEngine.Parakeet,
        "Erkennt die Sprache selbst: Deutsch, Englisch und 23 weitere europäische Sprachen.",
        "Sehr schnell und genau, auch ohne Grafikkarte.",
        [
            new("encoder.int8.onnx", ParakeetBase + "encoder.int8.onnx", 652_184_281, "acfc2b4456377e15d04f0243af540b7fe7c992f8d898d751cf134c3a55fd2247"),
            new("decoder.int8.onnx", ParakeetBase + "decoder.int8.onnx", 11_845_275, "179e50c43d1a9de79c8a24149a2f9bac6eb5981823f2a2ed88d655b24248db4e"),
            new("joiner.int8.onnx", ParakeetBase + "joiner.int8.onnx", 6_355_277, "3164c13fc2821009440d20fcb5fdc78bff28b4db2f8d0f0b329101719c0948b3"),
            new("tokens.txt", ParakeetBase + "tokens.txt", 93_939, "d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d"),
        ],
        Recommended: true,
        PageUrl: "https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3",
        Attribution: "Parakeet TDT 0.6B v3 von NVIDIA, CC BY 4.0 (https://creativecommons.org/licenses/by/4.0/), via sherpa-onnx.");

    public static readonly ModelInfo WhisperSmall = new(
        WhisperSmallId,
        "Whisper Small",
        ModelEngine.Whisper,
        WhisperLanguages,
        "Klein und schnell, etwas ungenauer.",
        [new("ggml-small.bin", WhisperBase + "ggml-small.bin", 487_601_967, "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b")],
        PageUrl: "https://huggingface.co/ggerganov/whisper.cpp");

    public static readonly ModelInfo WhisperTurbo = new(
        WhisperTurboId,
        "Whisper Large v3 Turbo",
        ModelEngine.Whisper,
        WhisperLanguages,
        "Genau und für viele Sprachen, langsamer als Parakeet.",
        [new("ggml-large-v3-turbo-q5_0.bin", WhisperBase + "ggml-large-v3-turbo-q5_0.bin", 574_041_195, "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2")],
        PageUrl: "https://huggingface.co/ggerganov/whisper.cpp");

    public static readonly ModelInfo WhisperTurboQ8 = new(
        WhisperTurboQ8Id,
        "Whisper Large v3 Turbo (hohe Qualität)",
        ModelEngine.Whisper,
        WhisperLanguages,
        "Weniger stark komprimiert: minimal genauer, größer und langsamer.",
        [new("ggml-large-v3-turbo-q8_0.bin", WhisperBase + "ggml-large-v3-turbo-q8_0.bin", 874_188_075, "317eb69c11673c9de1e1f0d459b253999804ec71ac4c23c17ecf5fbe24e259a1")],
        PageUrl: "https://huggingface.co/ggerganov/whisper.cpp");

    /// Optional package for meetings: pyannote segmentation 3.0 plus a WeSpeaker ResNet34
    /// speaker embedding (the embedding pyannote 3.0 itself was built with).
    public static readonly ModelInfo SpeakerDiarization = new(
        SpeakerDiarizationId,
        "Sprecher-Erkennung",
        ModelEngine.SpeakerDiarization,
        "Unabhängig von der Sprache.",
        "Unterscheidet in Meetings die Stimmen (Person 1, Person 2, ...).",
        [
            new(SherpaFiles.Segmentation, SegmentationBase + "model.onnx", 5_992_913, "220ad67ca923bef2fa91f2390c786097bf305bceb5e261d4af67b38e938e1079"),
            new(SherpaFiles.SpeakerEmbedding, SpeakerEmbeddingBase + "wespeaker_en_voxceleb_resnet34_LM.onnx", 26_530_550, "e9848563da86f263117134dfd7ad63c92355b37de492b55e325400c9d9c39012"),
        ],
        PageUrl: "https://k2-fsa.github.io/sherpa/onnx/speaker-diarization/index.html",
        Attribution: "pyannote segmentation 3.0 (MIT) und WeSpeaker ResNet34 (Apache-2.0), via sherpa-onnx.");

    /// Recommended first, then by size.
    public static IReadOnlyList<ModelInfo> All { get; } = [Parakeet, WhisperSmall, WhisperTurbo, WhisperTurboQ8, SpeakerDiarization];

    public static IReadOnlyList<ModelInfo> TranscriptionModels { get; } = All.Where(m => m.IsTranscriptionModel).ToList();

    public static ModelInfo? Find(string? id)
    {
        var trimmed = (id ?? "").Trim();
        return All.FirstOrDefault(m => string.Equals(m.Id, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// The transcription model for a settings value; unknown or empty values give the default.
    public static ModelInfo TranscriptionModelOrDefault(string? id) =>
        Find(id) is { IsTranscriptionModel: true } model ? model : Parakeet;

    /// File names inside the speaker diarization folder.
    public static class SherpaFiles
    {
        public const string Segmentation = "segmentation.onnx";
        public const string SpeakerEmbedding = "speaker-embedding.onnx";
    }
}
