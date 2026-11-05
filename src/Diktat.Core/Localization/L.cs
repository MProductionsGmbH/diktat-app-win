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

    public static void Use(UiLanguage language) => Current = language;

    /// The language a setting means; "System" follows the Windows display language
    /// (only German and English exist, every other language falls back to German).
    public static UiLanguage Resolve(AppLanguage setting, CultureInfo? system = null) => setting switch
    {
        AppLanguage.German => UiLanguage.German,
        AppLanguage.English => UiLanguage.English,
        _ => (system ?? CultureInfo.InstalledUICulture).TwoLetterISOLanguageName == "en" ? UiLanguage.English : UiLanguage.German,
    };

    /// The text in the current language.
    public static string T(string german) => Translate(german, Current);

    /// A text with placeholders ({0}, {1}) in the current language.
    public static string F(string germanFormat, params object?[] args) =>
        string.Format(Culture, T(germanFormat), args);

    /// The text in <paramref name="language"/>; a text without translation stays German.
    public static string Translate(string german, UiLanguage language) =>
        language == UiLanguage.English && English.TryGetValue(german, out var english) ? english : german;

    private static IReadOnlyDictionary<string, string> LoadEnglish()
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream("Diktat.Core.Localization.en.json");
        if (stream is null) return new Dictionary<string, string>();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
    }
}
