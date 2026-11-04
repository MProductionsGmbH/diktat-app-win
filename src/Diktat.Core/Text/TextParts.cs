using System.Globalization;

namespace Diktat.Core.Text;

/// Cuts transcript text for a model that reads a limited number of characters at once: after a
/// whole line ("Name: ...") when possible, otherwise after a word. Limits count user-perceived
/// characters, like Swift's String.count; the results are UTF-16 indices into the string.
public static class TextParts
{
    /// Where the first part of <paramref name="text"/> within <paramref name="limit"/> characters ends.
    public static int EndOfLeadingPart(string text, int limit)
    {
        var starts = StringInfo.ParseCombiningCharacters(text);
        if (starts.Length <= limit) return text.Length;
        var cut = starts[Math.Max(limit, 0)];
        var newline = text.LastIndexOf('\n', Math.Max(cut - 1, 0), cut);
        if (newline > 0) return newline + 1;
        var space = text.LastIndexOf(' ', Math.Max(cut - 1, 0), cut);
        if (space > 0) return space + 1;
        return cut;
    }

    /// Where the last part of <paramref name="text"/> within <paramref name="limit"/> characters starts.
    public static int StartOfTrailingPart(string text, int limit)
    {
        var starts = StringInfo.ParseCombiningCharacters(text);
        if (starts.Length <= limit) return 0;
        var cut = starts[starts.Length - Math.Max(limit, 0)];
        var newline = text.IndexOf('\n', cut);
        return newline >= 0 ? newline + 1 : cut;
    }
}

/// Summaries and notes show their bullet points with "•", because the UI font kerns "- Punkt"
/// into "-Punkt"; copied text keeps "- ", which mail and notes apps read as a list.
public static class ListDisplay
{
    public static string Bullets(string text) =>
        string.Join("\n", text.Split('\n').Select(line => line.StartsWith("- ", StringComparison.Ordinal) ? "• " + line[2..] : line));
}
