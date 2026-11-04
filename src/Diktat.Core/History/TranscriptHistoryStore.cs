using System.Text.Json;
using System.Text.Json.Serialization;
using Diktat.Core.Settings;
using Diktat.Core.Support;

namespace Diktat.Core.History;

public sealed record TranscriptHistoryEntry(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("workflow")] WorkflowType Workflow,
    [property: JsonPropertyName("text")] string Text);

/// Recent results, stored only on this PC in one file, encrypted with <see cref="IDataProtector"/>
/// (DPAPI for the current user on Windows). Thread-safe; <see cref="Changed"/> fires after every
/// change of <see cref="Entries"/>.
public sealed class TranscriptHistoryStore
{
    public const int MaxEntries = 30;
    public const string FileName = "history.dat";

    private readonly object _lock = new();
    private readonly string _path;
    private readonly IDataProtector _protector;
    private readonly Func<DateTimeOffset> _clock;
    private List<TranscriptHistoryEntry> _entries;
    private HistoryRetention _retention;

    public event EventHandler? Changed;

    /// <param name="directory">Where the history file lives, e.g. %LOCALAPPDATA%\Diktat.</param>
    /// <param name="clock">The current time; tests pass a fixed one.</param>
    public TranscriptHistoryStore(string directory, IDataProtector protector, HistoryRetention retention, Func<DateTimeOffset>? clock = null)
    {
        _path = System.IO.Path.Combine(directory, FileName);
        _protector = protector;
        _retention = retention;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _entries = Load();
        Prune();
    }

    public string Path => _path;

    /// Newest first.
    public IReadOnlyList<TranscriptHistoryEntry> Entries
    {
        get
        {
            lock (_lock) return _entries.ToList();
        }
    }

    public HistoryRetention Retention
    {
        get
        {
            lock (_lock) return _retention;
        }
    }

    public void Add(string text, WorkflowType workflow)
    {
        var trimmed = (text ?? "").Trim();
        lock (_lock)
        {
            if (_retention == HistoryRetention.Off || trimmed.Length == 0) return;
            _entries.Insert(0, new TranscriptHistoryEntry(Guid.NewGuid(), _clock(), workflow, trimmed));
            PruneCore();
        }
        OnChanged();
    }

    public void Remove(Guid id)
    {
        lock (_lock)
        {
            if (_entries.RemoveAll(e => e.Id == id) == 0) return;
            Save();
        }
        OnChanged();
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries = [];
            DeleteFile();
        }
        OnChanged();
    }

    public void UpdateRetention(HistoryRetention retention)
    {
        lock (_lock) _retention = retention;
        Prune();
    }

    /// Drops expired entries. Call on launch, on every change and when the history is shown.
    public void Prune()
    {
        bool changed;
        lock (_lock) changed = PruneCore();
        if (changed) OnChanged();
    }

    /// Callers hold the lock.
    private bool PruneCore()
    {
        if (_retention.MaxAge() is not TimeSpan maxAge)
        {
            var hadEntries = _entries.Count > 0;
            _entries = [];
            DeleteFile();
            return hadEntries;
        }
        var now = _clock();
        var pruned = _entries.Where(e => now - e.CreatedAt < maxAge).Take(MaxEntries).ToList();
        var changed = !pruned.SequenceEqual(_entries);
        _entries = pruned;
        Save();
        return changed;
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static readonly JsonSerializerOptions JsonOptions = SettingsJson.Create();

    private void Save()
    {
        if (_entries.Count == 0)
        {
            DeleteFile();
            return;
        }
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            var data = JsonSerializer.SerializeToUtf8Bytes(_entries, JsonOptions);
            var temporary = _path + ".tmp";
            File.WriteAllBytes(temporary, _protector.Protect(data));
            File.Move(temporary, _path, overwrite: true);
        }
        catch
        {
            // History is a convenience; a failed write must never block pasting.
        }
    }

    private List<TranscriptHistoryEntry> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];
            var data = _protector.Unprotect(File.ReadAllBytes(_path));
            return JsonSerializer.Deserialize<List<TranscriptHistoryEntry>>(data, JsonOptions) ?? [];
        }
        catch
        {
            // Unreadable (other user, damaged file): start empty.
            return [];
        }
    }

    private void DeleteFile()
    {
        try
        {
            File.Delete(_path);
        }
        catch
        {
            // Nothing to do.
        }
    }
}
