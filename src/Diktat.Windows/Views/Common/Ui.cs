using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Diktat.Core.History;
using Diktat.Core.Settings;
using Diktat.Transcription.Models;
using Diktat.Windows.Platform;
using Diktat.Windows.Runtime;
using Application = System.Windows.Application;

namespace Diktat.Windows.Views;

/// Small helpers the windows share.
public static class Ui
{
    /// Dates and numbers follow the language of the texts.
    public static CultureInfo Culture => L.Culture;

    /// Runs <paramref name="action"/> on the UI thread (now when already there). Service events
    /// (history, meetings, model downloads) may come from worker threads.
    public static void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }

    /// Always queues, so the caller finishes first (debounced UI refreshes).
    public static void PostLater(Action action) =>
        Application.Current?.Dispatcher.BeginInvoke(action, DispatcherPriority.Background);

    /// Opens a web page, mailto: or ms-settings: link with the shell.
    public static void Open(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open {target}", ex);
        }
    }

    public static void OpenFolder(string path)
    {
        try
        {
            AppPaths.Ensure(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open folder {path}", ex);
        }
    }

    /// A yes/no question; true for yes. Without an owner window it is shown on its own.
    public static bool Confirm(DependencyObject? owner, string message, string title)
    {
        var window = owner is null ? null : Window.GetWindow(owner);
        var result = window is not null
            ? System.Windows.MessageBox.Show(window, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : System.Windows.MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    public static void Inform(DependencyObject? owner, string message, string title, bool warning = false)
    {
        var window = owner is null ? null : Window.GetWindow(owner);
        var image = warning ? MessageBoxImage.Warning : MessageBoxImage.Information;
        if (window is not null) System.Windows.MessageBox.Show(window, message, title, MessageBoxButton.OK, image);
        else System.Windows.MessageBox.Show(message, title, MessageBoxButton.OK, image);
    }

    // --- Modes --------------------------------------------------------------------------

    /// Segoe Fluent Icons / MDL2 glyph per mode.
    public static string Glyph(WorkflowType mode) => mode switch
    {
        WorkflowType.TapTalk => "",       // Microphone
        WorkflowType.LocalTapTalk => "",  // Lock
        WorkflowType.TextImprover => "",  // Edit
        WorkflowType.DampfAblassen => "", // LightningBolt
        WorkflowType.EmojiText => "",     // Emoji2
        _ => "",
    };

    /// The accent family of the mode, as in the Mac app: Diktat day, WTF night, :-) evening.
    public static string TintKey(WorkflowType mode) => mode switch
    {
        WorkflowType.DampfAblassen => "Diktat.Mode.Night",
        WorkflowType.TextImprover or WorkflowType.EmojiText => "Diktat.Mode.Evening",
        _ => "Diktat.Mode.Day",
    };

    public static string SoftTintKey(WorkflowType mode) => TintKey(mode) + "Soft";

    /// The name the user gave the mode, or its default name.
    public static string DisplayName(SettingsContainer settings, WorkflowType mode)
    {
        var custom = mode switch
        {
            WorkflowType.TextImprover => settings.TextImprovement.CustomName,
            WorkflowType.DampfAblassen => settings.DampfAblassen.CustomName,
            WorkflowType.EmojiText => settings.EmojiText.CustomName,
            _ => "",
        };
        custom = (custom ?? "").Trim();
        return custom.Length > 0 ? custom : mode.DisplayName();
    }

    public static string HotkeyLabel(SettingsContainer settings, WorkflowType mode) =>
        settings.App.HotkeyFor(mode)?.Label(GlobalHotkeyService.KeyName) ?? "";

    /// The subtitle of a mode row: why it is unavailable, the local model for Diktat, else the slogan.
    public static string Subtitle(IDiktatApp app, WorkflowType mode)
    {
        if (!app.IsModeAvailable(mode)) return app.ModeUnavailableReason(mode) ?? mode.Subtitle();
        if (mode is WorkflowType.TapTalk or WorkflowType.LocalTapTalk)
        {
            var model = ModelCatalog.TranscriptionModelOrDefault(app.Services.Settings.App.TranscriptionModel);
            return L.F("Lokal: {0}.", ShortModelName(model));
        }
        return mode.Subtitle();
    }

    public static string ShortModelName(ModelInfo model) => model.DisplayName.Replace(" (empfohlen)", "").Replace(" (recommended)", "");

    // --- Text ---------------------------------------------------------------------------

    /// "gerade eben", "vor 5 Minuten", "vor 2 Stunden", "gestern", "vor 3 Tagen".
    public static string RelativeTime(DateTimeOffset moment, DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.Now;
        var span = current - moment;
        if (span < TimeSpan.FromMinutes(1)) return L.T("gerade eben");
        if (span < TimeSpan.FromHours(1))
        {
            var m = (int)span.TotalMinutes;
            return m == 1 ? L.T("vor 1 Minute") : L.F("vor {0} Minuten", m);
        }
        if (span < TimeSpan.FromDays(1))
        {
            var h = (int)span.TotalHours;
            return h == 1 ? L.T("vor 1 Stunde") : L.F("vor {0} Stunden", h);
        }
        var days = (int)Math.Round(span.TotalDays);
        return days <= 1 ? L.T("gestern") : L.F("vor {0} Tagen", days);
    }

    public static string DayTitle(HistoryDay day) => day.Kind switch
    {
        HistoryDayKind.Today => L.T("Heute"),
        HistoryDayKind.Yesterday => L.T("Gestern"),
        _ => day.Date.ToString(L.Current == UiLanguage.English ? "dddd, MMMM d, yyyy" : "dddd, d. MMMM yyyy", Culture),
    };

    public static string Clock(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";

    public static string Number(int value) => value.ToString("N0", Culture);

    /// "unter 1 Min., 3 Personen".
    public static string MeetingSummaryLine(Diktat.AI.Meetings.MeetingRecord record)
    {
        var minutes = (int)Math.Round(record.Duration / 60);
        var duration = minutes < 1 ? L.T("unter 1 Min.") : L.F("{0} Min.", minutes);
        var count = record.Speakers.Count;
        var people = count == 1 ? L.T("1 Person") : L.F("{0} Personen", count);
        return count == 0 ? duration : $"{duration}, {people}";
    }

    public static string RetentionHint(HistoryRetention retention) => retention switch
    {
        HistoryRetention.Day => L.T("24 Stunden"),
        HistoryRetention.Week => L.T("7 Tage"),
        _ => "",
    };
}
