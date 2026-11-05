using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Diktat.Core.Settings;
using Diktat.Core.Support;

namespace Diktat.AI.Meetings;

public sealed record MeetingTurn
{
    [JsonPropertyName("id")] public Guid Id { get; init; } = Guid.NewGuid();
    [JsonPropertyName("speaker")] public int Speaker { get; init; }
    /// Seconds from the start of the recording.
    [JsonPropertyName("start")] public double Start { get; init; }
    [JsonPropertyName("text")] public string Text { get; init; } = "";

    public MeetingTurn() { }

    public MeetingTurn(int speaker, double start, string text)
    {
        Speaker = speaker;
        Start = start;
        Text = text;
    }
}

public enum MeetingStatus
{
    /// Audio is kept until processing succeeds, so an interrupted meeting can be retried.
    Pending,
    Done,
}

/// One meeting. A plain value: the store hands out and keeps copies, so changing a record does
/// nothing until it is passed to <see cref="MeetingStore.Upsert"/>.
public sealed class MeetingRecord
{
    [JsonPropertyName("id")] public Guid Id { get; init; } = Guid.NewGuid();
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    /// Seconds.
    [JsonPropertyName("duration")] public double Duration { get; set; }
    [JsonPropertyName("status")] public MeetingStatus Status { get; set; } = MeetingStatus.Pending;
    [JsonPropertyName("turns")] public List<MeetingTurn> Turns { get; set; } = new();
    [JsonPropertyName("speakerNames")] public Dictionary<int, string> SpeakerNames { get; set; } = new();
    [JsonPropertyName("summary")] public string? Summary { get; set; }
    /// Why the summary is missing, e.g. no language model available.
    [JsonPropertyName("summaryNote")] public string? SummaryNote { get; set; }

    private static readonly string[] GermanMonths =
        ["Jan.", "Feb.", "März", "Apr.", "Mai", "Juni", "Juli", "Aug.", "Sept.", "Okt.", "Nov.", "Dez."];

    /// "Meeting am 17. Okt. 2026, 11:58", in English "Meeting on Oct 17, 2026, 11:58 AM". The title
    /// is made once, in the UI language at the time, and then belongs to the meeting.
    public static string TitleFor(DateTimeOffset startedAt) => L.F("Meeting am {0}", L.Current == UiLanguage.English
        ? startedAt.ToString("MMM d, yyyy, h:mm tt", L.CultureFor(UiLanguage.English))
        : $"{startedAt.Day}. {GermanMonths[startedAt.Month - 1]} {startedAt.Year}, " + startedAt.ToString("HH:mm", CultureInfo.InvariantCulture));

    /// Speaker 0 is the microphone in an online meeting, the person at this PC.
    public string NameFor(int speaker)
    {
        var custom = SpeakerNames.TryGetValue(speaker, out var name) ? name.Trim() : "";
        if (custom.Length > 0) return custom;
        return speaker == 0 ? "Ich" : $"Person {speaker}";
    }

    [JsonIgnore]
    public IReadOnlyList<int> Speakers => Turns.Select(t => t.Speaker).Distinct().Order().ToList();

    /// "Name: text" per turn, the form used for the summary and for copying.
    [JsonIgnore]
    public string TranscriptPlainText => TranscriptText(null);

    /// For a language model: the person at this PC ("Ich") appears as <paramref name="ownName"/>
    /// unless renamed, so the tasks they take on are assigned to them and not to a stranger.
    public string TranscriptText(string? ownName) => string.Join("\n\n", Turns.Select(turn =>
    {
        var renamed = SpeakerNames.TryGetValue(turn.Speaker, out var custom) && custom.Trim().Length > 0;
        var name = turn.Speaker == 0 && !renamed ? ownName ?? NameFor(0) : NameFor(turn.Speaker);
        return $"{name}: {turn.Text}";
    }));

    /// Title and transcript by person, e.g. to continue in another AI app.
    [JsonIgnore]
    public string TranscriptShareText => Title + "\n\n" + TranscriptPlainText;

    [JsonIgnore]
    public string ShareText
    {
        get
        {
            var parts = new List<string> { Title };
            if (Summary is not null) parts.Add(Summary);
            parts.Add(L.T("Transkript") + "\n\n" + TranscriptPlainText);
            return string.Join("\n\n", parts);
        }
    }

    public MeetingRecord Clone() => new()
    {
        Id = Id,
        CreatedAt = CreatedAt,
        Title = Title,
        Duration = Duration,
        Status = Status,
        Turns = Turns.ToList(),
        SpeakerNames = new Dictionary<int, string>(SpeakerNames),
        Summary = Summary,
        SummaryNote = SummaryNote,
    };
}

/// Meetings stay on this PC, encrypted for the current user (DPAPI via <see cref="IDataProtector"/>),
/// in one file in the given directory. The audio of a meeting being processed lies next to it.
public sealed class MeetingStore
{
    public const string FileName = ".meetings";

    private static readonly JsonSerializerOptions Json = SettingsJson.Create();
    private static readonly Regex OldSpeakerLabel = new(@"Sprecher (\d+)", RegexOptions.CultureInvariant);

    private readonly string _directory;
    private readonly IDataProtector _protector;
    private readonly object _lock = new();
    private List<MeetingRecord> _meetings = new();

    public MeetingStore(string directory, IDataProtector protector)
    {
        _directory = directory;
        _protector = protector;
        Load();
    }

    /// Raised after the meetings changed.
    public event EventHandler? Changed;

    public string DirectoryPath => _directory;
    private string FilePath => Path.Combine(_directory, FileName);

    /// The microphone recording of a meeting.
    public string AudioPath(Guid id) => Path.Combine(_directory, $"{id}.wav");

    /// What the PC played during an online meeting, i.e. the other side.
    public string SystemAudioPath(Guid id) => Path.Combine(_directory, $"{id}-system.wav");

    /// Newest first.
    public IReadOnlyList<MeetingRecord> Meetings
    {
        get { lock (_lock) return _meetings.Select(m => m.Clone()).ToList(); }
    }

    public MeetingRecord? Meeting(Guid id)
    {
        lock (_lock) return _meetings.FirstOrDefault(m => m.Id == id)?.Clone();
    }

    /// Returns whether the meetings reached the disk, so audio is only deleted once its
    /// transcript is safe.
    public bool Upsert(MeetingRecord meeting)
    {
        bool saved;
        lock (_lock)
        {
            var index = _meetings.FindIndex(m => m.Id == meeting.Id);
            if (index >= 0) _meetings[index] = meeting.Clone();
            else _meetings.Insert(0, meeting.Clone());
            saved = Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return saved;
    }

    /// Sets only the summary of a meeting that still exists, so a delete or a rename made while
    /// the summary was being written is kept.
    public void SetSummary(Guid id, string? summary, string? note)
    {
        lock (_lock)
        {
            var meeting = _meetings.FirstOrDefault(m => m.Id == id);
            if (meeting is null) return;
            meeting.Summary = summary;
            meeting.SummaryNote = note;
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Delete(Guid id)
    {
        lock (_lock)
        {
            TryDelete(AudioPath(id));
            TryDelete(SystemAudioPath(id));
            _meetings.RemoveAll(m => m.Id == id);
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Load()
    {
        if (!File.Exists(FilePath)) return;
        List<MeetingRecord>? meetings = null;
        try
        {
            var plain = _protector.Unprotect(File.ReadAllBytes(FilePath));
            meetings = JsonSerializer.Deserialize<List<MeetingRecord>>(plain, Json);
        }
        catch (Exception)
        {
            meetings = null;
        }
        if (meetings is null)
        {
            // Never overwrite meetings that only this build cannot read (another user's key, a
            // later format): keep the file aside so they can still be recovered.
            MoveUnreadableFileAside();
            return;
        }
        _meetings = meetings;
        MigrateSpeakerLabels();
    }

    private void MoveUnreadableFileAside()
    {
        try
        {
            var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            File.Move(FilePath, FilePath + $".unreadable-{stamp}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// Summaries written before speakers became "Person N" still name them "Sprecher N".
    private void MigrateSpeakerLabels()
    {
        var changed = false;
        foreach (var meeting in _meetings)
        {
            if (meeting.Summary is not { } summary || !summary.Contains("Sprecher ")) continue;
            meeting.Summary = OldSpeakerLabel.Replace(summary, "Person $1");
            changed = true;
        }
        if (changed) Save();
    }

    private bool Save()
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var data = JsonSerializer.SerializeToUtf8Bytes(_meetings, Json);
            var temporary = FilePath + ".tmp";
            File.WriteAllBytes(temporary, _protector.Protect(data));
            File.Move(temporary, FilePath, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
