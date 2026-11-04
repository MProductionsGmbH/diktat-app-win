using System.IO;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Automation;
using Diktat.Windows.Platform.Native;

namespace Diktat.Windows.Platform;

/// The app in front. <see cref="ExeName"/> is the lower-case file name ("outlook.exe"), what
/// AppModeRule.ProcessName matches. For UWP apps it is the app's own process, not
/// ApplicationFrameHost.exe.
public sealed record ForegroundApp(IntPtr Window, int ProcessId, string ExeName, string Title)
{
    public bool IsOwnProcess => ProcessId == Environment.ProcessId;
    public bool IsBrowser => ForegroundAppInfo.BrowserExeNames.Contains(ExeName);
}

/// Reads the foreground window, its process and, for browsers, the host of the current tab
/// ("Modus je Website"). All members are thread-safe.
public static class ForegroundAppInfo
{
    public static readonly IReadOnlySet<string> BrowserExeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "opera_gx.exe", "vivaldi.exe",
        "arc.exe", "chromium.exe", "zen.exe", "librewolf.exe", "floorp.exe", "waterfox.exe", "thorium.exe",
    };

    private static readonly bool OwnElevation = ComputeOwnElevation();

    public static bool IsCurrentProcessElevated => OwnElevation;

    /// The foreground window (top level), or null when there is none.
    public static ForegroundApp? Current()
    {
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        var root = Win32.GetAncestor(hwnd, Win32.GA_ROOT);
        return FromWindow(root != IntPtr.Zero ? root : hwnd);
    }

    public static ForegroundApp? FromWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return null;
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return null;
        var exe = ExeNameOf((int)pid) ?? "";
        if (exe == "applicationframehost.exe" && UwpChildProcess(hwnd, pid) is uint child)
        {
            pid = child;
            exe = ExeNameOf((int)child) ?? exe;
        }
        return new ForegroundApp(hwnd, (int)pid, exe, WindowTitle(hwnd));
    }

    /// The window with the keyboard focus inside <paramref name="topLevel"/>'s thread (an edit
    /// control in classic apps; the top-level window itself in browsers and most modern apps).
    public static IntPtr FocusedWindow(IntPtr topLevel)
    {
        uint thread = Win32.GetWindowThreadProcessId(topLevel, out _);
        if (thread == 0) return IntPtr.Zero;
        var info = new Win32.GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.GUITHREADINFO>() };
        return Win32.GetGUIThreadInfo(thread, ref info) ? info.hwndFocus : IntPtr.Zero;
    }

    public static string WindowTitle(IntPtr hwnd)
    {
        int length = Win32.GetWindowTextLength(hwnd);
        if (length <= 0) return "";
        var buffer = new char[Math.Min(length + 1, 1024)];
        int read = Win32.GetWindowText(hwnd, buffer, buffer.Length);
        return read > 0 ? new string(buffer, 0, read) : "";
    }

    /// Lower-case exe file name of a process, or null when it cannot be read.
    public static string? ExeNameOf(int processId)
    {
        var handle = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[1024];
            uint size = (uint)buffer.Length;
            if (!Win32.QueryFullProcessImageName(handle, 0, buffer, ref size)) return null;
            return Path.GetFileName(new string(buffer, 0, (int)size)).ToLowerInvariant();
        }
        finally
        {
            Win32.CloseHandle(handle);
        }
    }

    /// Whether the process runs elevated; null when unknown (access denied, gone).
    public static bool? IsElevated(int processId)
    {
        var process = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            if (!Win32.OpenProcessToken(process, Win32.TOKEN_QUERY, out var token)) return null;
            try
            {
                return Win32.GetTokenInformation(token, Win32.TokenElevation, out int elevated, sizeof(int), out _)
                    ? elevated != 0
                    : null;
            }
            finally
            {
                Win32.CloseHandle(token);
            }
        }
        finally
        {
            Win32.CloseHandle(process);
        }
    }

    private static bool ComputeOwnElevation()
    {
        try { return IsElevated(Environment.ProcessId) ?? false; } catch { return false; }
    }

    /// UWP apps are hosted in an ApplicationFrameHost window; the app's CoreWindow is a child
    /// owned by the real process.
    private static uint? UwpChildProcess(IntPtr frame, uint framePid)
    {
        uint? found = null;
        Win32.EnumChildWindows(frame, (child, _) =>
        {
            Win32.GetWindowThreadProcessId(child, out uint pid);
            if (pid != 0 && pid != framePid)
            {
                found = pid;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // ---------------------------------------------------------------- web host

    private static readonly ConcurrentDictionary<(IntPtr Window, string Title), (string? Host, long At)> HostCache = new();
    private static readonly long CacheTicks = Stopwatch.Frequency * 2;

    /// Host of the browser tab in front ("mail.google.com", without "www."), read from the
    /// address bar via UI Automation. Null for non-browsers, when the address bar holds no
    /// address (user typing, new tab) or after <paramref name="timeout"/> (default 300 ms).
    /// Cached per window and title for 2 s (the title changes with the tab).
    public static async Task<string?> GetWebHostAsync(ForegroundApp app, TimeSpan? timeout = null)
    {
        if (!app.IsBrowser || app.IsOwnProcess) return null;
        var key = (app.Window, app.Title);
        long now = Stopwatch.GetTimestamp();
        if (HostCache.TryGetValue(key, out var cached) && now - cached.At < CacheTicks) return cached.Host;

        var host = await UiaRunner.RunAsync(() => ReadAddressBar(app.Window, app.ExeName), timeout).ConfigureAwait(false);
        if (HostCache.Count > 64) HostCache.Clear();
        HostCache[key] = (host, Stopwatch.GetTimestamp());
        return host;
    }

    private static string? ReadAddressBar(IntPtr window, string exe)
    {
        var root = AutomationElement.FromHandle(window);
        if (root is null) return null;

        if (exe is "firefox.exe" or "librewolf.exe" or "floorp.exe" or "waterfox.exe" or "zen.exe")
        {
            var urlbar = root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "urlbar-input"));
            if (urlbar is not null && WebHost.Normalize(ValueOf(urlbar)) is string firefoxHost) return firefoxHost;
        }

        // Chromium: the omnibox is the first edit field in the browser's own UI. Search breadth
        // first and skip web content (documents), which can be huge.
        var walker = TreeWalker.ControlViewWalker;
        var queue = new Queue<(AutomationElement Element, int Depth)>();
        queue.Enqueue((root, 0));
        int visited = 0;
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 4;
        while (queue.Count > 0 && visited < 400 && Stopwatch.GetTimestamp() < deadline)
        {
            var (element, depth) = queue.Dequeue();
            visited++;
            var type = element.Current.ControlType;
            if (type == ControlType.Document)
            {
                // Chromium documents report the page URL as their value.
                if (WebHost.Normalize(ValueOf(element)) is string documentHost) return documentHost;
                continue;
            }
            if (type == ControlType.Edit && WebHost.Normalize(ValueOf(element)) is string host) return host;
            if (depth >= 12) continue;
            var child = walker.GetFirstChild(element);
            while (child is not null)
            {
                queue.Enqueue((child, depth + 1));
                child = walker.GetNextSibling(child);
            }
        }
        return null;
    }

    private static string? ValueOf(AutomationElement element) =>
        element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ? ((ValuePattern)pattern).Current.Value : null;
}

/// Website addresses for "Modus je Website": what people type, what browsers report.
public static class WebHost
{
    /// "https://www.Mail.google.com/mail/u/0" or "mail.google.com/x" → "mail.google.com".
    /// Null for anything that is not a host name (search text, empty, "about:blank").
    public static string? Normalize(string? input)
    {
        var trimmed = input?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Contains(' ')) return null;
        var withScheme = trimmed.Contains("://") ? trimmed : "https://" + trimmed;
        if (!Uri.TryCreate(withScheme, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null;
        var host = uri.Host;
        if (!host.Contains('.') || host.StartsWith('.') || host.EndsWith('.')) return null;
        return host.StartsWith("www.") ? host[4..] : host;
    }

    /// A rule for "google.com" also covers "mail.google.com", not "notgoogle.com".
    public static bool Matches(string host, string rule)
    {
        host = host.ToLowerInvariant();
        rule = rule.ToLowerInvariant();
        return host == rule || host.EndsWith("." + rule, StringComparison.Ordinal);
    }
}
