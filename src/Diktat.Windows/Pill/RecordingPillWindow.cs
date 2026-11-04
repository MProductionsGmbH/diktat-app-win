using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Diktat.Core.Settings;
using Diktat.Windows.Runtime;
using Brushes = System.Windows.Media.Brushes;
using WpfCursors = System.Windows.Input.Cursors;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfMouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace Diktat.Windows.Pill;

/// The borderless, transparent, always-on-top window of the pill. It never takes the focus
/// (WS_EX_NOACTIVATE, no activation on click), stays out of Alt+Tab (WS_EX_TOOLWINDOW) and lets
/// every click through (WS_EX_TRANSPARENT) unless the pill can be clicked; even then only the
/// capsule itself catches the mouse, the transparent rest passes clicks on.
public sealed class RecordingPillWindow : Window
{
    private readonly PillVisual _visual = new();
    private IntPtr _hwnd;
    private bool _clickable;
    private bool _animating;
    private DateTime _stateStarted = DateTime.UtcNow;
    private int _transition;

    /// The capsule was clicked while it is clickable.
    public event EventHandler? PillClicked;

    public RecordingPillWindow()
    {
        Title = "Diktat Aufnahme";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        SizeToContent = SizeToContent.Manual;
        Width = PillLayout.PanelWidth;
        Height = PillLayout.PanelHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // Created on the primary screen, where it is placed: no DPI change on the first show.
        Left = 0;
        Top = 0;
        Content = _visual;
        _visual.Opacity = 0;
        _visual.MouseLeftButtonUp += OnMouseUp;
        _visual.MouseMove += OnMouseMove;
        SourceInitialized += (_, _) => OnSourceInitialized();
    }

    internal PillVisual Visual => _visual;
    public PillState State => _visual.State;

    private void OnSourceInitialized()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        RuntimeNative.SetExtendedStyle(_hwnd,
            RuntimeNative.WS_EX_NOACTIVATE | RuntimeNative.WS_EX_TOOLWINDOW | (_clickable ? 0 : RuntimeNative.WS_EX_TRANSPARENT),
            _clickable ? RuntimeNative.WS_EX_TRANSPARENT : 0);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == RuntimeNative.WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(RuntimeNative.MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    /// Switches the content; <paramref name="clickable"/> lets the capsule catch clicks.
    public void SetState(PillState state, bool clickable)
    {
        if (_visual.State != state) _stateStarted = DateTime.UtcNow;
        _visual.State = state;
        _clickable = clickable;
        if (_hwnd != IntPtr.Zero)
        {
            RuntimeNative.SetExtendedStyle(_hwnd, clickable ? 0 : RuntimeNative.WS_EX_TRANSPARENT, clickable ? RuntimeNative.WS_EX_TRANSPARENT : 0);
        }
        _visual.ToolTip = state switch
        {
            PillState.Recording => "Klicken stoppt die Aufnahme",
            PillState.Meeting => "Meeting läuft. Klicken öffnet die Meeting-Seite.",
            _ => null,
        };
        _visual.InvalidateVisual();
    }

    /// Shows the window (placing it first when asked) and fades the pill in.
    public void ShowPill(bool place, PillPosition position)
    {
        _transition++;
        if (!IsVisible)
        {
            Show();
            place = true;
        }
        if (place)
        {
            Place(position);
            // A DPI change on the way (another screen scale) resizes the window; place it again after.
            Dispatcher.BeginInvoke(() => Place(position), System.Windows.Threading.DispatcherPriority.Background);
        }
        StartAnimation();
        _visual.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
    }

    /// Fades out and hides, unless something was shown again meanwhile.
    public void HidePill()
    {
        if (!IsVisible) return;
        int transition = ++_transition;
        SetState(_visual.State, clickable: false);
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
        fade.Completed += (_, _) =>
        {
            if (transition != _transition) return;
            StopAnimation();
            _visual.State = PillState.Hidden;
            Hide();
        };
        _visual.BeginAnimation(OpacityProperty, fade);
    }

    /// Along the bottom of the primary screen's work area, above the taskbar, in physical pixels.
    private void Place(PillPosition position)
    {
        if (_hwnd == IntPtr.Zero) return;
        var (area, scale) = RuntimeNative.PrimaryWorkArea();
        var (x, y, width, height) = PillLayout.Placement(area.Left, area.Top, area.Right, area.Bottom, scale, position);
        RuntimeNative.SetWindowPos(_hwnd, IntPtr.Zero, x, y, width, height, RuntimeNative.SWP_NOACTIVATE | RuntimeNative.SWP_NOZORDER);
    }

    private void StartAnimation()
    {
        if (_animating) return;
        _animating = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopAnimation()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        _visual.Time = (DateTime.UtcNow - _stateStarted).TotalSeconds;
        _visual.Advance();
    }

    private void OnMouseMove(object sender, WpfMouseEventArgs e)
    {
        _visual.Cursor = _clickable && _visual.CapsuleRect.Contains(e.GetPosition(_visual)) ? WpfCursors.Hand : null;
    }

    private void OnMouseUp(object sender, WpfMouseButtonEventArgs e)
    {
        if (_clickable && _visual.CapsuleRect.Contains(e.GetPosition(_visual))) PillClicked?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnClosed(EventArgs e)
    {
        StopAnimation();
        base.OnClosed(e);
    }
}
