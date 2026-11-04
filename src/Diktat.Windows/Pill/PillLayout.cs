using Diktat.Core.Settings;
using Diktat.Windows.Runtime;

namespace Diktat.Windows.Pill;

/// What the recording pill shows.
public enum PillState
{
    Hidden,
    /// Voice bars; a click stops the recording.
    Recording,
    /// Bars running in a wave while the text is written.
    Processing,
    /// A short check mark.
    Done,
    /// The text went to the clipboard, not into the field.
    Copied,
    /// Error tint, briefly.
    Error,
    /// A meeting records: red dot and time; a click opens the meeting page.
    Meeting,
}

/// Sizes, placement and motion of the pill (RecordingIndicator.swift, VoiceBarMotion.swift),
/// as pure functions.
public static class PillLayout
{
    /// The window: as wide as the longest label, tall enough for pill, label and shadow (DIPs).
    public const double PanelWidth = 380;
    public const double PanelHeight = 88;
    public const double PillHeight = 26;
    /// Space under the pill, above the taskbar.
    public const double BottomMargin = 8;
    public const double HintHeight = 28;
    public const double HintSpacing = 7;

    public const int BarCount = 11;
    public const double BarWidth = 2.4;
    public const double BarSpacing = 2.4;
    public const double BarMaxHeight = 15;

    /// Below this input level the room counts as quiet: room noise reads about 0.1, speech 0.4 and above.
    public const double VoiceGate = 0.12;

    public static double CapsuleWidth(PillState state) => state switch
    {
        PillState.Recording or PillState.Processing => 78,
        PillState.Meeting => 118,
        _ => 52,
    };

    /// What to show: a dictation comes first, then a running meeting; nothing when switched off.
    public static PillState StateFor(DictationPhase phase, bool onlyCopied, bool meetingRecording, bool enabled)
    {
        if (!enabled) return PillState.Hidden;
        return phase switch
        {
            DictationPhase.Recording => PillState.Recording,
            DictationPhase.Processing => PillState.Processing,
            DictationPhase.Done => onlyCopied ? PillState.Copied : PillState.Done,
            DictationPhase.Error => PillState.Error,
            _ => meetingRecording ? PillState.Meeting : PillState.Hidden,
        };
    }

    /// The window rectangle in physical pixels: along the bottom of the work area, above the
    /// taskbar, at the chosen share of the width.
    public static (int X, int Y, int Width, int Height) Placement(
        int areaLeft, int areaTop, int areaRight, int areaBottom, double scale, PillPosition position)
    {
        if (scale <= 0) scale = 1;
        int width = (int)Math.Round(PanelWidth * scale);
        int height = (int)Math.Round(PanelHeight * scale);
        double x = position.OriginX(areaLeft, areaRight - areaLeft, width);
        int y = Math.Max(areaTop, areaBottom - height);
        return ((int)Math.Round(x), y, width, height);
    }

    /// 0 while it is quiet, up to 1 for a loud voice.
    public static double Voice(float level) => Math.Clamp((level - VoiceGate) / 0.5, 0, 1);

    /// The height of a voice bar: a dot while quiet, up to the full height for a loud voice.
    public static double BarHeight(float level, bool silent) =>
        silent ? BarWidth : BarWidth + (BarMaxHeight - BarWidth) * Voice(level);

    /// The bars while the text is written: a wave running through them.
    public static double ThinkingBarHeight(double seconds, int index) =>
        BarWidth + 7 * (0.5 + 0.5 * Math.Sin(seconds * 5 - index * 0.7));

    /// The red dot of a running meeting pulses once every two seconds.
    public static double MeetingPulse(double seconds) => 0.55 + 0.45 * Math.Abs(Math.Cos(seconds * Math.PI / 2));

    /// "4:05" or, after an hour, "1:02:34".
    public static string Clock(TimeSpan elapsed)
    {
        var total = (int)Math.Max(0, elapsed.TotalSeconds);
        return total >= 3600
            ? $"{total / 3600}:{total % 3600 / 60:00}:{total % 60:00}"
            : $"{total / 60}:{total % 60:00}";
    }

    /// The label above the pill: the microphone while a recording starts, a warning while
    /// nothing comes in, and why a text is only on the clipboard.
    public static string? HintText(PillState state, string? microphone, bool noSignal, bool showsMicrophone)
    {
        switch (state)
        {
            case PillState.Recording:
                var name = microphone ?? "";
                if (noSignal) return name.Length == 0 ? "Kein Ton" : $"Kein Ton ({name})";
                return showsMicrophone && name.Length > 0 ? $"Mikrofon: {name}" : null;
            case PillState.Copied:
                return "Nicht eingefügt, der Text liegt in der Zwischenablage";
            default:
                return null;
        }
    }
}
