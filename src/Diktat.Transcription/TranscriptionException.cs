namespace Diktat.Transcription;

public enum TranscriptionError
{
    /// No transcription model is installed.
    NoModel,
    /// The model folder lacks files.
    ModelIncomplete,
    /// The model files are there but could not be loaded.
    LoadFailed,
    /// The model heard nothing that counts as speech.
    NoText,
}

/// A transcription problem with a German message for the UI (wording of the macOS app).
public sealed class TranscriptionException(TranscriptionError error, string message, Exception? inner = null) : Exception(message, inner)
{
    public TranscriptionError Error { get; } = error;

    public const string NoModelMessage = "Kein Sprachmodell geladen. Bitte in den Einstellungen unter Modelle eines laden.";
    public const string NoTextMessage = "Das lokale Modell hat keinen Text erkannt.";

    public static string IncompleteMessage(string modelName) => $"Das geladene Modell ist unvollständig: {modelName}";

    public static string LoadFailedMessage(string modelName) =>
        $"Das Sprachmodell {modelName} ließ sich nicht starten. Bitte in den Einstellungen unter Modelle löschen und neu laden.";

    public static TranscriptionException NoModel() => new(TranscriptionError.NoModel, NoModelMessage);

    public static TranscriptionException NoText() => new(TranscriptionError.NoText, NoTextMessage);
}
