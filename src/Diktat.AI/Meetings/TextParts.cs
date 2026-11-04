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
}
