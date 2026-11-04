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
        WatchSession();
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

    private void OnStartRoom(object sender, RoutedEventArgs e) => Start(online: false);
    private void OnStartOnline(object sender, RoutedEventArgs e) => Start(online: true);

    private void Start(bool online)
    {
        try
        {
            _app.StartMeeting(online);
        }
        catch (Exception ex)
        {
            Log.Warn("Starting the meeting failed", ex);
            Ui.Inform(this, ex.Message, "Meeting", warning: true);
        }
        WatchSession();
        _model.OnSessionChanged();
        UpdateTimer();
        Scroller.ScrollToTop();
    }

    private void OnCopyChatNote(object sender, RoutedEventArgs e) => _model.CopyChatNote();

    private void OnOpenMeeting(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MeetingListItem item)
        {
            HideRename();
            _model.Open(item.Id);
            Scroller.ScrollToTop();
        }
    }

    // --- Live -------------------------------------------------------------------------

    private void OnStop(object sender, RoutedEventArgs e) => _app.StopMeeting();

    private void OnDiscard(object sender, RoutedEventArgs e)
    {
        if (Ui.Confirm(this, "Die Aufnahme verwerfen? Sie wird gelöscht und nicht ausgewertet.", "Meeting verwerfen")) _app.CancelMeeting();
    }

    // --- Detail -----------------------------------------------------------------------

    private void OnBack(object sender, RoutedEventArgs e)
    {
        HideRename();
        _model.Close();
        _model.ReloadList();
    }

    private void OnDeleteFailed(object sender, RoutedEventArgs e) => _model.DeleteFailed();

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
            Ui.Inform(this, ex.Message, "Meeting", warning: true);
        }
        WatchSession();
        UpdateTimer();
        Scroller.ScrollToTop();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Ui.Confirm(this, "Dieses Meeting mit Transkript und Zusammenfassung löschen?", "Meeting löschen"))
        {
            HideRename();
            _model.Delete();
        }
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
            Title = "Meeting als Textdatei speichern",
            FileName = (name.Length > 0 ? name : "Meeting") + ".txt",
            Filter = "Textdatei (*.txt)|*.txt",
            AddExtension = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, record.ShareText);
        }
        catch (Exception ex)
        {
            Ui.Inform(this, $"Die Datei konnte nicht gespeichert werden: {ex.Message}", "Speichern", warning: true);
        }
    }

    private async void OnSummarize(object sender, RoutedEventArgs e) => await _model.SummarizeAsync();
    private void OnCancelSummary(object sender, RoutedEventArgs e) => _model.CancelSummary();

    private void OnRenameSpeaker(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SpeakerItem speaker || _model.Record is not { } record) return;
        _renaming = speaker.Speaker;
        var defaultName = speaker.Speaker == 0 ? "Ich" : $"Person {speaker.Speaker}";
        RenameHint.Text = $"Der Name ersetzt „{defaultName}“ im Transkript. Leer lassen für „{defaultName}“.";
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
    }
}
