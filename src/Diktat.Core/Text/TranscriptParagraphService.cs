using System.Text;
using System.Text.RegularExpressions;

namespace Diktat.Core.Text;

/// Formats transcript whitespace without rewriting words or punctuation: long dictation gets a
/// blank line at a sentence end after 45 words or four substantial sentences.
///
/// Contributed by dan-rot (MIT) to the macOS app. The macOS version uses the NaturalLanguage
/// sentence tokenizer; this one uses <see cref="SentenceSplitter"/>.
public static class TranscriptParagraphService
{
    private static readonly Regex ParagraphSeparator = new(@"\r?\n[\t ]*\r?\n(?:[\t ]*\r?\n)*", RegexOptions.CultureInvariant);

    public static string Format(string text, string language = "")
    {
        if (string.IsNullOrEmpty(text)) return text;
        // Keep existing paragraph separators exactly, and restart counting after each one.
        var result = new StringBuilder(text.Length + 16);
        var start = 0;
        foreach (Match match in ParagraphSeparator.Matches(text))
        {
            result.Append(FormatParagraph(text[start..match.Index], language));
            result.Append(match.Value);
            start = match.Index + match.Length;
        }
        result.Append(FormatParagraph(text[start..], language));
        return result.ToString();
    }

    private static string FormatParagraph(string text, string language)
    {
        var ranges = SentenceSplitter.Split(text, language);
        if (ranges.Count <= 1) return text;

        var result = new StringBuilder(text.Length + 16);
        var copyStart = 0;
        var wordCount = 0;
        var substantialSentences = 0;
        for (var index = 0; index < ranges.Count - 1; index++)
        {
            var (lower, upper) = ranges[index];
            var sentenceWords = SentenceSplitter.CountWords(text, lower, upper);
            wordCount += sentenceWords;
            if (sentenceWords >= 4) substantialSentences++;
            if (wordCount < 45 && substantialSentences < 4) continue;

            var end = upper;
            while (end > lower && char.IsWhiteSpace(text[end - 1])) end--;
            // A boundary at a newline alone must not split an unfinished sentence.
            var ending = text[lower..end].Trim('"', '\'', '“', '”', '„', '«', '»', '‹', '›', '‘', '’', ')', ']', '}');
            if (ending.Length == 0 || !".!?…。！？".Contains(ending[^1])) continue;

            var next = ranges[index + 1].Start;
            while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
            if (next < end) continue;
            var gapIsWhitespace = true;
            for (var i = end; i < next; i++) gapIsWhitespace &= char.IsWhiteSpace(text[i]);
            if (!gapIsWhitespace) continue;
            result.Append(text, copyStart, end - copyStart);
            result.Append("\n\n");
            copyStart = next;
            wordCount = 0;
            substantialSentences = 0;
        }
        result.Append(text, copyStart, text.Length - copyStart);
        return result.ToString();
    }
}

/// A rule-based sentence splitter, standing in for the NaturalLanguage tokenizer of macOS.
/// Sentences end after ".", "!", "?" or "…" (plus closing quotes and brackets) followed by
/// whitespace, after "。！？", and at line breaks. A period does not end a sentence after a known
/// abbreviation ("Dr.", "z. B."), a single letter, a number of up to two digits ("am 3. Mai") or
/// when the next word starts lowercase. The ranges cover the whole text, each sentence with the
/// whitespace after it.
public static class SentenceSplitter
{
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        // German
        "dr", "prof", "hr", "fr", "frl", "nr", "str", "bzw", "usw", "ca", "vgl", "ggf", "inkl", "exkl",
        "evtl", "etc", "bspw", "zb", "z.b", "d.h", "u.a", "o.ä", "s", "abs", "abt", "allg", "anm",
        "bzgl", "chr", "dipl", "ing", "jh", "jhd", "mio", "mrd", "tel", "zzgl", "zz", "zzt",
        "st", "geb", "gest", "sog", "lic", "tsd", "ehem", "eigtl", "insb",
        // English
        "mr", "mrs", "ms", "jr", "sr", "vs", "e.g", "i.e", "approx", "dept", "fig", "inc", "ltd",
        "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec",
    };

    public static IReadOnlyList<(int Start, int End)> Split(string text, string language = "")
    {
        var ranges = new List<(int, int)>();
        var start = 0;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            int boundary = -1;
            if (TextScan.IsNewline(c))
            {
                boundary = i + 1;
            }
            else if ("。！？".Contains(c))
            {
                boundary = i + 1;
            }
            else if (".!?…".Contains(c))
            {
                var j = i;
                while (j < text.Length && ".!?…".Contains(text[j])) j++;
                while (j < text.Length && "\"'“”«»‹›’)]}".Contains(text[j])) j++;
                if ((j >= text.Length || char.IsWhiteSpace(text[j])) && EndsSentence(text, i, j))
                {
                    boundary = j;
                }
                i = j - 1;
            }

            if (boundary >= 0)
            {
                var end = boundary;
                // The whitespace after the sentence belongs to it, up to and including a line break.
                if (!TextScan.IsNewline(text[boundary - 1]))
                {
                    while (end < text.Length && char.IsWhiteSpace(text[end]) && !TextScan.IsNewline(text[end])) end++;
                    if (end < text.Length && TextScan.IsNewline(text[end])) end++;
                }
                while (end < text.Length && TextScan.IsNewline(text[end])) end++;
                if (HasContent(text, start, end)) ranges.Add((start, end));
                else if (ranges.Count > 0) ranges[^1] = (ranges[^1].Item1, end);
                else ranges.Add((start, end));
                start = end;
                i = end;
                continue;
            }
            i++;
        }
        if (start < text.Length)
        {
            if (HasContent(text, start, text.Length) || ranges.Count == 0) ranges.Add((start, text.Length));
            else ranges[^1] = (ranges[^1].Item1, text.Length);
        }
        return ranges;
    }

    /// Words in a range: runs of letters and digits, apostrophes inside a word included.
    public static int CountWords(string text, int start, int end)
    {
        var count = 0;
        var inWord = false;
        for (var i = start; i < end;)
        {
            var rune = TextScan.RuneAt(text, i, out var length);
            var isWordChar = TextScan.IsLetterOrNumber(rune)
                || (inWord && (TextScan.IsMark(rune) || ((text[i] == '\'' || text[i] == '’') && i + 1 < end && char.IsLetter(text[i + 1]))));
            if (isWordChar && !inWord) count++;
            inWord = isWordChar;
            i += length;
        }
        return count;
    }

    private static bool HasContent(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (!char.IsWhiteSpace(text[i])) return true;
        }
        return false;
    }

    /// Whether the punctuation run from <paramref name="markStart"/> to <paramref name="markEnd"/> ends a sentence.
    private static bool EndsSentence(string text, int markStart, int markEnd)
    {
        // Only a single period can belong to an abbreviation.
        if (text[markStart] != '.' || markEnd - markStart > 1 && text[markStart + 1] == '.') return true;

        // The word before the period, with inner periods ("z.B", "d.h").
        var wordStart = markStart;
        while (wordStart > 0 && (char.IsLetterOrDigit(text[wordStart - 1]) || text[wordStart - 1] == '.')) wordStart--;
        var word = text[wordStart..markStart];
        if (word.Length > 0)
        {
            if (Abbreviations.Contains(word)) return false;
            if (word.Length == 1 && char.IsLetter(word[0])) return false;
            if (word.Length <= 2 && word.All(char.IsDigit)) return false;
            if (word.Contains('.')) return false;
        }

        // The next word starts lowercase: the sentence goes on.
        var next = markEnd;
        while (next < text.Length && char.IsWhiteSpace(text[next]) && !TextScan.IsNewline(text[next])) next++;
        if (next < text.Length && char.IsLetter(text[next]) && char.IsLower(text[next])) return false;
        return true;
    }
}
