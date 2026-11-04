using System.Globalization;

namespace Diktat.AI.Internal;

/// Small stand-ins for Foundation's CharacterSet helpers, so the ported filters split and trim
/// text the way the macOS app does.
internal static class TextHelpers
{
    /// CharacterSet.letters: letters and marks.
    public static bool IsLetter(char c) => char.IsLetter(c) || IsMark(c);

    /// CharacterSet.alphanumerics: letters, marks and numbers.
    public static bool IsAlphanumeric(char c) => char.IsLetter(c) || IsMark(c) || char.IsNumber(c);

    private static bool IsMark(char c) => CharUnicodeInfo.GetUnicodeCategory(c) is
        UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    /// CharacterSet.whitespaces: spaces and tabs, no line breaks.
    public static bool IsHorizontalWhitespace(char c) => c == '\t' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;

    /// CharacterSet.newlines.
    public static readonly char[] Newlines = ['\n', '\u000B', '\u000C', '\r', '\u0085', '\u2028', '\u2029'];

    /// Splits at every character that is not accepted, dropping empty pieces.
    public static List<string> SplitKeeping(string text, Func<char, bool> keep)
    {
        var result = new List<string>();
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var inside = i < text.Length && keep(text[i]);
            if (inside && start < 0) start = i;
            else if (!inside && start >= 0)
            {
                result.Add(text[start..i]);
                start = -1;
            }
        }
        return result;
    }

    /// "Kanal, ja" -> ["kanal", "ja"]: lower case, split at everything that is not a letter or number.
    public static List<string> AlphanumericWords(string text) => SplitKeeping(text.ToLowerInvariant(), IsAlphanumeric);

    public static string Trim(string text, Func<char, bool> trims)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && trims(text[start])) start++;
        while (end > start && trims(text[end - 1])) end--;
        return text[start..end];
    }

    /// The bare name of a heading line: "## To-dos", "**Entscheidungen:**" -> "to-dos", "entscheidungen".
    public static string BareHeading(string line) =>
        Trim(line, c => c is '#' or '*' or ':' or ' ' || IsHorizontalWhitespace(c)).ToLowerInvariant();

    /// Swift's trimmingCharacters(in: .whitespacesAndNewlines).
    public static string TrimAll(string text) => text.Trim();
}
