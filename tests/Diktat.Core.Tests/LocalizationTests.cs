using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Diktat.Core.Localization;

namespace Diktat.Core.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void ResolvesTheAppLanguage()
    {
        Assert.Equal(UiLanguage.German, L.Resolve(AppLanguage.German, CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal(UiLanguage.English, L.Resolve(AppLanguage.English, CultureInfo.GetCultureInfo("de-CH")));
        Assert.Equal(UiLanguage.English, L.Resolve(AppLanguage.System, CultureInfo.GetCultureInfo("en-GB")));
        Assert.Equal(UiLanguage.German, L.Resolve(AppLanguage.System, CultureInfo.GetCultureInfo("de-CH")));
        // Only German and English exist: any other Windows language gets German, like on the Mac.
        Assert.Equal(UiLanguage.German, L.Resolve(AppLanguage.System, CultureInfo.GetCultureInfo("fr-FR")));
    }

    [Fact]
    public void TranslatesAndKeepsUnknownTextsGerman()
    {
        Assert.Equal("Verlauf", L.Translate("Verlauf", UiLanguage.German));
        Assert.Equal("History", L.Translate("Verlauf", UiLanguage.English));
        Assert.Equal("Kein solcher Text 123", L.Translate("Kein solcher Text 123", UiLanguage.English));
    }

    [Fact]
    public void EveryEnglishTextKeepsThePlaceholders()
    {
        foreach (var (german, english) in L.English)
        {
            Assert.False(string.IsNullOrWhiteSpace(english), $"Empty English text for «{german}»");
            Assert.Equal(Placeholders(german), Placeholders(english));
        }
    }

    /// Every text the code shows (L.T, L.F, {l:T '…'} in XAML) has its English version.
    [Fact]
    public void EveryTextOfTheAppHasAnEnglishVersion()
    {
        var missing = UsedTexts().Where(text => !L.English.ContainsKey(text)).Distinct().Order(StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0, "No English text for:\n" + string.Join("\n", missing));
    }

    /// No visible text in XAML is left without {l:T …}.
    [Fact]
    public void XamlHasNoUntranslatedTexts()
    {
        var attribute = new Regex("""\b(Text|Content|Header|ToolTip|Title|AutomationProperties\.Name|Tag)="([^"{][^"]*)"(?<!=" ")""");
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(SourceRoot(), "*.xaml", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match match in attribute.Matches(lines[i]))
                {
                    var value = System.Net.WebUtility.HtmlDecode(match.Groups[2].Value);
                    if (!Regex.IsMatch(value, "[A-Za-zÄÖÜäöü]{2}") || AllowedLiterals.Contains(value) || value.StartsWith("http")) continue;
                    if (match.Groups[1].Value == "Tag" && !value.Contains(' ')) continue;
                    found.Add($"{Path.GetFileName(file)}:{i + 1} {match.Groups[1].Value}=\"{value}\"");
                }
            }
        }
        Assert.True(found.Count == 0, "Texts without {l:T …}:\n" + string.Join("\n", found));
    }

    /// Names that are the same in both languages.
    private static readonly HashSet<string> AllowedLiterals = ["Diktat", "WTF", ":-)", "Parakeet", "Whisper", "Ollama", "LM Studio", "GitHub", "OK", "Mail", "Diktat.ico"];

    private static IEnumerable<string> UsedTexts()
    {
        var code = new Regex("""\bL\.[TF]\(\s*"((?:[^"\\]|\\.)*)"[\s,)]""");
        var xaml = new Regex("""\{l:T\s+(?:Text=)?'((?:[^'\\]|\\.)*)'\s*\}""");
        foreach (var file in Directory.EnumerateFiles(SourceRoot(), "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            if (file.EndsWith(".cs"))
            {
                foreach (Match m in code.Matches(File.ReadAllText(file))) yield return Regex.Unescape(m.Groups[1].Value);
            }
            else if (file.EndsWith(".xaml"))
            {
                foreach (Match m in xaml.Matches(File.ReadAllText(file))) yield return XamlUnescape(m.Groups[1].Value);
            }
        }
    }

    private static string XamlUnescape(string value) =>
        System.Net.WebUtility.HtmlDecode(Regex.Replace(value, @"\\(.)", "$1"));

    private static string Placeholders(string text) =>
        string.Join(",", Regex.Matches(text, @"\{(\d+)(?:[:,][^}]*)?\}").Select(m => m.Groups[1].Value).Distinct().Order());

    private static string SourceRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src"));
}
