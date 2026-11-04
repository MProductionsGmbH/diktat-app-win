using System.Text.Json.Serialization;

namespace Diktat.Core.Settings;

// The settings of the app. Names and JSON keys follow the macOS app, so a settings export
// from the Mac ("Einstellungen exportieren") can be imported on Windows. Keys that only make
// sense on the Mac (bundle IDs, fn-key shortcuts) are ignored on import.

/// The modes. The raw JSON values are those of the macOS app.
public enum WorkflowType
{
    TapTalk,
    LocalTapTalk,
    TextImprover,
    DampfAblassen,
    EmojiText,
}

public static class WorkflowTypeInfo
{
    /// The modes shown in the tray window. KI-Schreibstil is a cleanup level of Diktat now and
    /// remains as a mode only for rules per app.
    public static readonly WorkflowType[] MainMenuCases = [WorkflowType.TapTalk, WorkflowType.DampfAblassen, WorkflowType.EmojiText];

    public static string DisplayName(this WorkflowType type) => type switch
    {
        WorkflowType.TapTalk => "Diktat",
        WorkflowType.LocalTapTalk => "Diktat Lokal",
        WorkflowType.TextImprover => "Diktat KI-Schreibstil",
        WorkflowType.DampfAblassen => "Diktat WTF",
        WorkflowType.EmojiText => "Diktat :-)",
        _ => type.ToString(),
    };

    public static string Subtitle(this WorkflowType type) => type switch
    {
        WorkflowType.TapTalk or WorkflowType.LocalTapTalk => "Sprache rein. Text raus.",
        WorkflowType.TextImprover => "Gesprochen rein. Geschrieben raus.",
        WorkflowType.DampfAblassen => "Frust rein. Entspannt raus.",
        WorkflowType.EmojiText => "Text rein. Emojis dazu.",
        _ => "",
    };

    /// Whether the mode needs a language model after the transcription.
    public static bool UsesLanguageModel(this WorkflowType type) =>
        type is WorkflowType.TextImprover or WorkflowType.DampfAblassen or WorkflowType.EmojiText;
}

public enum HotkeyMode
{
    /// Hold the keys to record, let go to stop.
    Hold,
    /// Press once to start, again (or Escape) to stop.
    Toggle,
}

public enum SpeechCleanupLevel
{
    /// Hesitation sounds, stutters and phrases said twice, in plain string code.
    Light,
    /// Plus restarts and self-corrections, by the language model. Wording stays.
    Medium,
    /// Plus grammar, word order and punctuation, by the language model.
    Strong,
    /// Written style: the model rewrites the wording in the chosen tone.
    Style,
}

public enum HistoryRetention
{
    Off,
    Day,
    Week,
}

public enum PillPosition
{
    Left,
    Center,
    Right,
}

/// Where KI-Schreibstil, WTF and :-) run their AI step. Transcription is always local.
public enum RewriteEngine
{
    /// A model server on this PC (Ollama, LM Studio) when one is set up, otherwise the provider.
    Automatic,
    /// Always the connected provider.
    Provider,
}

public enum TextTone
{
    Formal,
    Neutral,
    Casual,
}

public enum EmojiDensity
{
    Wenig,
    Mittel,
    Viel,
}

/// Where the dictated text goes, so it can be written in the tone that suits it.
public enum StyleContext
{
    Personal,
    Work,
    Email,
    Other,
}

/// How the text reads: the sentence mark it ends with and, with KI-Schreibstil, the wording.
public enum StyleTone
{
    Formal,
    Neutral,
    Casual,
}

/// A fixed rule: whenever <see cref="Spoken"/> appears as whole words, write <see cref="Written"/>.
public sealed record TermReplacement
{
    [JsonPropertyName("spoken")] public string Spoken { get; init; } = "";
    [JsonPropertyName("written")] public string Written { get; init; } = "";

    public TermReplacement() { }

    public TermReplacement(string spoken, string written)
    {
        Spoken = spoken;
        Written = written;
    }
}

/// A shortcut on Windows: modifier keys plus an optional main key (a virtual-key code).
/// Without a main key the shortcut is the modifiers alone, held together (like Ctrl + Win).
public sealed record KeyCombo
{
    [JsonPropertyName("ctrl")] public bool Ctrl { get; init; }
    [JsonPropertyName("alt")] public bool Alt { get; init; }
    [JsonPropertyName("shift")] public bool Shift { get; init; }
    [JsonPropertyName("win")] public bool Win { get; init; }
    /// Virtual-key code of the main key, or null for a modifiers-only shortcut.
    [JsonPropertyName("key")] public int? Key { get; init; }

    public bool IsModifierOnly => Key is null;

    public int ModifierCount => (Ctrl ? 1 : 0) + (Alt ? 1 : 0) + (Shift ? 1 : 0) + (Win ? 1 : 0);

    /// "Strg + Win + Alt" style label, in German like the rest of the app.
    public string Label(Func<int, string>? keyName = null)
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Strg");
        if (Win) parts.Add("Win");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Umschalt");
        if (Key is int key) parts.Add(keyName?.Invoke(key) ?? $"0x{key:X2}");
        return string.Join(" + ", parts);
    }

    /// The default shortcuts, held: Diktat Strg + Win, WTF Strg + Win + Alt, :-) Strg + Win + Umschalt.
    public static KeyCombo? DefaultFor(WorkflowType type) => type switch
    {
        WorkflowType.TapTalk => new KeyCombo { Ctrl = true, Win = true },
        WorkflowType.DampfAblassen => new KeyCombo { Ctrl = true, Win = true, Alt = true },
        WorkflowType.EmojiText => new KeyCombo { Ctrl = true, Win = true, Shift = true },
        _ => null,
    };
}

/// "Modus je App": the Diktat shortcut uses another mode in this program.
public sealed record AppModeRule
{
    /// The executable name, lower case, e.g. "outlook.exe".
    [JsonPropertyName("processName")] public string ProcessName { get; init; } = "";
    [JsonPropertyName("appName")] public string AppName { get; init; } = "";
    [JsonPropertyName("mode")] public WorkflowType Mode { get; init; } = WorkflowType.TapTalk;
}

/// The same for a website in a browser, e.g. Gmail. <see cref="Host"/> matches subdomains too.
public sealed record WebsiteModeRule
{
    [JsonPropertyName("host")] public string Host { get; init; } = "";
    [JsonPropertyName("mode")] public WorkflowType Mode { get; init; } = WorkflowType.TapTalk;
}

/// The tone per context, as the user set it.
public sealed class StyleSettings
{
    [JsonPropertyName("tones")] public Dictionary<string, StyleTone> Tones { get; set; } = new();

    public StyleTone ToneFor(StyleContext context) =>
        Tones.TryGetValue(ContextKey(context), out var tone) ? tone : DefaultTone(context);

    public void SetTone(StyleTone tone, StyleContext context) => Tones[ContextKey(context)] = tone;

    public static StyleTone DefaultTone(StyleContext context) => context switch
    {
        StyleContext.Personal => StyleTone.Casual,
        StyleContext.Email => StyleTone.Formal,
        _ => StyleTone.Neutral,
    };

    private static string ContextKey(StyleContext context) => JsonNaming.CamelCase(context.ToString());
}

public sealed class AppSettings
{
    [JsonPropertyName("hotkeyMode")] public HotkeyMode HotkeyMode { get; set; } = HotkeyMode.Hold;
    [JsonPropertyName("hasSeenOnboarding")] public bool HasSeenOnboarding { get; set; }
    [JsonPropertyName("rewriteEngine")] public RewriteEngine RewriteEngine { get; set; } = RewriteEngine.Automatic;
    /// Id of the selected speech model, see Diktat.Transcription.ModelCatalog.
    [JsonPropertyName("windowsTranscriptionModel")] public string TranscriptionModel { get; set; } = "parakeet-tdt-0.6b-v3";
    /// Shortcuts per mode (keyed by the JSON name of the mode). Missing entries use the defaults.
    [JsonPropertyName("windowsHotkeys")] public Dictionary<string, KeyCombo> Hotkeys { get; set; } = new();
    [JsonPropertyName("restoreClipboardAfterPaste")] public bool RestoreClipboardAfterPaste { get; set; } = true;
    [JsonPropertyName("correctCustomTermsEnabled")] public bool CorrectCustomTermsEnabled { get; set; } = true;
    [JsonPropertyName("removeFillerWordsEnabled")] public bool RemoveFillerWordsEnabled { get; set; } = true;
    [JsonPropertyName("speechCleanupLevel")] public SpeechCleanupLevel SpeechCleanupLevel { get; set; } = SpeechCleanupLevel.Light;
    [JsonPropertyName("styleSettings")] public StyleSettings StyleSettings { get; set; } = new();
    [JsonPropertyName("historyRetention")] public HistoryRetention HistoryRetention { get; set; } = HistoryRetention.Day;
    [JsonPropertyName("lastSeenWhatsNewVersion")] public string? LastSeenWhatsNewVersion { get; set; }
    [JsonPropertyName("windowsAppModeRules")] public List<AppModeRule> AppModeRules { get; set; } = new();
    [JsonPropertyName("websiteModeRules")] public List<WebsiteModeRule> WebsiteModeRules { get; set; } = new();
    [JsonPropertyName("fitToSurroundingText")] public bool FitToSurroundingText { get; set; } = true;
    [JsonPropertyName("aiSeesFieldContext")] public bool AiSeesFieldContext { get; set; }
    [JsonPropertyName("muteOutputWhileRecording")] public bool MuteOutputWhileRecording { get; set; } = true;
    [JsonPropertyName("automaticParagraphs")] public bool AutomaticParagraphs { get; set; }
    [JsonPropertyName("showsRecordingIndicator")] public bool ShowsRecordingIndicator { get; set; } = true;
    [JsonPropertyName("pillPosition")] public PillPosition PillPosition { get; set; } = PillPosition.Right;
    [JsonPropertyName("playsStartSound")] public bool PlaysStartSound { get; set; } = true;
    [JsonPropertyName("showsHistoryStatistics")] public bool ShowsHistoryStatistics { get; set; } = true;
    /// The microphone for dictation (its endpoint ID); null follows the Windows default input.
    [JsonPropertyName("windowsMicrophoneId")] public string? MicrophoneId { get; set; }
    [JsonPropertyName("summarizesMeetings")] public bool SummarizesMeetings { get; set; } = true;
    [JsonPropertyName("launchAtLogin")] public bool LaunchAtLogin { get; set; } = true;
    [JsonPropertyName("checksForUpdates")] public bool ChecksForUpdates { get; set; } = true;

    public KeyCombo? HotkeyFor(WorkflowType type) =>
        Hotkeys.TryGetValue(JsonNaming.CamelCase(type.ToString()), out var combo) ? combo : KeyCombo.DefaultFor(type);

    public void SetHotkey(WorkflowType type, KeyCombo? combo)
    {
        var key = JsonNaming.CamelCase(type.ToString());
        if (combo is null) Hotkeys.Remove(key); else Hotkeys[key] = combo;
    }
}

public sealed class TranscriptionSettings
{
    /// "auto": the speech model detects the language and the later steps follow what was spoken.
    [JsonPropertyName("language")] public string Language { get; set; } = "auto";
}

public sealed class TextImprovementSettings
{
    [JsonPropertyName("systemPrompt")] public string SystemPrompt { get; set; } = "";
    [JsonPropertyName("customTerms")] public List<string> CustomTerms { get; set; } = new();
    /// Fixed rules for words that do not sound like their spelling ("n acht n" -> "n8n").
    [JsonPropertyName("replacements")] public List<TermReplacement> Replacements { get; set; } = new();
    [JsonPropertyName("context")] public string Context { get; set; } = "";
    [JsonPropertyName("tone")] public TextTone Tone { get; set; } = TextTone.Neutral;
    [JsonPropertyName("customName")] public string CustomName { get; set; } = "";

    /// Everything the language model must keep spelled exactly: custom terms and the written
    /// side of the replacements, without duplicates (case-insensitive), in order.
    [JsonIgnore]
    public IReadOnlyList<string> ProtectedTerms
    {
        get
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return CustomTerms.Concat(Replacements.Select(r => r.Written)).Where(seen.Add).ToList();
        }
    }
}

public sealed class DampfAblassenSettings
{
    public const string DefaultPrompt = "Du erhältst ein emotional gesprochenes Transkript. Erkenne zuerst das eigentliche Ziel, Anliegen und den wahren Frust der Person. Formuliere daraus eine klare, respektvolle und wirksame Nachricht, mit der die Person ihr Ziel eher erreicht. Bewahre relevante Fakten, konkrete Probleme, Grenzen, Erwartungen und die nötige Dringlichkeit. Entferne Beleidigungen, Drohungen, Sarkasmus, Unterstellungen und unnötige Eskalation. Wenn mehrere Vorwürfe genannt werden, verdichte sie auf die entscheidenden Kernpunkte. Der Ton soll ruhig, menschlich, bestimmt und lösungsorientiert sein. Gib NUR die fertige Nachricht zurück.";

    [JsonPropertyName("systemPrompt")] public string SystemPrompt { get; set; } = DefaultPrompt;
    [JsonPropertyName("customName")] public string CustomName { get; set; } = "";
}

public sealed class EmojiTextSettings
{
    [JsonPropertyName("emojiDensity")] public EmojiDensity EmojiDensity { get; set; } = EmojiDensity.Mittel;
    [JsonPropertyName("customName")] public string CustomName { get; set; } = "";
}

/// Everything that is saved, in the layout of the macOS settings export.
public sealed class SettingsContainer
{
    [JsonPropertyName("app")] public AppSettings App { get; set; } = new();
    [JsonPropertyName("transcription")] public TranscriptionSettings Transcription { get; set; } = new();
    [JsonPropertyName("textImprovement")] public TextImprovementSettings TextImprovement { get; set; } = new();
    [JsonPropertyName("dampfAblassen")] public DampfAblassenSettings DampfAblassen { get; set; } = new();
    [JsonPropertyName("emojiText")] public EmojiTextSettings EmojiText { get; set; } = new();
}

public static class JsonNaming
{
    public static string CamelCase(string name) =>
        string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
