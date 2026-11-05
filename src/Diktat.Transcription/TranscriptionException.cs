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

/// A transcription problem with a message for the UI, in the UI language (wording of the macOS app).
public sealed class TranscriptionException(TranscriptionError error, string message, Exception? inner = null) : Exception(message, inner)
{
    public TranscriptionError Error { get; } = error;

    public static string NoModelMessage => L.T("Kein Sprachmodell geladen. Bitte in den Einstellungen unter Modelle eines laden.");
    public static string NoTextMessage => L.T("Das lokale Modell hat keinen Text erkannt.");

    public static string IncompleteMessage(string modelName) => L.F("Das geladene Modell ist unvollständig: {0}", modelName);

    public static string LoadFailedMessage(string modelName) =>
        L.F("Das Sprachmodell {0} ließ sich nicht starten. Bitte in den Einstellungen unter Modelle löschen und neu laden.", modelName);

    public static TranscriptionException NoModel() => new(TranscriptionError.NoModel, NoModelMessage);

    public static TranscriptionException NoText() => new(TranscriptionError.NoText, NoTextMessage);
}
