using System.Globalization;
using Diktat.AI;
using Diktat.Core.Settings;
using Diktat.Core.Text;
using Diktat.Transcription;
using Diktat.Windows.Platform;

namespace Diktat.Windows.Runtime;

/// The small decisions of the dictation flow, free of Windows and of state, so they can be tested.
public static class DictationRules
{
    public const string RecordingStatus = "Aufnahme läuft ...";
    public const string TranscribingStatus = "Wird transkribiert ...";
    public const string LoadingModelStatus = "Sprachmodell wird geladen ...";
    public const string PolishingStatus = "Text wird geglättet ...";
    public const string PastedStatus = "Eingefügt";
    public const string CopiedStatus = "Text liegt in der Zwischenablage";
    public const string NothingRecognized = NothingRecognizedException.DefaultMessage;
    public const string OnlySilence = "Das Mikrofon liefert nur Stille. Ist das richtige Mikrofon gewählt? Wähle in den Einstellungen unter Mikrofon ein anderes.";
    public const string ClipboardBusy = "Die Zwischenablage war belegt, der Text wurde nicht eingefügt. Du findest ihn im Verlauf.";
    public const string ReplaceFailed = "Ersetzen ging hier nicht. Der wortgenaue Text liegt in der Zwischenablage.";

    /// How long "Done" stays before the app is idle again; a text that was only copied stays longer.
    public static readonly TimeSpan DoneDuration = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan CopiedDuration = TimeSpan.FromSeconds(2.8);
    public static readonly TimeSpan ErrorDuration = TimeSpan.FromSeconds(4);
    /// "Wortgenau statt KI-Text" is offered for this long after an AI mode inserted text.
    public static readonly TimeSpan ReplaceOfferDuration = TimeSpan.FromMinutes(10);

    public static bool IsPlainDictation(WorkflowType mode) => mode is WorkflowType.TapTalk or WorkflowType.LocalTapTalk;

    /// Whether a mode can run: every mode needs a speech model, the AI modes a language model too.
    public static bool IsModeAvailable(WorkflowType mode, bool hasSpeechModel, bool canRewrite) =>
        hasSpeechModel && (!mode.UsesLanguageModel() || canRewrite);

    /// Why a mode cannot run, in German, or null when it can.
    public static string? UnavailableReason(WorkflowType mode, bool hasSpeechModel, bool canRewrite)
    {
        if (!hasSpeechModel) return TranscriptionException.NoModelMessage;
        if (mode.UsesLanguageModel() && !canRewrite) return Diktat.AI.TextRewriterException.NoModelMessage;
        return null;
    }

    /// The mode's name as the user may have renamed it ("Diktat WTF" or their own name).
    public static string DisplayName(SettingsContainer settings, WorkflowType mode)
    {
        var custom = mode switch
        {
            WorkflowType.TextImprover => settings.TextImprovement.CustomName,
            WorkflowType.DampfAblassen => settings.DampfAblassen.CustomName,
            WorkflowType.EmojiText => settings.EmojiText.CustomName,
            _ => "",
        };
        return string.IsNullOrWhiteSpace(custom) ? mode.DisplayName() : custom.Trim();
    }

    /// What the status line says when the text went to the clipboard instead of the field.
    public static string StatusFor(PasteOutcome outcome) => outcome switch
    {
        PasteOutcome.Pasted => PastedStatus,
        PasteOutcome.CopiedTargetElevated => "Text liegt in der Zwischenablage. Das Fenster läuft als Administrator, dort kann Diktat nicht einfügen.",
        PasteOutcome.Failed => ClipboardBusy,
        _ => CopiedStatus,
    };

    /// Spaces and capitals of the text follow the text around the cursor; password fields and
    /// unread fields leave it as it is.
    public static string FitToField(string text, FocusedField? field, bool enabled) =>
        enabled && field is { IsPassword: false } ? InsertionFitter.Fit(text, field.TextBefore, field.TextAfter) : text;

    /// The text around the cursor for an AI mode, only when the user allowed it.
    public static string? AiContext(FocusedField? field, bool allowed) =>
        allowed && field is { IsPassword: false } ? field.AiContext : null;

    /// The first name of an account's display name ("Paul Muster" -> "Paul"), or null.
    public static string? FirstName(string? displayName)
    {
        var first = (displayName ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        // "DOMAIN\user" or "user@domain" is a login, not a name.
        if (first is null || first.Contains('\\') || first.Contains('@')) return null;
        return first;
    }

    /// How many caret steps the text takes in a field (a line break or an emoji is one step).
    public static int CaretSteps(string text) =>
        new StringInfo(text.Replace("\r\n", "\n", StringComparison.Ordinal)).LengthInTextElements;

    /// "2 Std. 3 Min." or "45 Min."
    public static string MeetingDuration(double seconds)
    {
        int minutes = Math.Max(1, (int)Math.Round(seconds / 60));
        return minutes >= 60 ? $"{minutes / 60} Std. {minutes % 60} Min." : $"{minutes} Min.";
    }

    /// The toast after a meeting: length and number of people, never what was said.
    public static string MeetingFinishedText(double durationSeconds, int speakers)
    {
        var people = speakers == 1 ? "1 Person" : $"{speakers} Personen";
        return $"{MeetingDuration(durationSeconds)}, {people}. In Diktat unter Meetings.";
    }
}
