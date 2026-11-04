using System.IO;
using Microsoft.Win32;

namespace Diktat.Windows.Platform;

/// Start with Windows: HKCU\Software\Microsoft\Windows\CurrentVersion\Run, value "Diktat" =
/// "\"C:\…\Diktat.exe\" --background". No admin rights needed. Thread-safe.
public static class AutostartService
{
    public const string ValueName = "Diktat";
    public const string BackgroundArgument = "--background";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    /// Where Task Manager / Settings › Apps › Autostart record "disabled" (first byte odd).
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Diktat.exe");

    public static string CommandLine => $"\"{ExecutablePath}\" {BackgroundArgument}";

    /// The Run entry exists, points to this exe and is not disabled in Task Manager.
    public static bool IsEnabled => IsRegistered && !IsDisabledByUser;

    /// The Run entry exists and points to this exe (it may still be disabled in Task Manager).
    public static bool IsRegistered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(ValueName) is not string value) return false;
            return value.Contains(ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// The user switched Diktat off in Task Manager › Autostart.
    public static bool IsDisabledByUser
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return key?.GetValue(ValueName) is byte[] { Length: > 0 } data && (data[0] & 1) == 1;
        }
    }

    /// Registers this exe. Also lifts a Task Manager "disabled" mark: the user just asked for it.
    public static void Enable()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true))
        {
            key.SetValue(ValueName, CommandLine, RegistryValueKind.String);
        }
        if (IsDisabledByUser)
        {
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// Applies the setting; keeps the path current after the app moved or updated.
    public static void Apply(bool enabled)
    {
        try
        {
            if (enabled) Enable(); else Disable();
        }
        catch (Exception ex)
        {
            Log.Warn("Updating autostart failed", ex);
        }
    }

    /// Whether this process was started by the Run entry (start hidden in the tray).
    public static bool WasStartedInBackground(string[] args) =>
        args.Any(a => string.Equals(a, BackgroundArgument, StringComparison.OrdinalIgnoreCase));
}
