using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Diktat.Core.Settings;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using Size = System.Windows.Size;

namespace Diktat.Windows.Views.Controls;

/// The round mode icon: the glyph in the mode's tint on a soft circle of the same tint.
public sealed class ModeIcon : Border
{
    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(WorkflowType), typeof(ModeIcon),
        new PropertyMetadata(WorkflowType.TapTalk, (d, _) => ((ModeIcon)d).Update()));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(ModeIcon),
        new PropertyMetadata(34.0, (d, _) => ((ModeIcon)d).Update()));

    private readonly TextBlock _glyph = new()
    {
        FontFamily = IconFont,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public ModeIcon()
    {
        Child = _glyph;
        Focusable = false;
        Update();
    }

    public WorkflowType Mode
    {
        get => (WorkflowType)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    private void Update()
    {
        Width = Height = Size;
        CornerRadius = new CornerRadius(Size / 2);
        _glyph.FontSize = Math.Round(Size * 0.44);
        _glyph.Text = Ui.Glyph(Mode);
        _glyph.SetResourceReference(TextBlock.ForegroundProperty, Ui.TintKey(Mode));
        SetResourceReference(BackgroundProperty, Ui.SoftTintKey(Mode));
    }
}

/// "Strg + Win + Alt" as separate key caps.
public sealed class HotkeyBadge : StackPanel
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(HotkeyBadge),
        new PropertyMetadata("", (d, _) => ((HotkeyBadge)d).Update()));

    public HotkeyBadge()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    private void Update()
    {
        Children.Clear();
        System.Windows.Automation.AutomationProperties.SetName(this, Label ?? "");
        if (string.IsNullOrWhiteSpace(Label)) return;
        var keys = Label.Split(" + ", StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < keys.Length; i++)
        {
            var text = new TextBlock { Text = keys[i], FontSize = 11, FontWeight = FontWeights.SemiBold };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Diktat.TextSecondary");
            var cap = new Border
            {
                Child = text,
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(7, 3, 7, 3),
                Margin = new Thickness(i == 0 ? 0 : 3, 0, 0, 0),
            };
            cap.SetResourceReference(BackgroundProperty, "Diktat.Key");
            cap.SetResourceReference(Border.BorderBrushProperty, "Diktat.KeyStroke");
            Children.Add(cap);
        }
    }
}

/// Live level bars: the recent input levels (0...1) as rounded vertical bars, newest right.
/// While <see cref="IsActive"/> and visible, it samples <see cref="Level"/> 20 times a second.
public sealed class LevelBars : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(float), typeof(LevelBars), new PropertyMetadata(0f));

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(LevelBars),
        new PropertyMetadata(false, (d, _) => ((LevelBars)d).UpdateTimer()));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(LevelBars),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarCountProperty = DependencyProperty.Register(
        nameof(BarCount), typeof(int), typeof(LevelBars),
        new FrameworkPropertyMetadata(24, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly Queue<float> _history = new();
    private readonly System.Windows.Threading.DispatcherTimer _timer;

    public LevelBars()
    {
        SetResourceReference(FillProperty, "Diktat.Accent");
        IsHitTestVisible = false;
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => Push(Level);
        IsVisibleChanged += (_, _) => UpdateTimer();
        Unloaded += (_, _) => _timer.Stop();
        Loaded += (_, _) => UpdateTimer();
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private void UpdateTimer()
    {
        if (IsActive && IsVisible && IsLoaded)
        {
            if (!_timer.IsEnabled) _timer.Start();
        }
        else
        {
            _timer.Stop();
            if (!IsActive) Reset();
        }
    }

    public float Level
    {
        get => (float)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public int BarCount
    {
        get => (int)GetValue(BarCountProperty);
        set => SetValue(BarCountProperty, value);
    }

    /// Clears the bars (e.g. when a recording ends).
    public void Reset()
    {
        _history.Clear();
        InvalidateVisual();
    }

    private void Push(float level)
    {
        _history.Enqueue(Math.Clamp(float.IsFinite(level) ? level : 0, 0, 1));
        while (_history.Count > Math.Max(1, BarCount)) _history.Dequeue();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? BarCount * 5 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 24 : availableSize.Height);

    protected override void OnRender(DrawingContext dc)
    {
        var count = Math.Max(1, BarCount);
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0 || Fill is null) return;
        var slot = w / count;
        var bar = Math.Max(2, slot * 0.55);
        var levels = _history.ToArray();
        for (int i = 0; i < count; i++)
        {
            int index = levels.Length - count + i;
            var level = index >= 0 ? levels[index] : 0f;
            var barHeight = Math.Max(bar, h * (0.08 + 0.92 * Math.Sqrt(level)));
            var x = i * slot + (slot - bar) / 2;
            var y = (h - barHeight) / 2;
            dc.DrawRoundedRectangle(Fill, null, new Rect(x, y, bar, barHeight), bar / 2, bar / 2);
        }
    }
}
