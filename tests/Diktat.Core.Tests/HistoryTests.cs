using System.Text;
using Diktat.Core.History;
using Diktat.Core.Settings;
using Diktat.Core.Support;

namespace Diktat.Core.Tests;

public class HistoryOverviewTests
{
    private static readonly TimeZoneInfo Zurich = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");

    private static DateTimeOffset Date(int day, int hour)
    {
        var local = new DateTime(2026, 11, day, hour, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Zurich.GetUtcOffset(local));
    }

    [Fact]
    public void GroupsByDayNewestFirst()
    {
        var now = Date(3, 15);
        var items = new[] { Date(1, 9), Date(3, 8), Date(2, 17), Date(3, 14), Date(2, 10) };
        var groups = HistoryOverview.Groups(items, d => d, now, Zurich);
        Assert.Equal(
            new[] { HistoryDayKind.Today, HistoryDayKind.Yesterday, HistoryDayKind.Date },
            groups.Select(g => g.Day.Kind));
        Assert.Equal(new DateOnly(2026, 11, 1), groups[2].Day.Date);
        Assert.Equal(new[] { 2, 2, 1 }, groups.Select(g => g.Items.Count));
        Assert.Equal(new[] { Date(3, 14), Date(3, 8) }, groups[0].Items);
    }

    [Fact]
    public void DaysFollowTheTimeZoneNotUtc()
    {
        // 00:30 in Zurich is the previous day in UTC.
        var now = Date(3, 15);
        var groups = HistoryOverview.Groups(new[] { Date(3, 0).AddMinutes(30) }, d => d, now, Zurich);
        Assert.Equal(HistoryDayKind.Today, groups.Single().Day.Kind);
    }

    [Fact]
    public void NoItemsNoGroups() => Assert.Empty(HistoryOverview.Groups(Array.Empty<DateTimeOffset>(), d => d, timeZone: Zurich));

    [Fact]
    public void SearchIgnoresCaseAndAccents()
    {
        Assert.True(HistoryOverview.Matches("Das Büro öffnet um acht", "buro"));
        Assert.True(HistoryOverview.Matches("Das Büro öffnet um acht", " ACHT "));
        Assert.False(HistoryOverview.Matches("Das Büro öffnet um acht", "neun"));
        Assert.True(HistoryOverview.Matches("Alles", "  "));
    }

    [Fact]
    public void Statistics()
    {
        var stats = HistoryOverview.Statistics(["Eins zwei drei", "Vier  fünf\nsechs sieben", ""]);
        Assert.Equal(new HistoryStatistics(7, 3), stats);
        Assert.Equal(2, stats.WordsPerDictation);
        Assert.Equal(0, HistoryOverview.Statistics([]).WordsPerDictation);
    }
}

public sealed class TranscriptHistoryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "diktat-history-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private TranscriptHistoryStore Store(HistoryRetention retention = HistoryRetention.Day, IDataProtector? protector = null) =>
        new(_directory, protector ?? new XorProtector(), retention, () => _now);

    [Fact]
    public void AddsNewestFirstAndSurvivesARestart()
    {
        var store = Store();
        store.Add("  Erster Text ", WorkflowType.TapTalk);
        _now = _now.AddMinutes(1);
        store.Add("Zweiter Text", WorkflowType.TextImprover);
        store.Add("   ", WorkflowType.TapTalk);

        var reopened = Store();
        Assert.Equal(new[] { "Zweiter Text", "Erster Text" }, reopened.Entries.Select(e => e.Text));
        Assert.Equal(WorkflowType.TextImprover, reopened.Entries[0].Workflow);
    }

    [Fact]
    public void TheFileIsEncrypted()
    {
        var store = Store();
        store.Add("Geheimer Text", WorkflowType.TapTalk);
        var bytes = File.ReadAllBytes(store.Path);
        Assert.DoesNotContain("Geheimer", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void ExpiredEntriesGo()
    {
        var store = Store();
        store.Add("Alt", WorkflowType.TapTalk);
        _now = _now.AddHours(23);
        store.Add("Neu", WorkflowType.TapTalk);
        _now = _now.AddHours(2);
        store.Prune();
        Assert.Equal(new[] { "Neu" }, store.Entries.Select(e => e.Text));

        store.UpdateRetention(HistoryRetention.Week);
        _now = _now.AddDays(6);
        store.Prune();
        Assert.Single(store.Entries);
    }

    [Fact]
    public void KeepsAtMostThirtyEntries()
    {
        var store = Store();
        for (var i = 0; i < 35; i++) store.Add($"Text {i}", WorkflowType.TapTalk);
        Assert.Equal(TranscriptHistoryStore.MaxEntries, store.Entries.Count);
        Assert.Equal("Text 34", store.Entries[0].Text);
    }

    [Fact]
    public void OffKeepsNothingAndDeletesTheFile()
    {
        var store = Store();
        store.Add("Text", WorkflowType.TapTalk);
        Assert.True(File.Exists(store.Path));
        store.UpdateRetention(HistoryRetention.Off);
        Assert.Empty(store.Entries);
        Assert.False(File.Exists(store.Path));
        store.Add("Noch ein Text", WorkflowType.TapTalk);
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void RemoveAndClearNotify()
    {
        var store = Store();
        var changes = 0;
        store.Changed += (_, _) => changes++;
        store.Add("Eins", WorkflowType.TapTalk);
        store.Add("Zwei", WorkflowType.TapTalk);
        store.Remove(store.Entries[0].Id);
        Assert.Equal(new[] { "Eins" }, store.Entries.Select(e => e.Text));
        store.Clear();
        Assert.Empty(store.Entries);
        Assert.False(File.Exists(store.Path));
        Assert.Equal(4, changes);
    }

    [Fact]
    public void AnUnreadableFileStartsEmpty()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, TranscriptHistoryStore.FileName), "kein JSON");
        Assert.Empty(Store(protector: new PassThroughDataProtector()).Entries);
    }

    /// Stands in for DPAPI: reversible, and the plain text is not readable in the file.
    private sealed class XorProtector : IDataProtector
    {
        public byte[] Protect(byte[] plain) => plain.Select(b => (byte)(b ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] protectedData) => Protect(protectedData);
    }
}
