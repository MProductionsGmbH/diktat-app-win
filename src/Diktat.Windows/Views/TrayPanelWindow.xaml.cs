using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Diktat.Core.Settings;
using Diktat.Windows.Runtime;
using Diktat.Windows.Views.ViewModels;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;
using Screen = System.Windows.Forms.Screen;

namespace Diktat.Windows.Views;

/// The small window above the tray icon (like the Mac's menu bar window): status, modes, the
/// last text, a few history entries. Borderless with a shadow; hides when it loses focus, so
/// the same instance can be shown again with <see cref="ShowNear"/>.
public partial class TrayPanelWindow : Window
{
    private readonly IDiktatApp _app;
    private readonly TrayPanelViewModel _model;
    private Point? _anchor;

    public TrayPanelWindow(IDiktatApp app)
    {
        ThemeManager.EnsureInitialized();
        _app = app;
        _model = new TrayPanelViewModel(app);
        InitializeComponent();
        DataContext = _model;
        ThemeManager.RegisterWindow(this, roundCorners: false);

        Deactivated += (_, _) => HidePanel();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _model.Attach();
            else _model.Detach();
        };
        SizeChanged += (_, _) => Reposition();
        _model.PropertyChanged += OnModelChanged;
        _model.HideRequested += (_, _) => HidePanel();
        Closed += (_, _) => _model.Detach();
    }

    /// Shows the panel above the tray, near the given screen point (device-independent pixels).
    public void ShowNear(Point anchor)
    {
        _anchor = anchor;
        _model.Attach();
        UpdateMicButton();
        if (!IsVisible)
        {
            // Off screen first, so the panel never flashes at the old place.
            Left = -10000;
            Top = -10000;
            Show();
        }
        Reposition();
        Activate();
        Scroller.ScrollToTop();
        Focus();
    }

    private void HidePanel()
    {
        if (IsVisible) Hide();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HidePanel();
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    // --- Position -----------------------------------------------------------------------

    private void Reposition()
    {
        if (_anchor is not { } anchor || !IsVisible) return;
        var area = WorkAreaNear(anchor);
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : 600;
        const double shadow = 12; // the margin of the panel border
        const double gap = 6;

        double left, top;
        if (anchor.Y >= area.Bottom - 2)
        {
            // Taskbar at the bottom (the usual place): above it, centered on the icon.
            left = anchor.X - width / 2;
            top = area.Bottom - height + shadow - gap;
        }
        else if (anchor.Y <= area.Top + 2)
        {
            left = anchor.X - width / 2;
            top = area.Top - shadow + gap;
        }
        else if (anchor.X >= area.Right - 2)
        {
            left = area.Right - width + shadow - gap;
            top = anchor.Y - height + 40;
        }
        else if (anchor.X <= area.Left + 2)
        {
            left = area.Left - shadow + gap;
            top = anchor.Y - height + 40;
        }
        else
        {
            // The overflow flyout or a point inside the screen: just above the point.
            left = anchor.X - width / 2;
            top = anchor.Y - height - gap;
        }

        Left = Math.Max(area.Left - shadow + gap, Math.Min(left, area.Right - width + shadow - gap));
        Top = Math.Max(area.Top - shadow + gap, Math.Min(top, area.Bottom - height + shadow - gap));
    }

    /// The work area (without the taskbar) of the monitor at the anchor, in DIPs.
    private Rect WorkAreaNear(Point anchor)
    {
        try
        {
            var scale = DpiScale();
            var screen = Screen.FromPoint(new System.Drawing.Point((int)(anchor.X * scale), (int)(anchor.Y * scale)));
            var wa = screen.WorkingArea;
            return new Rect(wa.Left / scale, wa.Top / scale, wa.Width / scale, wa.Height / scale);
        }
        catch
        {
            return SystemParameters.WorkArea;
        }
    }

    private double DpiScale()
    {
        try
        {
            if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target) return target.TransformToDevice.M11;
            new WindowInteropHelper(this).EnsureHandle();
            return VisualTreeHelper.GetDpi(this).DpiScaleX;
        }
        catch
        {
            return 1;
        }
    }

    // --- State ----------------------------------------------------------------------------

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "" or null or nameof(TrayPanelViewModel.Phase) or nameof(TrayPanelViewModel.IsRecording)) UpdateMicButton();
    }

    private void UpdateMicButton()
    {
        if (_model.IsRecording)
        {
            MicGlyph.Text = ""; // Stop
            MicButton.SetResourceReference(BackgroundProperty, "Diktat.Recording");
            MicButton.IsEnabled = true;
        }
        else
        {
            MicGlyph.Text = ""; // Microphone
            MicButton.SetResourceReference(BackgroundProperty, "Diktat.Primary");
            MicButton.IsEnabled = !_model.IsProcessing && _model.CanStartDictation;
        }
    }

    // --- Actions --------------------------------------------------------------------------

    private void OnMicClick(object sender, RoutedEventArgs e)
    {
        if (_model.IsRecording)
        {
            _app.StopDictation();
            return;
        }
        if (!_app.IsModeAvailable(WorkflowType.TapTalk)) return;
        // The target is the window in front: give focus back to it before recording.
        HidePanel();
        _app.StartDictation(WorkflowType.TapTalk);
    }

    private void OnStop(object sender, RoutedEventArgs e) => _app.StopDictation();
    private void OnCancel(object sender, RoutedEventArgs e) => _app.CancelDictation();

    private void OnInsertTranscript(object sender, RoutedEventArgs e)
    {
        HidePanel();
        _app.InsertTranscriptInstead();
    }

    private void OnCopyLast(object sender, RoutedEventArgs e) => _model.CopyLastText();

    private void OnCopyHistory(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is HistoryEntryViewModel entry)
        {
            _app.CopyToClipboard(entry.Text);
            entry.FlashCopied();
        }
    }

    private void OnUpdate(object sender, RoutedEventArgs e)
    {
        if (_app.AvailableUpdate is { } update) Ui.Open(string.IsNullOrEmpty(update.ReleasePageUrl) ? update.DownloadUrl : update.ReleasePageUrl);
        HidePanel();
    }

    private void Navigate(string? page)
    {
        HidePanel();
        _app.ShowMainWindow(page);
    }

    private void OnSetup(object sender, RoutedEventArgs e)
    {
        HidePanel();
        _app.ShowSetupAssistant();
    }

    private void OnMeeting(object sender, RoutedEventArgs e) => Navigate("meetings");
    private void OnSettings(object sender, RoutedEventArgs e) => Navigate("settings");
    private void OnOpenWindow(object sender, RoutedEventArgs e) => Navigate(null);
    private void OnHistory(object sender, RoutedEventArgs e) => Navigate("history");
    private void OnAudioFile(object sender, RoutedEventArgs e) => Navigate("audio");

    private void OnQuit(object sender, RoutedEventArgs e)
    {
        HidePanel();
        _app.Quit();
    }
}
