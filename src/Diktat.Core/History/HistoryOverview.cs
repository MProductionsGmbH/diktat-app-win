using System.Globalization;
using System.Text;

namespace Diktat.Core.History;

public enum HistoryDayKind
{
    Today,
    Yesterday,
    Date,
}

/// The day a group of results belongs to; <see cref="Date"/> is the calendar day in the time zone used.
public readonly record struct HistoryDay(HistoryDayKind Kind, DateOnly Date)
{
    public static HistoryDay Today(DateOnly date) => new(HistoryDayKind.Today, date);
    public static HistoryDay Yesterday(DateOnly date) => new(HistoryDayKind.Yesterday, date);
    public static HistoryDay On(DateOnly date) => new(HistoryDayKind.Date, date);
}

public sealed record HistoryGroup<TItem>(HistoryDay Day, IReadOnlyList<TItem> Items);

public readonly record struct HistoryStatistics(int Words, int Dictations)
{
    public int WordsPerDictation => Dictations == 0 ? 0 : (int)Math.Round((double)Words / Dictations, MidpointRounding.AwayFromZero);
}

/// What the history page of the main window shows: results grouped by day, narrowed by a
/// search, and the optional counts. Pure logic, so it is tested without the window.
public static class HistoryOverview
{
    /// The items, newest first, in groups per calendar day of <paramref name="timeZone"/> (local by default).
    public static IReadOnlyList<HistoryGroup<TItem>> Groups<TItem>(
        IEnumerable<TItem> items,
        Func<TItem, DateTimeOffset> date,
        DateTimeOffset? now = null,
        TimeZoneInfo? timeZone = null)
    {
        var zone = timeZone ?? TimeZoneInfo.Local;
        var today = DayOf(now ?? DateTimeOffset.Now, zone);
        var yesterday = today.AddDays(-1);

        var result = new List<HistoryGroup<TItem>>();
        DateOnly? currentDay = null;
        var current = new List<TItem>();
        void Flush()
        {
            if (currentDay is not DateOnly day || current.Count == 0) return;
            var kind = day == today ? HistoryDayKind.Today : day == yesterday ? HistoryDayKind.Yesterday : HistoryDayKind.Date;
            result.Add(new HistoryGroup<TItem>(new HistoryDay(kind, day), current));
        }
        foreach (var item in items.OrderByDescending(date))
        {
            var day = DayOf(date(item), zone);
            if (day != currentDay)
            {
                Flush();
                currentDay = day;
                current = [];
            }
            current.Add(item);
        }
        Flush();
        return result;
    }

    private static DateOnly DayOf(DateTimeOffset moment, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(moment, zone).DateTime);

    /// Case and accents do not matter; an empty search matches everything.
    public static bool Matches(string text, string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0) return true;
        return Fold(text).Contains(Fold(trimmed), StringComparison.Ordinal);
    }

    /// Lowercased with accents removed: "Büro" -> "buro".
    private static string Fold(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(c);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    public static int WordCount(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    public static HistoryStatistics Statistics(IEnumerable<string> texts)
    {
        var list = texts as IReadOnlyCollection<string> ?? texts.ToList();
        return new HistoryStatistics(list.Sum(WordCount), list.Count);
    }
}
