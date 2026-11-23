using System.Collections.ObjectModel;
using System.ComponentModel;
using Diktat.AI.Meetings;
using Diktat.Core.Settings;
using Diktat.Windows.Runtime;

namespace Diktat.Windows.Views.ViewModels;

public enum MeetingsView
{
    Start,
    Live,
    Processing,
    Failed,
    Detail,
}

public sealed record MeetingListItem(Guid Id, string Title, string Detail, bool IsPending)
{
    public string AutomationName => $"{Title}, {Detail}";
}

public sealed record SpeakerItem(int Speaker, string Name)
{
    public string RenameAutomationName => L.F("{0} umbenennen", Name);
}

public sealed record TurnItem(string Name, string Time, string Text, int Speaker);

/// A card of the source picker: "Im Raum" or "Online-Meeting". The online card's subtitle says
/// whether the own microphone is recorded too.
public sealed class SourceCardItem(MeetingSource value, string glyph) : ObservableObject
{
    private MeetingSource _current = value;

    public MeetingSource Value { get; } = value;
    public string Glyph { get; } = glyph;
    public string Title => Value.Title();
    public string Subtitle => _current.Card() == Value ? _current.Subtitle() : Value.Subtitle();
    public string AutomationName => $"{Title}, {Subtitle}";

    public void Show(MeetingSource current)
    {
        _current = current;
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(AutomationName));
    }
}

/// "Meetings": start a recording, follow the running one, read and share the saved ones.
public sealed class MeetingsViewModel : ObservableObject
{
    private readonly IDiktatApp _app;
    private IMeetingSession? _session;
    private MeetingPhase _lastPhase = MeetingPhase.Idle;
    private Guid? _selectedId;
    private MeetingRecord? _record;
    private bool _isSummarizing;
    private string? _summaryError;
    private CancellationTokenSource? _summaryCts;
    private bool _showFailure;
    private bool _chatNoteCopied;
    private string _copiedTarget = "";

    public MeetingsViewModel(IDiktatApp app)
    {
        _app = app;
        // People for the room, a video call for online.
        SourceCards = [new SourceCardItem(MeetingSource.Room, ""), new SourceCardItem(MeetingSource.Online, "")];
        ShowSource();
    }

    public ObservableCollection<MeetingListItem> Meetings { get; } = new();
    public ObservableCollection<SpeakerItem> Speakers { get; } = new();
    public ObservableCollection<TurnItem> Turns { get; } = new();

    public IMeetingSession? Session => _session;

    // --- View state ---------------------------------------------------------------------

    public MeetingsView View
    {
        get
        {
            if (_session is { Phase: MeetingPhase.Recording }) return MeetingsView.Live;
            if (_session is { Phase: MeetingPhase.Processing }) return MeetingsView.Processing;
            if (_showFailure && _session?.ErrorMessage is not null) return MeetingsView.Failed;
            if (_record is not null) return MeetingsView.Detail;
            return MeetingsView.Start;
        }
    }

    public bool IsStart => View == MeetingsView.Start;
    public bool IsLive => View == MeetingsView.Live;
    public bool IsProcessing => View == MeetingsView.Processing;
    public bool IsFailed => View == MeetingsView.Failed;
    public bool IsDetail => View == MeetingsView.Detail;

    // --- Start --------------------------------------------------------------------------

    public bool SummarizesMeetings
    {
        get => _app.Services.Settings.App.SummarizesMeetings;
        set
        {
            if (_app.Services.Settings.App.SummarizesMeetings == value) return;
            _app.Services.Settings.App.SummarizesMeetings = value;
            _app.Services.SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SummarizeDetail));
        }
    }

    public string SummarizeDetail => SummarizesMeetings
        ? L.T("Diktat schreibt nach dem Meeting eine Zusammenfassung mit Entscheidungen und To-dos. Das dauert etwas länger.")
        : L.T("Du bekommst nur das Transkript mit den Personen, das geht schneller. Die Zusammenfassung kannst du später starten.");

    public bool HasMeetings => Meetings.Count > 0;

    public IReadOnlyList<SourceCardItem> SourceCards { get; }

    /// The picked source, kept in the settings for the next meeting.
    public MeetingSource Source
    {
        get => MeetingSourceInfo.Resolve(_app.Services.Settings.App.MeetingSource, legacySystemAudio: false, canRecordSystemAudio: true);
        set
        {
            if (Source == value && _app.Services.Settings.App.MeetingSource is not null) return;
            _app.Services.Settings.App.MeetingSource = value;
            _app.Services.SaveSettings();
            OnSourceChanged();
        }
    }

    /// The card that is selected. The online card keeps the choice of its microphone switch.
    public MeetingSource SelectedCard
    {
        get => Source.Card();
        set
        {
            if (Source.Card() != value) Source = value;
        }
    }

    public bool IsOnlineCard => Source.UsesSystemAudio();

    /// "Mein Mikrofon mit aufnehmen" on the online card.
    public bool RecordsOwnMicrophone
    {
        get => Source.UsesMicrophone();
        set => Source = MeetingSourceInfo.Online(withMicrophone: value);
    }

    public string OwnMicrophoneDetail => Source.UsesMicrophone()
        ? L.T("Das System-Audio enthält nur die anderen. Dein Mikrofon bringt dazu, was du selbst sagst.")
        : L.T("Ausgeschaltet fehlt im Transkript, was du selbst sagst. Gedacht für Webinare und Videos.");

    public string SourceExplanation => Source.Explanation();

    /// The question for the chat belongs to a call one takes part in.
    public bool ShowsChatNote => Source == MeetingSource.Online;

    /// The settings changed elsewhere (the default source, or a call notification picked online).
    public void OnSourceChanged()
    {
        ShowSource();
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(SelectedCard));
        OnPropertyChanged(nameof(IsOnlineCard));
        OnPropertyChanged(nameof(RecordsOwnMicrophone));
        OnPropertyChanged(nameof(OwnMicrophoneDetail));
        OnPropertyChanged(nameof(SourceExplanation));
        OnPropertyChanged(nameof(ShowsChatNote));
    }

    private void ShowSource()
    {
        var source = Source;
        foreach (var card in SourceCards) card.Show(source);
    }

    /// Asked in the call's chat before starting, so everyone can agree in writing.
    public static string ChatNote => L.T("Ich würde dieses Meeting gern mit Diktat aufnehmen und auf meinem PC transkribieren und zusammenfassen lassen. Ton und Text bleiben auf meinem PC, die Aufnahme wird nach der Auswertung gelöscht. Ist das für alle in Ordnung?");

    public string ChatNoteButton => _chatNoteCopied ? L.T("Kopiert, im Chat einfügen") : L.T("Frage für den Chat kopieren");

    public void CopyChatNote()
    {
        _app.CopyToClipboard(ChatNote);
        _chatNoteCopied = true;
        OnPropertyChanged(nameof(ChatNoteButton));
    }

    // --- Live ---------------------------------------------------------------------------

    public string LiveTitle => _session?.IsOnline == true ? L.T("Aufnahme läuft, mit System-Audio") : L.T("Aufnahme läuft");
    public string Elapsed => Ui.Clock(_session?.Elapsed ?? TimeSpan.Zero);
    public bool IsOnline => _session?.IsOnline == true;
    public bool RecordsMicrophone => _session?.RecordsMicrophone != false;

    /// Which microphone hears the room, so a wrong one is noticed at the start.
    public string MicrophoneLine => _session?.MicrophoneName is { Length: > 0 } name ? L.F("Mikrofon: {0}", name) : "";

    public bool HasProblem => IsLive && _session?.Problem is not null;
    public string ProblemTitle => _session?.Problem is { } problem ? problem.Title() : "";
    public string ProblemDetail => _session?.Problem is { } problem ? problem.Detail() : "";
    /// A notice of the running recording, e.g. that the microphone changed.
    public string LiveNotice => IsLive ? _session?.StatusMessage ?? "" : "";
    public string LiveNotes => _session?.LiveNotes ?? "";
    public string ProcessingText => _session?.StatusMessage ?? L.T("Meeting wird ausgewertet …");
    public string FailureText => _session?.ErrorMessage ?? "";
    public bool FailedMeetingIsPending => _session?.MeetingId is { } id && _app.Services.Meetings.Meeting(id)?.Status == MeetingStatus.Pending;
    public bool CanRetryFailed => _session?.MeetingId is { } id && FailedMeetingIsPending && SafeHasAudio(id);

    // --- Detail -------------------------------------------------------------------------

    public MeetingRecord? Record => _record;
    public string DetailTitle => _record?.Title ?? "";
    public string DetailLine => _record is null ? "" : $"{_record.CreatedAt.ToLocalTime().ToString(DetailDatePattern, L.Culture)} · {Ui.MeetingSummaryLine(_record)}";

    private static string DetailDatePattern =>
        L.Current == UiLanguage.English ? "dddd, MMMM d, yyyy, h:mm tt" : "dddd, d. MMMM yyyy, HH:mm";
    public string? Summary => _record?.Summary;
    public bool HasSummary => !string.IsNullOrWhiteSpace(_record?.Summary);
    public string SummaryNote => _record?.SummaryNote ?? L.T("Noch keine Zusammenfassung.");
    public bool IsPending => _record?.Status == MeetingStatus.Pending;
    public bool CanRetry => _record is { Status: MeetingStatus.Pending } record && SafeHasAudio(record.Id);

    private bool SafeHasAudio(Guid id)
    {
        try { return _app.HasMeetingAudio(id); }
        catch { return false; }
    }

    /// Evaluates a pending meeting again; the session then shows the progress.
    public void Retry(Guid? id = null)
    {
        var target = id ?? _record?.Id ?? _session?.MeetingId;
        if (target is not { } meetingId) return;
        _showFailure = false;
        _record = null;
        _selectedId = null;
        _app.RetryMeeting(meetingId);
        UpdateSession();
        OnSessionChanged();
    }

    public Guid? FailedMeetingId => _session?.MeetingId;
    public bool IsSummarizing => _isSummarizing;
    public bool CanSummarize => !_isSummarizing && _record is { Status: MeetingStatus.Done, Turns.Count: > 0 };
    public string? SummaryError => _summaryError;
    public bool HasTurns => Turns.Count > 0;

    public string CopyTranscriptLabel => _copiedTarget == "transcript" ? L.T("Kopiert") : L.T("Transkript kopieren");
    public string CopyAllLabel => _copiedTarget == "all" ? L.T("Kopiert") : L.T("Alles kopieren");
    public string CopySummaryLabel => _copiedTarget == "summary" ? L.T("Kopiert") : L.T("Kopieren");

    public async void Copy(string target)
    {
        if (_record is null) return;
        var text = target switch
        {
            "transcript" => _record.TranscriptShareText,
            "summary" => _record.Summary ?? "",
            _ => _record.ShareText,
        };
        if (text.Length == 0) return;
        _app.CopyToClipboard(text);
        _copiedTarget = target;
        RaiseCopyLabels();
        await Task.Delay(1600);
        _copiedTarget = "";
        RaiseCopyLabels();
    }

    private void RaiseCopyLabels()
    {
        OnPropertyChanged(nameof(CopyTranscriptLabel));
        OnPropertyChanged(nameof(CopyAllLabel));
        OnPropertyChanged(nameof(CopySummaryLabel));
    }

    public void Open(Guid id)
    {
        _selectedId = id;
        _showFailure = false;
        _summaryError = null;
        LoadRecord();
    }

    public void Close()
    {
        _selectedId = null;
        _record = null;
        _showFailure = false;
        Raise();
    }

    public void Delete()
    {
        if (_record is null) return;
        _summaryCts?.Cancel();
        _app.Services.Meetings.Delete(_record.Id);
        Close();
        ReloadList();
    }

    public void DeleteFailed()
    {
        if (_session?.MeetingId is { } id) _app.Services.Meetings.Delete(id);
        _showFailure = false;
        ReloadList();
        Raise();
    }

    public void Rename(int speaker, string name)
    {
        if (_record is null) return;
        var record = _app.Services.Meetings.Meeting(_record.Id);
        if (record is null) return;
        var trimmed = name.Trim();
        if (trimmed.Length == 0) record.SpeakerNames.Remove(speaker);
        else record.SpeakerNames[speaker] = trimmed;
        _app.Services.Meetings.Upsert(record);
        LoadRecord();
    }

    public async Task SummarizeAsync()
    {
        if (_record is null || _isSummarizing) return;
        var id = _record.Id;
        _summaryCts?.Cancel();
        _summaryCts = new CancellationTokenSource();
        _isSummarizing = true;
        _summaryError = null;
        Raise();
        try
        {
            await _app.SummarizeMeetingAsync(id, _summaryCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _summaryError = ex.Message;
        }
        finally
        {
            _isSummarizing = false;
            if (_selectedId == id) LoadRecord();
            else Raise();
        }
    }

    public void CancelSummary() => _summaryCts?.Cancel();

    // --- Loading ------------------------------------------------------------------------

    /// Follows the app's current meeting session; returns true when it changed.
    public bool UpdateSession()
    {
        var session = _app.Meeting;
        if (ReferenceEquals(session, _session)) return false;
        _session = session;
        _lastPhase = session?.Phase ?? MeetingPhase.Idle;
        Raise();
        return true;
    }

    /// The session's phase or texts changed.
    public void OnSessionChanged()
    {
        var phase = _session?.Phase ?? MeetingPhase.Idle;
        if (phase != _lastPhase)
        {
            var wasRunning = _lastPhase != MeetingPhase.Idle;
            _lastPhase = phase;
            if (phase == MeetingPhase.Idle && wasRunning)
            {
                ReloadList();
                if (_session?.ErrorMessage is not null) _showFailure = true;
                else if (_session?.MeetingId is { } id) Open(id);
            }
            else if (phase != MeetingPhase.Idle)
            {
                _record = null;
                _selectedId = null;
            }
        }
        Raise();
    }

    public void ReloadList()
    {
        var recordingId = _session is { Phase: not MeetingPhase.Idle } ? _session.MeetingId : null;
        Meetings.Clear();
        foreach (var record in _app.Services.Meetings.Meetings)
        {
            if (record.Id == recordingId) continue;
            var detail = record.Status == MeetingStatus.Done
                ? Ui.MeetingSummaryLine(record)
                : L.T("Noch nicht ausgewertet");
            Meetings.Add(new MeetingListItem(record.Id, record.Title, detail, record.Status == MeetingStatus.Pending));
        }
        if (_selectedId is not null) LoadRecord();
        else Raise();
    }

    private void LoadRecord()
    {
        _record = _selectedId is { } id ? _app.Services.Meetings.Meeting(id) : null;
        if (_record is null) _selectedId = null;
        Speakers.Clear();
        Turns.Clear();
        if (_record is not null)
        {
            foreach (var speaker in _record.Speakers) Speakers.Add(new SpeakerItem(speaker, _record.NameFor(speaker)));
            foreach (var turn in _record.Turns)
            {
                Turns.Add(new TurnItem(_record.NameFor(turn.Speaker), Ui.Clock(TimeSpan.FromSeconds(turn.Start)), turn.Text, turn.Speaker));
            }
        }
        Raise();
    }

    public void RaiseLive()
    {
        OnPropertyChanged(nameof(Elapsed));
        OnPropertyChanged(nameof(LiveNotes));
        OnPropertyChanged(nameof(ProcessingText));
    }

    private void Raise() => OnAllPropertiesChanged();
}
