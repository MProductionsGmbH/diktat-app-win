using System.Buffers;
using System.Globalization;
using System.Text;

namespace Diktat.Core.Text;

/// Character helpers that behave like Swift's Character properties on UTF-16 text:
/// surrogate pairs count as one character and combining marks belong to the letter before.
internal static class TextScan
{
    /// The rune at <paramref name="index"/> and how many UTF-16 units it takes.
    public static Rune RuneAt(string text, int index, out int length)
    {
        if (Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out length) != OperationStatus.Done)
        {
            length = 1;
            return Rune.ReplacementChar;
        }
        return rune;
    }

    public static bool IsMark(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
    }

    public static bool IsLetterOrNumber(Rune rune) => Rune.IsLetter(rune) || Rune.IsNumber(rune);

    public static bool IsNewline(char c) => c is '\n' or '\r' or '\u000B' or '\u000C' or '\u0085' or '\u2028' or '\u2029';

    /// Letters (with their combining marks) of a word, lowercased.
    public static string LettersLowercased(string text, int start, int end)
    {
        var builder = new StringBuilder(end - start);
        var previousWasLetter = false;
        for (var i = start; i < end;)
        {
            var rune = RuneAt(text, i, out var length);
            if (Rune.IsLetter(rune) || (previousWasLetter && IsMark(rune)))
            {
                builder.Append(text, i, length);
                previousWasLetter = true;
            }
            else
            {
                previousWasLetter = false;
            }
            i += length;
        }
        return builder.ToString().ToLowerInvariant();
    }

    /// True when every character of the range is a letter (combining marks included).
    public static bool AllLetters(string text, int start, int end)
    {
        if (start >= end) return true;
        for (var i = start; i < end;)
        {
            var rune = RuneAt(text, i, out var length);
            if (!Rune.IsLetter(rune) && !(i > start && IsMark(rune))) return false;
            i += length;
        }
        return true;
    }

    /// The text with its first character uppercased, when that character is lowercase.
    public static string UppercasingFirst(string text)
    {
        if (text.Length == 0) return text;
        var rune = RuneAt(text, 0, out var length);
        if (!Rune.IsLower(rune)) return text;
        return text[..length].ToUpperInvariant() + text[length..];
    }

    /// Number of user-perceived characters (grapheme clusters).
    public static int CharacterCount(string text) => new StringInfo(text).LengthInTextElements;
}
