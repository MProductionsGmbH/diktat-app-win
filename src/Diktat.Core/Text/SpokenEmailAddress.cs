using System.Text;
using System.Text.RegularExpressions;

namespace Diktat.Core.Text;

/// Writes dictated e-mail addresses as addresses: "test at outlook punkt com" or
/// "max punkt meier ät firma.de" become "test@outlook.com" and "max.meier@firma.de". Only text
/// that ends in a known top-level domain is touched, so an "at" in a normal sentence stays.
public static class SpokenEmailAddress
{
    private static readonly string[] TopLevelDomains =
    [
        "com", "de", "net", "org", "io", "eu", "at", "ch", "info", "biz", "co", "uk", "fr", "it",
        "es", "nl", "be", "dk", "se", "no", "fi", "pl", "cz", "app", "ai", "dev", "me", "tv", "email",
    ];

    /// Words that make the next "Punkt" the noun at the start of a sentence ("Letzter Punkt"), so
    /// they cannot become the first part of an address. Inside a sentence the case tells: a
    /// lowercase word before a capital "Punkt" ("ein wichtiger Punkt").
    private static readonly string[] NounMarkers =
    [
        "der", "den", "dem", "des", "ein", "einen", "einem", "kein", "keinen", "dieser", "diesen",
        "jeder", "jeden", "welcher", "erster", "zweiter", "dritter", "letzter", "nächster",
        "wichtiger", "weiterer", "anderer", "neuer", "offener", "springender", "toter",
    ];

    private static readonly Regex Pattern = BuildPattern();

    private static Regex BuildPattern()
    {
        // A label: letters, digits and the characters addresses use inside a name.
        const string label = @"[\p{L}\p{N}_%+-]+";
        const string spokenDot = @"\s+(?:punkt|dot)\s+";
        var markers = string.Join("|", NounMarkers);
        // Before the "at" a written dot must not have spaces, or a sentence end would join the
        // address. A label followed by a spoken dot must not make it the noun "Punkt".
        var noun = $@"(?:{markers}){spokenDot}|(?-i:\p{{Ll}}[\p{{L}}\p{{N}}_%+-]*\s+Punkt\s)";
        var local = $@"(?:(?!{noun}){label}(?:{spokenDot}|\.))*{label}";
        const string at = @"\s+(?:at|ät)\s+|\s*@\s*";
        var tld = string.Join("|", TopLevelDomains);
        // Recognizers sometimes put a space next to the last dot ("outlook. com"). After such a
        // dot the top-level domain has to be lowercase or end the text, because a capital letter
        // there usually starts the next sentence ("at this. It is").
        var spacedEnd = $@"(?:\s\.\s?|\.\s)(?:(?-i:(?:{tld}))|(?:{tld})(?=[.!?]?\s*$))";
        var domain = $@"{label}(?:(?:{spokenDot}|\.){label})*(?:(?:{spokenDot}|\.)(?:{tld})|{spacedEnd})";
        var address = $@"(?<![\p{{L}}\p{{N}}_%+.@-])({local})({at})({domain})(?![\p{{L}}\p{{N}}])";
        return new Regex(address, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static readonly Regex AnyDot = new(@"\s+(?:punkt|dot)\s+|\s*\.\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Format(string text)
    {
        var matches = Pattern.Matches(text);
        if (matches.Count == 0) return text;

        // A plain "at" is an ordinary word in English ("find us at example.com"), so it only
        // counts outside English text. The text around the addresses decides; a dictation that
        // is just an address counts as one.
        var english = IsEnglish(matches, text);

        var result = new StringBuilder(text);
        // Back to front, so earlier ranges stay valid.
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var match = matches[i];
            var local = match.Groups[1].Value;
            var at = match.Groups[2].Value;
            var domain = match.Groups[3].Value;
            if (english && at.Trim().Equals("at", StringComparison.OrdinalIgnoreCase)) continue;
            var address = (Joined(local) + "@" + Joined(domain)).ToLowerInvariant();
            result.Remove(match.Index, match.Length).Insert(match.Index, address);
        }
        return result.ToString();
    }

    /// "max punkt meier" or "outlook . com" without spaces and with written dots.
    private static string Joined(string part) => AnyDot.Replace(part, ".").Replace(" ", "");

    private static bool IsEnglish(MatchCollection matches, string text)
    {
        var context = new StringBuilder(text);
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            context.Remove(matches[i].Index, matches[i].Length).Insert(matches[i].Index, " ");
        }
        return LanguageGuesser.IsLikelyEnglish(context.ToString());
    }
}
