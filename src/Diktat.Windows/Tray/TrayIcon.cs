using System.ComponentModel;
using System.Diagnostics;
using Diktat.Core.Settings;
using Diktat.Windows.Platform;
using Diktat.Windows.Runtime;
using WpfPoint = System.Windows.Point;

namespace Diktat.Windows.Tray;

/// The symbol in the notification area (the Mac's menu bar item): left click opens the tray
/// panel, right click the menu. Shows the state in its tooltip and with a dot on the icon, and
/// tells about finished meetings and updates in a notification.
public sealed class TrayIcon : IDisposable
{
    private readonly DiktatApp _app;
    private readonly NotifyIcon _icon;
    private readonly TrayIconImages _images = new();
    private readonly ContextMenuStrip _menu = new();
    private Action? _balloonClick;
    private string? _text;

    public TrayIcon(DiktatApp app)
    {
        _app = app;
        _icon = new NotifyIcon
        {
            Icon = _images.Idle,
            Text = L.T("Diktat – bereit"),
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseClick += OnMouseClick;
        _icon.BalloonTipClicked += (_, _) => RunBalloonAction();
        _icon.BalloonTipClosed += (_, _) => _balloonClick = null;
        _menu.Opening += (_, e) =>
        {
            BuildMenu();
            e.Cancel = false; // an empty strip starts out cancelled
        };
        _app.PropertyChanged += OnAppChanged;
        _app.MeetingChanged += OnMeetingChanged;
        Refresh();
    }

    /// Shows a notification (a toast on Windows 10 and 11). <paramref name="onClick"/> runs when it is clicked.
    public void ShowBalloon(string title, string text, Action? onClick)
    {
        _balloonClick = onClick;
        _icon.ShowBalloonTip(8_000, title, string.IsNullOrWhiteSpace(text) ? " " : text, ToolTipIcon.None);
    }

    private void RunBalloonAction()
    {
        var action = _balloonClick;
        _balloonClick = null;
        try
        {
            action?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn("Notification action failed", ex);
        }
    }

    private void OnMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        try
        {
            _app.ToggleTrayPanel(CursorInDips());
        }
        catch (Exception ex)
        {
            Log.Error("Opening the tray panel failed", ex);
        }
    }

    /// The pointer position in device-independent pixels of the monitor it is on.
    private static WpfPoint CursorInDips()
    {
        RuntimeNative.GetCursorPos(out var point);
        var monitor = RuntimeNative.MonitorFromPoint(point, RuntimeNative.MONITOR_DEFAULTTONEAREST);
        double scale = RuntimeNative.DpiScale(monitor);
        return new WpfPoint(point.X / scale, point.Y / scale);
    }

    private void OnMeetingChanged(object? sender, EventArgs e) => Refresh();

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DiktatApp.Phase) or nameof(DiktatApp.StatusMessage)) Refresh();
    }

    /// Icon and tooltip for the current state.
    public void Refresh()
    {
        var meeting = _app.CurrentMeeting;
        var (icon, text) = _app.Phase switch
        {
            DictationPhase.Recording => (_images.Recording, L.T("Diktat – Aufnahme läuft")),
            DictationPhase.Processing => (_images.Processing, "Diktat – " + (_app.StatusMessage ?? L.T("wird verarbeitet ..."))),
            DictationPhase.Error => (_images.Idle, "Diktat – " + (_app.StatusMessage ?? L.T("Fehler"))),
            _ when meeting?.Phase == MeetingPhase.Recording => (_images.Recording, L.T("Diktat – Meeting wird aufgenommen")),
            _ when meeting?.Phase == MeetingPhase.Processing => (_images.Processing, L.T("Diktat – Meeting wird ausgewertet")),
            _ => (_images.Idle, L.T("Diktat – bereit")),
        };
        if (_icon.Icon != icon) _icon.Icon = icon;
        // The tooltip takes at most 127 characters; the meeting refreshes often, so only on change.
        if (text.Length > 127) text = text[..126] + "…";
        if (text != _text) _icon.Text = _text = text;
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var settings = _app.Services.Settings;
        _menu.Items.Add(Item(L.T("Diktat öffnen"), () => _app.ShowMainWindow(), bold: true));
        _menu.Items.Add(new ToolStripSeparator());

        if (_app.Phase == DictationPhase.Recording)
        {
            _menu.Items.Add(Item(L.T("Aufnahme beenden"), _app.StopDictation));
            _menu.Items.Add(Item(L.T("Aufnahme verwerfen"), _app.CancelDictation));
        }
        else
        {
            bool busy = _app.Phase == DictationPhase.Processing;
            foreach (var mode in WorkflowTypeInfo.MainMenuCases)
            {
                var combo = settings.App.HotkeyFor(mode);
                var label = L.F("{0} starten", DictationRules.DisplayName(settings, mode));
                if (combo is not null) label += $" ({combo.Label(GlobalHotkeyService.KeyName)})";
                var item = Item(label, () => _app.StartDictation(mode));
                item.Enabled = !busy && _app.IsModeAvailable(mode);
                if (!_app.IsModeAvailable(mode)) item.ToolTipText = _app.ModeUnavailableReason(mode);
                _menu.Items.Add(item);
            }
        }
        _menu.Items.Add(new ToolStripSeparator());

        var meeting = _app.CurrentMeeting;
        if (meeting?.Phase == MeetingPhase.Recording)
        {
            _menu.Items.Add(Item(L.T("Meeting beenden"), _app.StopMeeting));
        }
        else
        {
            var meetingMenu = new ToolStripMenuItem(L.T("Meeting aufnehmen")) { Enabled = meeting?.Phase != MeetingPhase.Processing };
            meetingMenu.DropDownItems.Add(Item(L.T("Online-Meeting (Teams, Zoom, Meet)"), () => StartMeeting(online: true)));
            meetingMenu.DropDownItems.Add(Item(L.T("Meeting im Raum"), () => StartMeeting(online: false)));
            _menu.Items.Add(meetingMenu);
        }
        _menu.Items.Add(Item(L.T("Audiodatei transkribieren …"), _app.ChooseAndTranscribeAudioFile));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item(L.T("Einstellungen"), () => _app.ShowMainWindow("settings")));
        _menu.Items.Add(Item(L.T("Nach Updates suchen"), CheckForUpdates));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item(L.T("Beenden"), _app.Quit));
    }

    private void StartMeeting(bool online)
    {
        _app.StartMeeting(online);
        _app.ShowMainWindow("meetings");
    }

    private async void CheckForUpdates()
    {
        try
        {
            var update = await _app.CheckForUpdatesAsync(CancellationToken.None);
            if (update is null)
            {
                ShowBalloon(L.T("Diktat ist aktuell"), L.F("Version {0} ist die neueste.", AppServices.Version), null);
            }
            else
            {
                var url = update.DownloadUrl;
                ShowBalloon(L.T("Update verfügbar"), L.F("Diktat {0} ist da. Klicken lädt es herunter.", update.Version), () => OpenUrl(url));
            }
        }
        catch (Exception ex)
        {
            ShowBalloon(L.T("Update-Suche fehlgeschlagen"), ex.Message, null);
        }
    }

    private static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private static ToolStripMenuItem Item(string text, Action action, bool bold = false)
    {
        var item = new ToolStripMenuItem(text);
        if (bold) item.Font = new Font(item.Font, FontStyle.Bold);
        item.Click += (_, _) =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Error($"Tray menu action failed: {text}", ex);
            }
        };
        return item;
    }

    public void Dispose()
    {
        _app.PropertyChanged -= OnAppChanged;
        _app.MeetingChanged -= OnMeetingChanged;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _images.Dispose();
    }
}
