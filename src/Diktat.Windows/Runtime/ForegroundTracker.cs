using System.Runtime.InteropServices;
using Diktat.Windows.Platform;

namespace Diktat.Windows.Runtime;

/// Remembers the last window in front that is neither Diktat's nor part of the taskbar, so a
/// dictation started from the tray panel can go into the window that was in front before
/// (the Mac's manual start pastes into the app that was active before the menu opened).
///
/// Uses an out-of-context WinEvent hook; create it on the UI thread, whose message loop
/// delivers the events.
public sealed class ForegroundTracker : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, char[] className, int maxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    /// Windows of the shell that come to the front when the tray icon is clicked.
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland", "XamlExplorerHostIslandWindow",
        "Windows.UI.Core.CoreWindow", "Progman", "WorkerW",
    };

    private readonly WinEventProc _callback; // kept alive while hooked
    private IntPtr _hook;
    private IntPtr _last;

    public ForegroundTracker()
    {
        _callback = OnForeground;
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _callback, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (_hook == IntPtr.Zero) Log.Warn("Foreground hook not available");
        Remember(ForegroundAppInfo.Current()?.Window ?? IntPtr.Zero);
    }

    /// The last outside window that was in front and still exists, or null.
    public ForegroundApp? LastExternalApp =>
        _last != IntPtr.Zero && IsWindow(_last) ? ForegroundAppInfo.FromWindow(_last) : null;

    private void OnForeground(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0) return; // OBJID_WINDOW
        try
        {
            Remember(hwnd);
        }
        catch (Exception ex)
        {
            Log.Warn("Foreground tracking failed", ex);
        }
    }

    private void Remember(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var root = GetAncestor(hwnd, 2); // GA_ROOT
        if (root != IntPtr.Zero) hwnd = root;
        if (IsShellWindow(hwnd)) return;
        var app = ForegroundAppInfo.FromWindow(hwnd);
        if (app is null || app.IsOwnProcess) return;
        _last = hwnd;
    }

    /// The taskbar, the tray overflow, Start, search or the desktop.
    public static bool IsShellWindow(IntPtr hwnd)
    {
        var buffer = new char[128];
        int length = GetClassName(hwnd, buffer, buffer.Length);
        return length > 0 && ShellClasses.Contains(new string(buffer, 0, length));
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }
}
