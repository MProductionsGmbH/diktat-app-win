using System.ComponentModel;
using Diktat.AI.Meetings;
using Diktat.AI.Updates;
using Diktat.Core.Settings;
using Diktat.Windows.Pill;
using Diktat.Windows.Platform;
using Diktat.Windows.Tray;
using WpfApplication = System.Windows.Application;
using WpfPoint = System.Windows.Point;

namespace Diktat.Windows.Runtime;

/// The running app behind the windows (IDiktatApp): dictation, meetings, audio files, updates,
/// tray icon, recording pill and the windows themselves. UI thread only.
public sealed class DiktatApp : ObservableObject, IDiktatApp, IDisposable
{
    private readonly GlobalHotkeyService _hotkeys;
    private readonly ClipboardPasteService _paste;
    private readonly ForegroundTracker _foreground;
    private readonly AudioFileTranscriber _audioFiles;
    private readonly UpdateScheduler _updates;
    private TrayIcon? _tray;
    private PillController? _pill;
    private MeetingSession? _meeting;
    private bool _hotkeysRunning;
    private bool _quitting;
    private bool _disposed;

    public DiktatApp(AppServices services)
    {
        Services = services;
        Windows = new WindowManager(this);
        _hotkeys = new GlobalHotkeyService(services.UiContext);
        _paste = new ClipboardPasteService(services.UiContext);
        _foreground = new ForegroundTracker();
        _audioFiles = new AudioFileTranscriber(services);
        _updates = new UpdateScheduler(services);
        _updates.AvailableUpdateChanged += (_, _) => OnPropertyChanged(nameof(AvailableUpdate));
        _updates.UpdateFound += (_, update) => _tray?.ShowBalloon(
            L.T("Update verfügbar"), L.F("Diktat {0} ist da. Klicken öffnet die Einstellungen.", update.Version), () => ShowMainWindow("settings"));

        Dictation = new DictationController(services, _hotkeys, _paste, _foreground, Windows.IsContentWindow,
            () => _meeting?.RecordsSystemAudio == true);
        Dictation.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
        Dictation.ModeUnavailable += (_, _) => ShowTrayPanel();

        _hotkeys.Pressed += (_, mode) => Dictation.OnShortcutPressed(mode);
        _hotkeys.Released += (_, mode) => Dictation.OnShortcutReleased(mode);
        _hotkeys.Cancelled += (_, _) => Dictation.OnShortcutCancelled();
        _hotkeys.PotentialStart += (_, mode) => Dictation.OnPotentialStart(mode);

        services.SettingsChanged += (_, _) => OnSettingsChanged();
    }

    public AppServices Services { get; }
    internal WindowManager Windows { get; }
    internal DictationController Dictation { get; }

    /// Any property of the current meeting changed, or another meeting became the current one.
    public event EventHandler? MeetingChanged;

    // ------------------------------------------------------------------ startup

    /// Starts everything that runs in the background: shortcuts, tray icon, pill, model warm-up,
    /// update checks. Errors are logged; the app keeps running with what works.
    public void Start()
    {
        StartHotkeys();
        _tray = new TrayIcon(this);
        _pill = new PillController(this);
        _updates.Start();
        MeetingAudio.DeleteOrphans(Services.Meetings.Meetings.Where(m => m.Status == MeetingStatus.Pending).Select(m => m.Id));
        WarmUp();
        if (!_hotkeysRunning)
        {
            _tray.ShowBalloon(L.T("Tastenkürzel nicht verfügbar"),
                L.T("Windows hat die Tastenkürzel nicht zugelassen. Diktat lässt sich über das Symbol in der Taskleiste starten."), null);
        }
    }

    /// Installs the keyboard hook with the shortcuts from the settings. False when Windows refuses.
    public bool StartHotkeys()
    {
        try
        {
            _hotkeys.SetCombos(Services.Settings.App);
            _hotkeys.Start();
            _hotkeysRunning = true;
        }
        catch (Exception ex)
        {
            Log.Error("Keyboard hook could not be installed", ex);
            _hotkeysRunning = false;
        }
        return _hotkeysRunning;
    }

    internal void StopHotkeys()
    {
        _hotkeys.Stop();
        _hotkeysRunning = false;
    }

    /// Whether the setup assistant should open: never finished, or no speech model on this PC.
    public bool NeedsSetup => !Services.Settings.App.HasSeenOnboarding || !Services.Transcription.HasModel;

    private void WarmUp()
    {
        if (Services.Transcription.HasModel)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Services.Transcription.WarmUpAsync();
                    Log.Info("Speech model loaded");
                }
                catch (Exception ex)
                {
                    Log.Warn("Pre-loading the speech model failed", ex);
                }
            });
        }
        _ = FocusedFieldReader.WarmupAsync();
    }

    private void OnSettingsChanged()
    {
        ReloadHotkeys();
        _pill?.SettingsChanged();
        _tray?.Refresh();
        if (Services.Transcription.HasModel && !Services.Transcription.IsReady && !Dictation.IsBusy) WarmUp();
    }

    // ------------------------------------------------------------------ dictation

    public DictationPhase Phase => Dictation.Phase;
    public WorkflowType? ActiveMode => Dictation.ActiveMode;
    public string? StatusMessage => Dictation.StatusMessage;
    public float AudioLevel => Dictation.AudioLevel;
    public bool NoSignal => Dictation.NoSignal;
    public string? LastText => Dictation.LastText;
    public string? LastTranscript => Dictation.LastTranscript;
    public bool LastTextWasOnlyCopied => Dictation.LastTextWasOnlyCopied;

    public bool IsModeAvailable(WorkflowType mode) => Dictation.IsModeAvailable(mode);
    public string? ModeUnavailableReason(WorkflowType mode) => Dictation.ModeUnavailableReason(mode);
    public void StartDictation(WorkflowType mode) => Dictation.StartDictation(mode);
    public void StopDictation() => Dictation.StopDictation();
    public void CancelDictation() => Dictation.CancelDictation();
    public void InsertTranscriptInstead() => Dictation.InsertTranscriptInstead();
    public void CopyToClipboard(string text) => Dictation.CopyToClipboard(text);

    public void ReloadHotkeys()
    {
        _hotkeys.SetCombos(Services.Settings.App);
        _hotkeys.ResetToggle();
        _tray?.Refresh();
    }

    public async Task<KeyCombo?> CaptureShortcutAsync(CancellationToken cancellationToken)
    {
        if (!_hotkeysRunning) return null;
        return await _hotkeys.CaptureNextComboAsync(null, cancellationToken);
    }

    // ------------------------------------------------------------------ meetings

    public IMeetingSession? Meeting => _meeting;
    internal MeetingSession? CurrentMeeting => _meeting;

    private bool MeetingIsBusy => _meeting is { IsBusy: true };

    public void StartMeeting(bool online)
    {
        if (MeetingIsBusy) return;
        var session = NewMeetingSession();
        _ = session.StartAsync(online);
    }

    public void StopMeeting() => _meeting?.Stop();

    public void CancelMeeting() => _meeting?.Cancel();

    public bool HasMeetingAudio(Guid meetingId) => MeetingAudio.Exists(meetingId);

    public void RetryMeeting(Guid meetingId)
    {
        if (MeetingIsBusy || !MeetingAudio.Exists(meetingId)) return;
        var session = NewMeetingSession();
        _ = session.ProcessAsync(meetingId);
    }

    private MeetingSession NewMeetingSession()
    {
        var session = new MeetingSession(Services);
        session.PropertyChanged += (_, _) =>
        {
            if (session == _meeting) MeetingChanged?.Invoke(this, EventArgs.Empty);
        };
        session.Finished += (_, record) =>
        {
            if (record is not null)
            {
                _tray?.ShowBalloon(L.T("Meeting ausgewertet"),
                    DictationRules.MeetingFinishedText(record.Duration, record.Speakers.Count), () => ShowMainWindow("meetings"));
            }
            else
            {
                _tray?.ShowBalloon(L.T("Meeting-Auswertung fehlgeschlagen"), session.ErrorMessage ?? "", () => ShowMainWindow("meetings"));
            }
        };
        _meeting = session;
        OnPropertyChanged(nameof(Meeting));
        MeetingChanged?.Invoke(this, EventArgs.Empty);
        return session;
    }

    public async Task SummarizeMeetingAsync(Guid meetingId, CancellationToken cancellationToken)
    {
        if (Services.Meetings.Meeting(meetingId) is not { } record) return;
        var ownName = DictationRules.FirstName(RuntimeNative.UserDisplayName());
        var result = await Services.MeetingSummaries.SummarizeAsync(record, ownName, null,
            prefersServerOnThisPC: Services.Settings.App.RewriteEngine == RewriteEngine.Provider, cancellationToken);
        Services.Meetings.SetSummary(meetingId, result.Summary, result.Note);
    }

    // ------------------------------------------------------------------ audio files

    public Task<string> TranscribeAudioFileAsync(string path, bool paragraphs, IProgress<string>? status, CancellationToken cancellationToken) =>
        _audioFiles.TranscribeAsync(path, paragraphs, status, cancellationToken);

    /// The tray menu: pick a file, transcribe it, put the text on the clipboard.
    internal async void ChooseAndTranscribeAudioFile()
    {
        var patterns = string.Join(";", AudioFileTranscriber.Extensions.Select(e => "*" + e));
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("Audiodatei transkribieren"),
            Filter = L.T("Audiodateien") + "|" + patterns + "|" + L.T("Alle Dateien") + "|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home
                ? System.IO.Path.Combine(home, "Downloads") : "",
        };
        if (dialog.ShowDialog() != true) return;
        var name = System.IO.Path.GetFileName(dialog.FileName);
        _tray?.ShowBalloon(L.T("Audiodatei wird transkribiert"), name, null);
        try
        {
            var text = await TranscribeAudioFileAsync(dialog.FileName, Services.Settings.App.AutomaticParagraphs, null, CancellationToken.None);
            CopyToClipboard(text);
            _tray?.ShowBalloon(L.T("Audiodatei transkribiert"), L.T("Der Text liegt in der Zwischenablage und im Verlauf."), () => ShowMainWindow("history"));
        }
        catch (Exception ex)
        {
            Log.Warn("Audio file transcription failed", ex);
            _tray?.ShowBalloon(L.T("Audiodatei nicht transkribiert"), ex.Message, null);
        }
    }

    // ------------------------------------------------------------------ updates and windows

    public UpdateInfo? AvailableUpdate => _updates.AvailableUpdate;

    public Task<UpdateInfo?> CheckForUpdatesAsync(CancellationToken cancellationToken) => _updates.CheckAsync(cancellationToken);

    public void ShowMainWindow(string? page = null) => Windows.ShowMainWindow(page);

    public void ShowSetupAssistant() => Windows.ShowSetupAssistant();

    /// Above the tray (bottom right of the primary screen's work area) when not opened by a click.
    public void ShowTrayPanel() => Windows.ShowTrayPanel(TrayAnchor());

    internal void ToggleTrayPanel(WpfPoint anchor) => Windows.ToggleTrayPanel(anchor);

    private static WpfPoint TrayAnchor()
    {
        var (area, scale) = RuntimeNative.PrimaryWorkArea();
        return new WpfPoint((area.Right - 12) / scale, (area.Bottom - 4) / scale);
    }

    /// Ends Diktat: no recording runs on, nothing stays muted, the clipboard gets its content back.
    public void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        Log.Info("Diktat quits");
        Shutdown();
        WpfApplication.Current?.Shutdown();
    }

    /// Starts a new Diktat that waits until this one has ended, then quits this one.
    public void Restart()
    {
        if (_quitting) return;
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, SingleInstance.RestartArgument) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Warn("Could not restart Diktat", ex);
            return;
        }
        Quit();
    }

    /// Everything Quit does except ending the process (also used at logoff).
    internal void Shutdown()
    {
        if (_disposed) return;
        _meeting?.FinishBeforeQuit();
        Dictation.Shutdown();
        try { _paste.SettleNow(); } catch (Exception ex) { Log.Warn("Settling the clipboard failed", ex); }
        Dispose();
        Log.Flush();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _updates.Dispose();
        _pill?.Dispose();
        _pill = null;
        _tray?.Dispose();
        _tray = null;
        try { Windows.CloseAll(); } catch (Exception ex) { Log.Warn("Closing windows failed", ex); }
        _hotkeys.Dispose();
        _foreground.Dispose();
        Dictation.Dispose();
        _paste.Dispose();
        Services.Dispose();
    }
}
