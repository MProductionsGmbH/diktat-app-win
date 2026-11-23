using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Diktat.Windows.Platform;
using Diktat.Windows.Runtime;
using Diktat.Windows.Views.ViewModels;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace Diktat.Windows.Views.Pages;

/// "Meetings": start, the running recording, the saved meetings with summary and transcript.
public partial class MeetingsPage : UserControl, IMainPage
{
    private readonly IDiktatApp _app;
    private readonly MeetingsViewModel _model;
    private readonly DispatcherTimer _timer;
    private IMeetingSession? _watched;
    private bool _shown;
    private int? _renaming;

    public MeetingsPage(IDiktatApp app)
    {
        _app = app;
        _model = new MeetingsViewModel(app);
        InitializeComponent();
        DataContext = _model;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Tick();
    }

    public void OnShown()
    {
        if (_shown) return;
        _shown = true;
        _app.PropertyChanged += OnAppChanged;
        _app.Services.Meetings.Changed += OnMeetingsChanged;
        _app.Services.SettingsChanged += OnSettingsChanged;
        WatchSession();
        _model.OnSourceChanged();
        _model.ReloadList();
        _model.OnSessionChanged();
        UpdateTimer();
    }

    public void OnHidden()
    {
        if (!_shown) return;
        _shown = false;
        _app.PropertyChanged -= OnAppChanged;
        _app.Services.Meetings.Changed -= OnMeetingsChanged;
        _app.Services.SettingsChanged -= OnSettingsChanged;
        SoundTest.Stop();
        if (_watched is not null) _watched.PropertyChanged -= OnSessionChanged;
        _watched = null;
        _timer.Stop();
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IDiktatApp.Meeting) or "" or null) Ui.Post(WatchSession);
    }

    private void WatchSession()
    {
        if (!ReferenceEquals(_watched, _app.Meeting))
        {
            if (_watched is not null) _watched.PropertyChanged -= OnSessionChanged;
            _watched = _app.Meeting;
            if (_watched is not null) _watched.PropertyChanged += OnSessionChanged;
        }
        if (_model.UpdateSession()) UpdateTimer();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Levels and the clock are read by the timer.
        if (e.PropertyName is nameof(IMeetingSession.MicrophoneLevel) or nameof(IMeetingSession.SystemAudioLevel) or nameof(IMeetingSession.Elapsed)) return;
        Ui.Post(() =>
        {
            _model.OnSessionChanged();
            UpdateTimer();
        });
    }

    private void OnMeetingsChanged(object? sender, EventArgs e) => Ui.Post(_model.ReloadList);

    /// The source may have changed elsewhere: the default in the settings, or a call notification.
    private void OnSettingsChanged(object? sender, EventArgs e) => Ui.Post(_model.OnSourceChanged);

    private void UpdateTimer()
    {
        if (_shown && _model.Session is { Phase: not MeetingPhase.Idle }) _timer.Start();
        else _timer.Stop();
    }

    private int _ticks;

    private void Tick()
    {
        if (_model.Session is not { } session) return;
        MicBars.Level = session.MicrophoneLevel;
        SystemBars.Level = session.SystemAudioLevel;
        if (++_ticks % 5 == 0) _model.RaiseLive();
        // The phase may change without an event reaching us in between.
        if (_model.IsLive != (session.Phase == MeetingPhase.Recording) || _model.IsProcessing != (session.Phase == MeetingPhase.Processing))
        {
            _model.OnSessionChanged();
            UpdateTimer();
        }
    }

    // --- Start ------------------------------------------------------------------------

    private void OnStart(object sender, RoutedEventArgs e)
    {
        // The meeting records the sound itself.
        SoundTest.Stop();
        try
        {
            _app.StartMeeting(_model.Source);
        }
        catch (Exception ex)
        {
            Log.Warn("Starting the meeting failed", ex);
            Ui.Inform(this, ex.Message, L.T("Meeting"), warning: true);
        }
        WatchSession();
        _model.OnSessionChanged();
        UpdateTimer();
        Scroller.ScrollToTop();
    }

    private void OnCopyChatNote(object sender, RoutedEventArgs e) => _model.CopyChatNote();

    private void OnOpenMeeting(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MeetingListItem item) ShowMeeting(item.Id);
    }

    /// One meeting, e.g. after a click on the message that it is evaluated. A running recording
    /// or evaluation stays on screen.
    public void ShowMeeting(Guid id)
    {
        HideRename();
        if (_app.Meeting is { Phase: not MeetingPhase.Idle } || _app.Services.Meetings.Meeting(id) is null) return;
        _model.Open(id);
        Scroller.ScrollToTop();
    }

    /// Back to the list of meetings ("Meetings" in the sidebar again).
    public void ShowList()
    {
        if (!_model.IsDetail && !_model.IsFailed) return;
        HideRename();
        _model.Close();
        _model.ReloadList();
        Scroller.ScrollToTop();
    }

    private void OnDeleteMeeting(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MeetingListItem item) return;
        if (ConfirmDelete()) _model.DeleteMeeting(item.Id);
    }

    private bool ConfirmDelete() => Ui.Confirm(this,
        L.T("Transkript, Zusammenfassung und eine noch nicht ausgewertete Aufnahme werden gelöscht."), L.T("Meeting löschen?"));

    // --- Live -------------------------------------------------------------------------

    private void OnStop(object sender, RoutedEventArgs e) => _app.StopMeeting();

    private void OnDiscard(object sender, RoutedEventArgs e)
    {
        if (Ui.Confirm(this, L.T("Die Aufnahme wird gelöscht und nicht ausgewertet. Das lässt sich nicht rückgängig machen."), L.T("Aufnahme verwerfen?")))
        {
            _app.CancelMeeting();
        }
    }

    // --- Detail -----------------------------------------------------------------------

    private void OnBack(object sender, RoutedEventArgs e)
    {
        HideRename();
        _model.Close();
        _model.ReloadList();
    }

    private void OnDeleteFailed(object sender, RoutedEventArgs e)
    {
        if (Ui.Confirm(this, L.T("Die Aufnahme wird gelöscht und kann nicht mehr ausgewertet werden."), L.T("Meeting löschen?"))) _model.DeleteFailed();
    }

    private void OnRetry(object sender, RoutedEventArgs e) => Retry(null);
    private void OnRetryFailed(object sender, RoutedEventArgs e) => Retry(_model.FailedMeetingId);

    private void Retry(Guid? id)
    {
        HideRename();
        try
        {
            _model.Retry(id);
        }
        catch (Exception ex)
        {
            Log.Warn("Retrying the meeting failed", ex);
            Ui.Inform(this, ex.Message, L.T("Meeting"), warning: true);
        }
        WatchSession();
        UpdateTimer();
        Scroller.ScrollToTop();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (ConfirmDelete())
        {
            HideRename();
            _model.Delete();
        }
    }

    private void OnRenameTitle(object sender, RoutedEventArgs e)
    {
        if (_model.Record is not { } record) return;
        HideRename();
        TitleBox.Text = record.Title;
        TitleRow.Visibility = Visibility.Collapsed;
        TitleRenamePanel.Visibility = Visibility.Visible;
        TitleBox.Focus();
        TitleBox.SelectAll();
    }

    private void OnTitleKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { SaveTitle(); e.Handled = true; }
        else if (e.Key == Key.Escape) { HideTitleRename(); e.Handled = true; }
    }

    private void OnTitleSave(object sender, RoutedEventArgs e) => SaveTitle();
    private void OnTitleCancel(object sender, RoutedEventArgs e) => HideTitleRename();

    private void SaveTitle()
    {
        _model.RenameMeeting(TitleBox.Text);
        HideTitleRename();
    }

    private void HideTitleRename()
    {
        TitleRenamePanel.Visibility = Visibility.Collapsed;
        TitleRow.Visibility = Visibility.Visible;
    }

    private void OnCopyTranscript(object sender, RoutedEventArgs e) => _model.Copy("transcript");
    private void OnCopyAll(object sender, RoutedEventArgs e) => _model.Copy("all");
    private void OnCopySummary(object sender, RoutedEventArgs e) => _model.Copy("summary");

    private void OnSaveText(object sender, RoutedEventArgs e)
    {
        if (_model.Record is not { } record) return;
        var name = string.Join("_", record.Title.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        var dialog = new SaveFileDialog
        {
            Title = L.T("Meeting als Textdatei speichern"),
            FileName = (name.Length > 0 ? name : "Meeting") + ".txt",
            Filter = L.T("Textdatei (*.txt)") + "|*.txt",
            AddExtension = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, record.ShareText);
        }
        catch (Exception ex)
        {
            Ui.Inform(this, L.F("Die Datei konnte nicht gespeichert werden: {0}", ex.Message), L.T("Speichern"), warning: true);
        }
    }

    private async void OnSummarize(object sender, RoutedEventArgs e) => await _model.SummarizeAsync();
    private void OnCancelSummary(object sender, RoutedEventArgs e) => _model.CancelSummary();

    private void OnRenameSpeaker(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SpeakerItem speaker || _model.Record is not { } record) return;
        _renaming = speaker.Speaker;
        // The default name as MeetingRecord.NameFor shows it in the transcript.
        var defaultName = speaker.Speaker == 0 ? "Ich" : $"Person {speaker.Speaker}";
        RenameHint.Text = L.F("Der Name ersetzt „{0}“ im Transkript. Leer lassen für „{0}“.", defaultName);
        RenameBox.Text = record.SpeakerNames.TryGetValue(speaker.Speaker, out var custom) ? custom : "";
        RenamePanel.Visibility = Visibility.Visible;
        RenameBox.Focus();
        RenameBox.SelectAll();
    }

    private void OnRenameKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { SaveRename(); e.Handled = true; }
        else if (e.Key == Key.Escape) { HideRename(); e.Handled = true; }
    }

    private void OnRenameSave(object sender, RoutedEventArgs e) => SaveRename();
    private void OnRenameCancel(object sender, RoutedEventArgs e) => HideRename();

    private void SaveRename()
    {
        if (_renaming is int speaker) _model.Rename(speaker, RenameBox.Text);
        HideRename();
    }

    private void HideRename()
    {
        _renaming = null;
        RenamePanel.Visibility = Visibility.Collapsed;
        HideTitleRename();
    }
}
