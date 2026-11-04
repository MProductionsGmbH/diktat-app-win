using System.Text.RegularExpressions;
using Diktat.AI.Internal;

namespace Diktat.AI.Meetings;

/// Removes deadlines a language model added to the to-dos of a meeting summary although the
/// transcript never mentions them. Small models like to fill "(bis Freitag)" behind a to-do even
/// when nobody named a day. In doubt a deadline stays: when one of its words was said, when it is
/// only a number, or when the transcript is not German.
public static class InventedDeadlineFilter
{
    /// Words that carry no date of their own ("bis spätestens Ende ...").
    private static readonly HashSet<string> Fillers =
    [
        "bis", "zum", "zur", "am", "an", "im", "in", "der", "die", "das", "des", "dem", "den",
        "spätestens", "ca", "etwa", "frist", "termin",
    ];

    /// A bullet ("- ", "* ", "• ", "– ", "1. ") ending in a parenthesis, maybe followed by
    /// punctuation. Group 1 is the bullet without the parenthesis, group 2 its content.
    private static readonly Regex BulletWithParenthesis =
        new(@"^(\s*(?:[-*•–]|\d+[.)])\s+.*?)\s*\(([^()]*)\)[\s.,;!]*$", RegexOptions.CultureInvariant);

    private static readonly string[] Headings = ["zusammenfassung", "entscheidungen", "to-dos", "todos", "to-do"];

    /// <param name="transcriptIsGerman">The summary is German for a German meeting. Only a German
    /// transcript can be compared word by word; "by Friday" would otherwise lose its "(bis Freitag)".</param>
    public static string Filtered(string summary, string transcript, bool transcriptIsGerman)
    {
        if (!transcriptIsGerman) return summary;
        var spoken = TextHelpers.AlphanumericWords(transcript).ToHashSet();
        var inToDos = false;
        return string.Join("\n", summary.Split('\n').Select(line =>
        {
            if (Heading(line) is { } heading)
            {
                inToDos = heading is "to-dos" or "todos" or "to-do";
                return line;
            }
            return inToDos ? KeepingNamedDeadline(line, spoken) : line;
        }));
    }

    /// "To-dos", "**To-dos:**" or "## Entscheidungen" become their bare lowercased name.
    private static string? Heading(string line)
    {
        var bare = TextHelpers.BareHeading(line);
        return Headings.Contains(bare) ? bare : null;
    }

    private static string KeepingNamedDeadline(string line, HashSet<string> spoken)
    {
        var match = BulletWithParenthesis.Match(line);
        if (!match.Success) return line;

        // Numbers are not checked: a spoken "dreiundzwanzigsten" can come back as "23.".
        var deadlineWords = TextHelpers.AlphanumericWords(match.Groups[2].Value)
            .Where(w => w.Length >= 2 && !Fillers.Contains(w) && !w.All(char.IsNumber))
            .ToList();
        if (deadlineWords.Count == 0) return line;

        // Whole spoken words, so "ende" is not found in "Kalender". A prefix is enough to keep
        // "Freitag" when someone said "freitags" or "Freitagabend".
        var wasSaid = deadlineWords.Any(word => spoken.Any(s => s.StartsWith(word, StringComparison.Ordinal)));
        return wasSaid ? line : match.Groups[1].Value;
    }
}
