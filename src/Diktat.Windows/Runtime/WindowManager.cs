using System.Windows;
using System.Windows.Interop;
using Diktat.Windows.Views;
using WpfApplication = System.Windows.Application;
using Point = System.Windows.Point;

namespace Diktat.Windows.Runtime;

/// Creates Diktat's windows once and brings them back to the front. UI thread only.
public sealed class WindowManager
{
    private readonly IDiktatApp _app;
    private MainWindow? _main;
    private SetupAssistantWindow? _setup;
    private TrayPanelWindow? _trayPanel;
    private DateTime _trayPanelHiddenAt;

    public WindowManager(IDiktatApp app)
    {
        _app = app;
    }

    public void ShowMainWindow(string? page)
    {
        if (_main is null)
        {
            _main = new MainWindow(_app);
            _main.Closed += (_, _) => _main = null;
        }
        _main.ShowPage(page);
        BringToFront(_main);
    }

    public void ShowSetupAssistant()
    {
        if (_setup is null)
        {
            _setup = new SetupAssistantWindow(_app);
            _setup.Closed += (_, _) => _setup = null;
        }
        BringToFront(_setup);
    }

    /// Opens the tray panel near <paramref name="anchor"/> (DIPs), or closes it when it is open.
    public void ToggleTrayPanel(Point anchor)
    {
        if (_trayPanel is { IsVisible: true })
        {
            _trayPanel.Hide();
            return;
        }
        // The click on the tray icon took the focus and the panel hid itself just now: that
        // click meant "close".
        if (DateTime.UtcNow - _trayPanelHiddenAt < TimeSpan.FromMilliseconds(350)) return;
        ShowTrayPanel(anchor);
    }

    public void ShowTrayPanel(Point anchor)
    {
        if (_trayPanel is null)
        {
            _trayPanel = new TrayPanelWindow(_app);
            _trayPanel.Closed += (_, _) => _trayPanel = null;
            _trayPanel.IsVisibleChanged += (_, e) =>
            {
                if (e.NewValue is false) _trayPanelHiddenAt = DateTime.UtcNow;
            };
        }
        _trayPanel.ShowNear(anchor);
    }

    /// Main window and setup assistant: the text of a dictation never goes there.
    public bool IsContentWindow(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && (Handle(_main) == hwnd || Handle(_setup) == hwnd);

    public void CloseAll()
    {
        foreach (Window window in WpfApplication.Current?.Windows.Cast<Window>().ToList() ?? [])
        {
            try
            {
                window.Close();
            }
            catch (Exception ex)
            {
                Platform.Log.Warn("Closing a window failed", ex);
            }
        }
        _main = null;
        _setup = null;
        _trayPanel = null;
    }

    private static IntPtr Handle(Window? window) => window is null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;

    private static void BringToFront(Window window)
    {
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        // Windows only lets the foreground app take the focus; topmost for a moment brings the
        // window up when Diktat was in the background (tray, second start).
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }
}
