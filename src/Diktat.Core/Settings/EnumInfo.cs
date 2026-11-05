namespace Diktat.Core.Settings;

/// How much the word-for-word dictation (Diktat) tidies up what was said.
public static class SpeechCleanupLevelInfo
{
    public static readonly SpeechCleanupLevel[] AllCases =
        [SpeechCleanupLevel.Light, SpeechCleanupLevel.Medium, SpeechCleanupLevel.Strong, SpeechCleanupLevel.Style];

    public static string DisplayName(this SpeechCleanupLevel level) => level switch
    {
        SpeechCleanupLevel.Light => L.T("Leicht"),
        SpeechCleanupLevel.Medium => L.T("Mittel"),
        SpeechCleanupLevel.Strong => L.T("Stark"),
        _ => L.T("KI-Schreibstil"),
    };

    public static string Detail(this SpeechCleanupLevel level) => level switch
    {
        SpeechCleanupLevel.Light => L.T("Entfernt „ähm“, Stottern und doppelt gesagte Satzteile. Rein regelbasiert, ohne KI."),
        SpeechCleanupLevel.Medium => L.T("Zusätzlich: Wenn du dich verhaspelst, einen Satz neu ansetzt oder dich korrigierst, bleibt nur die richtige Fassung."),
        SpeechCleanupLevel.Strong => L.T("Zusätzlich: Grammatik, Satzstellung und Zeichensetzung werden korrigiert. Deine Wortwahl bleibt."),
        _ => L.T("Ein Sprachmodell schreibt den Text neu: Rechtschreibung, Grammatik, Formulierung und Lesefluss, im Ton, den du unten unter Stil je Kontext wählst. Die Bedeutung bleibt."),
    };

    /// Leicht runs in plain string code (<see cref="Text.TranscriptPostProcessor"/>); the other
    /// levels add a language-model step.
    public static bool UsesLanguageModel(this SpeechCleanupLevel level) => level != SpeechCleanupLevel.Light;
}

public static class HistoryRetentionInfo
{
    public static readonly HistoryRetention[] AllCases = [HistoryRetention.Off, HistoryRetention.Day, HistoryRetention.Week];

    public static string DisplayName(this HistoryRetention retention) => retention switch
    {
        HistoryRetention.Off => L.T("Aus"),
        HistoryRetention.Day => L.T("24 Std."),
        _ => L.T("7 Tage"),
    };

    /// How long an entry is kept; null when the history is off.
    public static TimeSpan? MaxAge(this HistoryRetention retention) => retention switch
    {
        HistoryRetention.Off => null,
        HistoryRetention.Day => TimeSpan.FromHours(24),
        _ => TimeSpan.FromDays(7),
    };
}

/// Where the recording pill sits along the bottom edge of the screen. The middle is often where
/// the text field of a chat or a command line is, so the default is the right side at two thirds
/// of the width.
public static class PillPositionInfo
{
    public static readonly PillPosition[] AllCases = [PillPosition.Left, PillPosition.Center, PillPosition.Right];

    public static string DisplayName(this PillPosition position) => position switch
    {
        PillPosition.Left => L.T("Links"),
        PillPosition.Center => L.T("Mitte"),
        _ => L.T("Rechts"),
    };

    /// The share of the screen width, from the left edge, where the middle of the pill sits.
    public static double Share(this PillPosition position) => position switch
    {
        PillPosition.Left => 1.0 / 3.0,
        PillPosition.Center => 0.5,
        _ => 2.0 / 3.0,
    };

    /// The left edge of a panel of this width on a screen area (e.g. the work area of a monitor),
    /// kept inside the area.
    public static double OriginX(this PillPosition position, double areaLeft, double areaWidth, double panelWidth)
    {
        var wanted = areaLeft + areaWidth * position.Share() - panelWidth / 2;
        var upper = Math.Max(areaLeft, areaLeft + areaWidth - panelWidth);
        return Math.Min(Math.Max(wanted, areaLeft), upper);
    }
}
