using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Diktat.Core.Localization;

/// The language of the interface, picked under Einstellungen › Allgemein › Sprache. It is
/// independent of the dictation language and applies after a restart, like on the Mac.
public enum AppLanguage
{
    /// The app follows Windows: English with an English Windows, German with any other.
    System,
    German,
    English,
}

/// The language the texts are shown in right now.
public enum UiLanguage
{
    German,
    English,
}

/// The texts of the app. They are written in German in the code and are the keys of
/// Localization/en.json, which holds the English version (taken from the Mac app's
/// Localizable.xcstrings where the text is the same).
///
/// Rules: translate where a text is shown, not in static fields (the smoke test switches the
/// language while the app runs); never translate text that goes to a language model, file
/// names, settings keys or log lines.
public static class L
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> EnglishTexts = new(LoadEnglish);

    /// German until <see cref="Use"/> is called at startup; tests run in German.
    public static UiLanguage Current { get; private set; } = UiLanguage.German;

    /// Dates and numbers follow the language of the texts.
    public static CultureInfo Culture => CultureFor(Current);

    public static CultureInfo CultureFor(UiLanguage language) =>
        CultureInfo.GetCultureInfo(language == UiLanguage.English ? "en-US" : "de-CH");

    public static IReadOnlyDictionary<string, string> English => EnglishTexts.Value;

    /// Whether the German texts are shown with "ss" instead of the sharp s (the Mac app's de-CH
    /// variant). Set at startup with <see cref="Use"/>; tests run without it.
    public static bool SwissSpelling { get; private set; }

    public static void Use(UiLanguage language, bool swissSpelling = false)
    {
        Current = language;
        SwissSpelling = swissSpelling && language == UiLanguage.German;
    }

    /// The language a setting means; "System" follows the Windows display language
    /// (only German and English exist, every other language falls back to German).
    public static UiLanguage Resolve(AppLanguage setting, CultureInfo? system = null) => setting switch
    {
        AppLanguage.German => UiLanguage.German,
        AppLanguage.English => UiLanguage.English,
        _ => (system ?? CultureInfo.InstalledUICulture).TwoLetterISOLanguageName == "en" ? UiLanguage.English : UiLanguage.German,
    };

    /// Whether the German texts take the Swiss spelling, like the Mac app's de-CH variant: only
    /// with "Automatisch" when Windows itself is German and Swiss, that is the display language
    /// is de-CH or German with the region Switzerland. A fallback to German (French Windows in
    /// Switzerland) and the choice "Deutsch" keep the sharp s. Independent of the setting
    /// "Schweizer Schreibweise", which is about what Diktat writes.
    public static bool UsesSwissSpelling(AppLanguage setting, CultureInfo? system = null, string? region = null)
    {
        if (setting != AppLanguage.System) return false;
        var culture = system ?? CultureInfo.InstalledUICulture;
        if (culture.TwoLetterISOLanguageName != "de") return false;
        if (culture.Name.EndsWith("-CH", StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(region ?? CurrentRegion(), "CH", StringComparison.OrdinalIgnoreCase);
    }

    private static string? CurrentRegion()
    {
        try { return RegionInfo.CurrentRegion.TwoLetterISORegionName; }
        catch (ArgumentException) { return null; }
    }

    /// The text in the current language.
    public static string T(string german) => Translate(german, Current, SwissSpelling);

    /// A text with placeholders ({0}, {1}) in the current language.
    public static string F(string germanFormat, params object?[] args) =>
        string.Format(Culture, T(germanFormat), args);

    /// The text in <paramref name="language"/>; a text without translation stays German. The
    /// German text takes "ss" for the sharp s when <paramref name="swissSpelling"/> is set.
    public static string Translate(string german, UiLanguage language, bool swissSpelling = false)
    {
        if (language == UiLanguage.English && English.TryGetValue(german, out var english)) return english;
        return swissSpelling && language == UiLanguage.German ? Diktat.Core.Text.SwissSpelling.Apply(german) : german;
    }

    private static IReadOnlyDictionary<string, string> LoadEnglish()
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream("Diktat.Core.Localization.en.json");
        if (stream is null) return new Dictionary<string, string>();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
    }
}
