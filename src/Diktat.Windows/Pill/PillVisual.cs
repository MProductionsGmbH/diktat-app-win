using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using FlowDirection = System.Windows.FlowDirection;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfPen = System.Windows.Media.Pen;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace Diktat.Windows.Pill;

/// Draws the pill: a lilac capsule at the bottom with voice bars, the wave while writing, a check,
/// the clipboard sign or the meeting clock, and the label above it. Everything else stays
/// transparent, so clicks there go to the window below.
internal sealed class PillVisual : FrameworkElement
{
    // DiktatTokens: night accent (tertiary) to night primary, error, and the night surface for the label.
    private static readonly WpfBrush LilacFill = Frozen(new LinearGradientBrush(
        WpfColor.FromRgb(0xBB, 0x84, 0xDF), WpfColor.FromRgb(0x83, 0x37, 0xA8), new WpfPoint(0, 0), new WpfPoint(1, 1)));
    private static readonly WpfBrush ErrorFill = Frozen(new SolidColorBrush(WpfColor.FromRgb(0xD9, 0x4C, 0x76)));
    private static readonly WpfBrush LabelFill = Frozen(new SolidColorBrush(WpfColor.FromArgb(0xEB, 0x0E, 0x10, 0x2F)));
    private static readonly WpfBrush White = Brushes.White;
    private static readonly WpfBrush WhiteSoft = Frozen(new SolidColorBrush(WpfColor.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)));
    private static readonly WpfBrush Warning = Frozen(new SolidColorBrush(WpfColor.FromRgb(0xFF, 0x8F, 0xA8)));
    private static readonly WpfBrush RecordingDot = Frozen(new SolidColorBrush(WpfColor.FromRgb(0xFF, 0x4D, 0x5E)));
    private static readonly WpfPen CheckPen = FrozenPen(new WpfPen(Brushes.White, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
    private static readonly WpfPen ClipboardPen = FrozenPen(new WpfPen(Brushes.White, 1.4) { LineJoin = PenLineJoin.Round });
    private static readonly Typeface LabelFace = MakeTypeface(FontWeights.SemiBold);

    private readonly double[] _levels = new double[PillLayout.BarCount];
    private readonly double[] _shown = new double[PillLayout.BarCount];
    private DateTime _lastFrame = DateTime.UtcNow;

    public PillState State { get; set; } = PillState.Hidden;
    public string? Hint { get; set; }
    public bool HintIsWarning { get; set; }
    public bool Silent { get; set; }
    public TimeSpan MeetingElapsed { get; set; }
    /// Seconds since the state began, for the wave and the pulse.
    public double Time { get; set; }

    public PillVisual()
    {
        Width = PillLayout.PanelWidth;
        Height = PillLayout.PanelHeight;
        SnapsToDevicePixels = true;
        Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 12,
            ShadowDepth = 2,
            Direction = 270,
            Opacity = 0.28,
            Color = Colors.Black,
        };
    }

    /// The capsule, for hit tests (DIPs inside this element).
    public Rect CapsuleRect
    {
        get
        {
            double width = PillLayout.CapsuleWidth(State);
            return new Rect((PillLayout.PanelWidth - width) / 2, PillLayout.PanelHeight - PillLayout.BottomMargin - PillLayout.PillHeight,
                width, PillLayout.PillHeight);
        }
    }

    /// The newest level goes in on the right, like a seismograph.
    public void PushLevel(float level)
    {
        Array.Copy(_levels, 1, _levels, 0, _levels.Length - 1);
        _levels[^1] = PillLayout.BarHeight(level, Silent);
    }

    public void ResetLevels()
    {
        Array.Fill(_levels, PillLayout.BarWidth);
        Array.Fill(_shown, PillLayout.BarWidth);
    }

    /// One animation frame: the bars ease towards their level (about 80 ms).
    public void Advance()
    {
        var now = DateTime.UtcNow;
        double dt = Math.Clamp((now - _lastFrame).TotalSeconds, 0, 0.1);
        _lastFrame = now;
        double k = 1 - Math.Exp(-dt / 0.04);
        for (int i = 0; i < _shown.Length; i++)
        {
            double target = Silent ? PillLayout.BarWidth : _levels[i];
            _shown[i] += (target - _shown[i]) * k;
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (State == PillState.Hidden) return;
        var capsule = CapsuleRect;
        double radius = capsule.Height / 2;
        dc.DrawRoundedRectangle(State == PillState.Error ? ErrorFill : LilacFill, null, capsule, radius, radius);
        var center = new WpfPoint(capsule.X + capsule.Width / 2, capsule.Y + capsule.Height / 2);

        switch (State)
        {
            case PillState.Recording:
                DrawBars(dc, center, i => _shown[i], Silent ? Warning : White);
                break;
            case PillState.Processing:
                DrawBars(dc, center, i => PillLayout.ThinkingBarHeight(Time, i), WhiteSoft);
                break;
            case PillState.Done:
                DrawCheck(dc, center);
                break;
            case PillState.Copied:
                DrawClipboard(dc, center);
                break;
            case PillState.Error:
                DrawExclamation(dc, center);
                break;
            case PillState.Meeting:
                DrawMeeting(dc, capsule, center);
                break;
        }

        if (!string.IsNullOrEmpty(Hint)) DrawHint(dc, Hint, capsule.Y - PillLayout.HintSpacing);
    }

    private static void DrawBars(DrawingContext dc, WpfPoint center, Func<int, double> height, WpfBrush brush)
    {
        int count = PillLayout.BarCount;
        double total = count * PillLayout.BarWidth + (count - 1) * PillLayout.BarSpacing;
        double x = center.X - total / 2;
        for (int i = 0; i < count; i++)
        {
            double h = Math.Max(PillLayout.BarWidth, height(i));
            var rect = new Rect(x, center.Y - h / 2, PillLayout.BarWidth, h);
            dc.DrawRoundedRectangle(brush, null, rect, PillLayout.BarWidth / 2, PillLayout.BarWidth / 2);
            x += PillLayout.BarWidth + PillLayout.BarSpacing;
        }
    }

    private static void DrawCheck(DrawingContext dc, WpfPoint c)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new WpfPoint(c.X - 5, c.Y + 0.5), false, false);
            g.LineTo(new WpfPoint(c.X - 1.5, c.Y + 4), true, true);
            g.LineTo(new WpfPoint(c.X + 5.5, c.Y - 4), true, true);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, CheckPen, geometry);
    }

    private static void DrawClipboard(DrawingContext dc, WpfPoint c)
    {
        dc.DrawRoundedRectangle(null, ClipboardPen, new Rect(c.X - 5, c.Y - 5.5, 10, 12), 1.6, 1.6);
        dc.DrawRoundedRectangle(White, null, new Rect(c.X - 2.5, c.Y - 7, 5, 3), 1, 1);
    }

    private static void DrawExclamation(DrawingContext dc, WpfPoint c)
    {
        dc.DrawRoundedRectangle(White, null, new Rect(c.X - 1.1, c.Y - 6, 2.2, 8), 1.1, 1.1);
        dc.DrawEllipse(White, null, new WpfPoint(c.X, c.Y + 4.6), 1.3, 1.3);
    }

    private void DrawMeeting(DrawingContext dc, Rect capsule, WpfPoint center)
    {
        var text = Text(PillLayout.Clock(MeetingElapsed), 11.5, WhiteSoft);
        double total = 7 + 7 + text.Width;
        double x = center.X - total / 2;
        dc.PushOpacity(PillLayout.MeetingPulse(Time));
        dc.DrawEllipse(RecordingDot, null, new WpfPoint(x + 3.5, center.Y), 3.5, 3.5);
        dc.Pop();
        dc.DrawText(text, new WpfPoint(x + 14, center.Y - text.Height / 2));
    }

    private void DrawHint(DrawingContext dc, string hint, double bottom)
    {
        var text = Text(hint, 12, HintIsWarning ? Warning : White);
        text.MaxTextWidth = PillLayout.PanelWidth - 40;
        text.MaxLineCount = 1;
        text.Trimming = TextTrimming.CharacterEllipsis;
        double width = Math.Min(PillLayout.PanelWidth - 12, text.Width + 28);
        var rect = new Rect((PillLayout.PanelWidth - width) / 2, bottom - PillLayout.HintHeight, width, PillLayout.HintHeight);
        dc.DrawRoundedRectangle(LabelFill, null, rect, rect.Height / 2, rect.Height / 2);
        dc.DrawText(text, new WpfPoint(rect.X + (rect.Width - text.Width) / 2, rect.Y + (rect.Height - text.Height) / 2));
    }

    private FormattedText Text(string text, double size, WpfBrush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override WpfSize MeasureOverride(WpfSize availableSize) => new(PillLayout.PanelWidth, PillLayout.PanelHeight);

    private static Typeface MakeTypeface(FontWeight weight)
    {
        try
        {
            var family = new WpfFontFamily(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Public Sans");
            return new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal, new WpfFontFamily("Segoe UI"));
        }
        catch (Exception)
        {
            return new Typeface(new WpfFontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal);
        }
    }

    private static WpfBrush Frozen(WpfBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static WpfPen FrozenPen(WpfPen pen)
    {
        pen.Freeze();
        return pen;
    }
}
