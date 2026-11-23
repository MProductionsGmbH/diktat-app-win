using System.Text.RegularExpressions;
using Diktat.AI.Internal;

namespace Diktat.AI.Meetings;

/// Cuts transcript text for a model that reads a limited number of characters at once: after a
/// whole line ("Name: ...") when possible, otherwise after a word.
public static class TextParts
{
    /// Where the first part of the text within the limit ends (an index into the text).
    public static int EndOfLeadingPart(string text, int limit)
    {
        if (text.Length <= limit) return text.Length;
        var head = text[..limit];
        var newline = head.LastIndexOf('\n');
        if (newline > 0) return newline + 1;
        var space = head.LastIndexOf(' ');
        if (space > 0) return space + 1;
        return limit;
    }

    /// Where the last part of the text within the limit starts (an index into the text).
    public static int StartOfTrailingPart(string text, int limit)
    {
        if (text.Length <= limit) return 0;
        var cut = text.Length - limit;
        var newline = text.IndexOf('\n', cut);
        return newline >= 0 ? newline + 1 : cut;
    }
}

/// Some fonts kern a hyphen and the space after it so tightly that "- Punkt" reads as "-Punkt".
/// Summaries and notes therefore show their bullet points with "•"; copied text keeps "- ",
/// which mail and notes apps read as a list.
public static class ListDisplay
{
    public static string Bullets(string text) =>
        string.Join("\n", text.Split('\n').Select(line => line.StartsWith("- ", StringComparison.Ordinal) ? "• " + line[2..] : line));

    /// The headings a summary and the live notes are made of, in both languages.
    private static readonly HashSet<string> Headings =
    [
        "zusammenfassung", "summary", "entscheidungen", "decisions", "to-dos", "todos", "to-do",
        "themen", "topics", "offene fragen", "open questions",
    ];

    private static readonly Regex MarkdownHeading = new(@"^\s{0,3}#{1,6}\s+", RegexOptions.CultureInvariant);
    private static readonly Regex StarBold = new(@"\*\*(.+?)\*\*", RegexOptions.CultureInvariant);
    private static readonly Regex UnderscoreBold = new(@"__(.+?)__", RegexOptions.CultureInvariant);

    private static string TrimHeading(string line) => TextHelpers.Trim(line, c => c is ':' or ' ' || TextHelpers.IsHorizontalWhitespace(c));

    /// Whether a line is one of the headings, with or without a colon.
    public static bool IsHeading(string line) => Headings.Contains(TrimHeading(line).ToLowerInvariant());

    /// Without the Markdown marks a small model adds on its own: "**To-dos**", "## Themen",
    /// "- **Paul:** Angebot schicken". Bullet points and everything else stay as they are.
    public static string Plain(string text) => string.Join("\n", text.Split('\n').Select(line =>
    {
        line = MarkdownHeading.Replace(line, "");
        line = StarBold.Replace(line, "$1");
        line = UnderscoreBold.Replace(line, "$1");
        return IsHeading(line) ? TrimHeading(line) : line;
    }));

    public readonly record struct Line(string Text, bool IsHeading);

    /// What a card shows: no Markdown marks, "•" for bullet points, headings marked as such.
    /// <param name="droppingTitle">A first heading that only repeats the title of the card
    /// ("Zusammenfassung" under "Zusammenfassung") is left out.</param>
    public static List<Line> Lines(string text, string? droppingTitle = null)
    {
        var lines = Bullets(Plain(text)).Split('\n').Select(line => new Line(line, IsHeading(line))).ToList();
        if (droppingTitle is not null)
        {
            var first = lines.FindIndex(line => line.Text.Trim().Length > 0);
            if (first >= 0 && lines[first].IsHeading && string.Equals(lines[first].Text, droppingTitle, StringComparison.OrdinalIgnoreCase))
            {
                lines.RemoveAt(first);
            }
        }
        return lines;
    }

    /// The title of the summary card in the language of the summary.
    public static string SummaryTitle(string summary) => summary.Contains("Summary", StringComparison.Ordinal) ? "Summary" : "Zusammenfassung";
}
