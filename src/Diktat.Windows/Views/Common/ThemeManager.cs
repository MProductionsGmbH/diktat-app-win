using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace Diktat.Windows.Views;

/// The Diktat look (tokens of DiktatTokens.swift) in light and dark, following the Windows app
/// theme ("Apps: hell/dunkel") live. Brushes live in the application resources under the keys
/// "Diktat.*"; XAML uses them with DynamicResource so a theme switch repaints everything.
public static class ThemeManager
{
    public const string ThemeUri = "pack://application:,,,/Diktat;component/Themes/Theme.xaml";

    private static bool _initialized;
    private static readonly List<WeakReference<Window>> Windows = new();

    public static bool IsDark { get; private set; }

    /// Raised on the UI thread after the theme switched.
    public static event EventHandler? Changed;

    /// Makes sure Theme.xaml is merged and the brushes match the Windows theme. Idempotent;
    /// every Diktat window calls it before InitializeComponent. The runtime may call it at startup.
    public static void EnsureInitialized()
    {
        if (_initialized) return;
        var app = Application.Current;
        if (app is null) return;
        _initialized = true;

        if (!HasThemeDictionary(app.Resources))
        {
            try
            {
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(ThemeUri, UriKind.Absolute) });
            }
            catch (Exception ex)
            {
                Platform.Log.Warn("Theme.xaml could not be merged", ex);
            }
        }

        IsDark = ReadAppsUseDarkTheme();
        ApplyPalette(app.Resources, IsDark);

        try
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
        catch (Exception ex)
        {
            Platform.Log.Warn("Theme change notifications unavailable", ex);
        }
    }

    private static bool HasThemeDictionary(ResourceDictionary resources)
    {
        if (resources.Contains("Diktat.ThemeMarker")) return true;
        foreach (var merged in resources.MergedDictionaries)
        {
            if (HasThemeDictionary(merged)) return true;
        }
        return false;
    }

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)) return;
        var app = Application.Current;
        if (app is null) return;
        app.Dispatcher.BeginInvoke(() =>
        {
            var dark = ReadAppsUseDarkTheme();
            if (dark == IsDark) return;
            IsDark = dark;
            ApplyPalette(app.Resources, dark);
            lock (Windows)
            {
                foreach (var weak in Windows.ToList())
                {
                    if (weak.TryGetTarget(out var window)) ApplyWindowFrame(window);
                    else Windows.Remove(weak);
                }
            }
            Changed?.Invoke(null, EventArgs.Empty);
        });
    }

    /// HKCU\...\Themes\Personalize\AppsUseLightTheme: 0 means dark. Missing means light.
    public static bool ReadAppsUseDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    // --- Palette -------------------------------------------------------------------------

    private static readonly (string Key, uint Light, uint Dark)[] Palette =
    [
        // Surfaces (ARGB)
        ("Diktat.Page", 0xFFF5F5F5, 0xFF050722),
        ("Diktat.Sidebar", 0xFFEDEDF1, 0xFF0A0C2B),
        ("Diktat.Card", 0xFFFFFFFF, 0xFF0E102F),
        ("Diktat.CardEdge", 0x17000000, 0x14FFFFFF),
        ("Diktat.Inset", 0xFFF3F3F5, 0xFF191B46),
        ("Diktat.Hover", 0x0F000000, 0x17FFFFFF),
        ("Diktat.Pressed", 0x1C000000, 0x26FFFFFF),
        ("Diktat.Popup", 0xFFFFFFFF, 0xFF13153A),
        // Text
        ("Diktat.Text", 0xFF111111, 0xFFFFFFFF),
        ("Diktat.TextSecondary", 0xFF5C5C5C, 0xFFBABCD4),
        ("Diktat.TextTertiary", 0xFF737373, 0xFF8E91B2),
        // Lines
        ("Diktat.Border", 0xFFCFCECD, 0xFF4F4D69),
        ("Diktat.Stroke", 0x1F000000, 0x26FFFFFF),
        // Actions
        ("Diktat.Primary", 0xFF3E41F4, 0xFF8337A8),
        ("Diktat.PrimaryHover", 0xFF2F33E3, 0xFF9243B9),
        ("Diktat.PrimaryPressed", 0xFF0B0EC1, 0xFF5F287B),
        ("Diktat.OnPrimary", 0xFFFFFFFF, 0xFFFFFFFF),
        ("Diktat.Accent", 0xFF3E41F4, 0xFFBB84DF),
        ("Diktat.AccentSoft", 0x1F3E41F4, 0x2EBB84DF),
        ("Diktat.FocusRing", 0xFF3E41F4, 0xFFBB84DF),
        ("Diktat.Tonal", 0x4270A5FF, 0x42BB84DF),
        ("Diktat.OnTonal", 0xFF0B0EC1, 0xFFFFFFFF),
        // Feedback
        ("Diktat.Success", 0xFF5BDFBD, 0xFF5BDFBD),
        ("Diktat.SuccessText", 0xFF3E41F4, 0xFF5BDFBD),
        ("Diktat.SuccessSoft", 0x245BDFBD, 0x295BDFBD),
        ("Diktat.Error", 0xFFD94C76, 0xFFD94C76),
        ("Diktat.ErrorText", 0xFFB3305A, 0xFFEC8AA7),
        ("Diktat.ErrorSoft", 0x1FD94C76, 0x2ED94C76),
        ("Diktat.Warning", 0xFFB86E00, 0xFFF2B35B),
        // Mode tints (day, night, evening accent families)
        ("Diktat.Mode.Day", 0xFF3E41F4, 0xFF70A5FF),
        ("Diktat.Mode.DaySoft", 0x243E41F4, 0x2E70A5FF),
        ("Diktat.Mode.Night", 0xFF8337A8, 0xFFBB84DF),
        ("Diktat.Mode.NightSoft", 0x248337A8, 0x2EBB84DF),
        ("Diktat.Mode.Evening", 0xFF593AB6, 0xFFA592DD),
        ("Diktat.Mode.EveningSoft", 0x24593AB6, 0x2EA592DD),
        // Keys of shortcut badges, scroll bars
        ("Diktat.Key", 0xFFF3F3F5, 0xFF191B46),
        ("Diktat.KeyStroke", 0x2B000000, 0x33FFFFFF),
        ("Diktat.ScrollThumb", 0x47000000, 0x52FFFFFF),
        ("Diktat.Recording", 0xFFD94C76, 0xFFD94C76),
    ];

    private static void ApplyPalette(ResourceDictionary resources, bool dark)
    {
        foreach (var (key, light, darkValue) in Palette)
        {
            var brush = new SolidColorBrush(FromArgb(dark ? darkValue : light));
            brush.Freeze();
            resources[key] = brush;
        }
    }

    private static Color FromArgb(uint v) =>
        Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);

    public static Color ColorOf(string key) =>
        Application.Current?.TryFindResource(key) is SolidColorBrush brush ? brush.Color : Colors.Gray;

    // --- Window frame (DWM) -----------------------------------------------------------

    /// Dark or light title bar (following the theme, also when it switches) and rounded corners
    /// on Windows 11. Safe to call before or after the window has its handle.
    public static void RegisterWindow(Window window, bool roundCorners = true)
    {
        void Apply(object? s, EventArgs e)
        {
            ApplyWindowFrame(window);
            if (roundCorners) Dwm.SetCornerPreference(window, Dwm.CornerRound);
        }

        lock (Windows) Windows.Add(new WeakReference<Window>(window));
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Apply(null, EventArgs.Empty);
        else window.SourceInitialized += Apply;
    }

    private static void ApplyWindowFrame(Window window) => Dwm.SetDarkTitleBar(window, IsDark);
}

/// Desktop Window Manager attributes for the Windows 11 look; all calls fail silently on
/// systems that do not know them (Windows 10).
internal static class Dwm
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    public const int CornerRound = 2;
    public const int CornerRoundSmall = 3;
    private const int DWMSBT_MAINWINDOW = 2; // Mica

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left, Right, Top, Bottom;
    }

    private static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;
    private static bool SupportsBackdrop => Environment.OSVersion.Version.Build >= 22621;

    private static IntPtr Handle(Window window) => new WindowInteropHelper(window).Handle;

    public static void SetDarkTitleBar(Window window, bool dark)
    {
        try
        {
            var hwnd = Handle(window);
            if (hwnd == IntPtr.Zero) return;
            int value = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
        catch
        {
            // dwmapi missing or attribute unknown
        }
    }

    public static void SetCornerPreference(Window window, int preference)
    {
        if (!IsWindows11) return;
        try
        {
            var hwnd = Handle(window);
            if (hwnd == IntPtr.Zero) return;
            int value = preference;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref value, sizeof(int));
        }
        catch
        {
        }
    }

    /// Mica as the system backdrop. Only on Windows 11 22H2 and later; returns whether it is on.
    public static bool TryEnableMica(Window window)
    {
        if (!SupportsBackdrop || SystemParameters.HighContrast) return false;
        try
        {
            var hwnd = Handle(window);
            if (hwnd == IntPtr.Zero) return false;
            int type = DWMSBT_MAINWINDOW;
            if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int)) != 0) return false;
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            if (DwmExtendFrameIntoClientArea(hwnd, ref margins) != 0) return false;
            if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target }) target.BackgroundColor = Colors.Transparent;
            window.Background = System.Windows.Media.Brushes.Transparent;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
