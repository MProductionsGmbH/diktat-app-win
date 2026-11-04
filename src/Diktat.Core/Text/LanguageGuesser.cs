namespace Diktat.Core.Text;

/// Tells German from other languages by common short words, as a stand-in for the language
/// recognizer of macOS. Too little text counts as German.
public static class LanguageGuesser
{
    private static readonly HashSet<string> German = new(StringComparer.OrdinalIgnoreCase)
    {
        "der", "die", "das", "und", "ist", "nicht", "ich", "du", "sie", "wir", "ihr", "ein", "eine",
        "einen", "zu", "mit", "auf", "für", "von", "den", "dem", "des", "es", "auch", "noch", "aber",
        "wie", "was", "wenn", "dass", "bitte", "danke", "hallo", "morgen", "heute", "habe", "hast",
        "hat", "haben", "bin", "bist", "sind", "kannst", "kann", "können", "mir", "mich", "dir", "dich",
        "uns", "euch", "schon", "doch", "mal", "jetzt", "hier", "oder", "im", "am", "vom", "zum", "zur",
        // Added for the Windows port: more German-only words, so short texts are told apart.
        "ja", "nein", "gut", "sehr", "wird", "werden", "war", "waren", "sich", "nur", "kein", "keine",
        "einem", "einer", "über", "nach", "bei", "aus", "wo", "warum", "weil", "denn", "dann", "sein",
        "seine", "mein", "meine", "dein", "deine", "gerne", "gern", "vielleicht", "machen", "macht",
        "geht", "kommt", "uhr", "zehn", "um", "er", "man", "diese", "dieser", "wieder", "immer",
        "schreib", "schreibe", "treffen", "danach", "dort", "da", "so", "in", "an",
    };

    private static readonly HashSet<string> English = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "is", "are", "not", "you", "we", "they", "a", "an", "to", "with", "on", "for",
        "of", "it", "also", "but", "how", "what", "if", "that", "please", "thanks", "hello", "tomorrow",
        "today", "have", "has", "am", "can", "could", "me", "my", "your", "our", "just", "now", "here",
        "or", "in", "at", "from", "this", "will", "would", "should", "there", "be", "was", "were", "i",
        // Added for the Windows port.
        "us", "find", "more", "available", "reach", "write", "look", "do", "does", "don't", "i'm",
        "it's", "let's", "very", "all", "some", "any", "about", "after", "before", "because", "when",
        "where", "who", "which", "think", "know", "want", "need", "meet", "see", "get", "go", "going",
        "said", "say", "make", "time", "good", "great", "yes", "send", "so",
        "contact", "visit", "check", "out", "him", "her", "them", "their", "his", "she", "he",
    };

    public static bool IsGermanOrUnknown(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (HasGermanLetters(text)) return true;

        var (german, other) = Count(text);
        if (german + other < 2) return true;
        return german >= other;
    }

    /// True when the text reads as English: more English-only than German-only words. Used where
    /// the macOS app asks its language recognizer for English (e.g. "find us at example.com",
    /// where "at" is a word and not a spoken "@"). Unknown text is not English.
    public static bool IsLikelyEnglish(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || HasGermanLetters(text)) return false;
        var (german, english) = Count(text);
        return english >= 1 && english > german;
    }

    private static bool HasGermanLetters(string text) => text.IndexOfAny(['ä', 'ö', 'ü', 'ß', 'Ä', 'Ö', 'Ü']) >= 0;

    /// Hits of words that are only German and only English; words of both languages ("in",
    /// "was", "am") count for neither.
    private static (int German, int English) Count(string text)
    {
        int german = 0, english = 0;
        foreach (var raw in Words(text))
        {
            var word = raw.Replace('’', '\'');
            var isGerman = German.Contains(word);
            var isEnglish = English.Contains(word);
            if (isGerman && !isEnglish) german++;
            else if (isEnglish && !isGerman) english++;
        }
        return (german, english);
    }

    private static IEnumerable<string> Words(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var isLetter = i < text.Length && (char.IsLetter(text[i]) || text[i] == '\'' || text[i] == '’');
            if (isLetter && start < 0) start = i;
            else if (!isLetter && start >= 0)
            {
                yield return text[start..i];
                start = -1;
            }
        }
    }
}
