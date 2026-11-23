using System.Globalization;
using System.Text.Json.Nodes;
using Diktat.Core.Localization;
using Diktat.Core.Settings;
using Diktat.Core.Text;

namespace Diktat.Core.Tests;

/// Tests that change the global switches (SwissSpelling.IsEnabled, L.Use) run alone, so other
/// tests never see them on.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GlobalSwitchesCollection
{
    public const string Name = "Global switches";
}

public sealed class SwissSpellingTests
{
    [Fact]
    public void SharpSBecomesDoubleS()
    {
        Assert.Equal("Die Strasse ist gross, wir schliessen um zehn.", SwissSpelling.Apply("Die Straße ist groß, wir schließen um zehn."));
        Assert.Equal("STRASSE", SwissSpelling.Apply("STRAẞE"));
    }

    [Fact]
    public void TextWithoutSharpSStaysAsItIs()
    {
        Assert.Equal("Massnahme, Kasse, Fluss", SwissSpelling.Apply("Massnahme, Kasse, Fluss"));
        Assert.Equal("", SwissSpelling.Apply(""));
    }

    [Fact]
    public void StartsOnInSwitzerlandAndLiechtenstein()
    {
        Assert.True(SwissSpelling.IsUsual("CH"));
        Assert.True(SwissSpelling.IsUsual("LI"));
        Assert.False(SwissSpelling.IsUsual("DE"));
        Assert.False(SwissSpelling.IsUsual("AT"));
        Assert.False(SwissSpelling.IsUsual(null));
    }

    [Fact]
    public void TheSettingDefaultsToTheRegion()
    {
        Assert.Equal(SwissSpelling.IsUsualHere, new AppSettings().SwissSpelling);
        // Settings saved before the setting existed get the region's default; a saved choice stays.
        Assert.Equal(SwissSpelling.IsUsualHere, SettingsStore.FromJson(JsonNode.Parse("""{"app":{}}""")!.AsObject()).App.SwissSpelling);
        Assert.False(SettingsStore.FromJson(JsonNode.Parse("""{"app":{"swissSpelling":false}}""")!.AsObject()).App.SwissSpelling);
        Assert.True(SettingsStore.FromJson(JsonNode.Parse("""{"app":{"swissSpelling":true}}""")!.AsObject()).App.SwissSpelling);
    }

    /// A dictation goes through the post-processor; with the setting on it comes out Swiss.
    [Fact]
    public void DictationsFollowTheSetting()
    {
        var processor = new TranscriptPostProcessor { CorrectCustomTerms = false, RemoveFillerWords = false };
        Assert.Equal("Wir treffen uns in der Bahnhofstraße.", processor.Process("Wir treffen uns in der Bahnhofstraße."));
        processor.SwissSpelling = true;
        Assert.Equal("Wir treffen uns in der Bahnhofstrasse.", processor.Process("Wir treffen uns in der Bahnhofstraße."));
    }

    [Fact]
    public void TheProcessorOfTheAppTakesTheSetting()
    {
        var settings = new SettingsContainer();
        settings.App.SwissSpelling = true;
        Assert.Equal("Das ist gross.", TranscriptPostProcessor.FromSettings(settings).Process("Das ist groß."));
        settings.App.SwissSpelling = false;
        Assert.Equal("Das ist groß.", TranscriptPostProcessor.FromSettings(settings).Process("Das ist groß."));
    }
}

[Collection(GlobalSwitchesCollection.Name)]
public sealed class SwissSpellingSwitchTests
{
    [Fact]
    public void OnlyWithTheSetting()
    {
        try
        {
            SwissSpelling.IsEnabled = false;
            Assert.Equal("groß", SwissSpelling.IfEnabled("groß"));
            SwissSpelling.IsEnabled = true;
            Assert.Equal("gross", SwissSpelling.IfEnabled("groß"));
            Assert.Equal("groß", SwissSpelling.IfEnabled("groß", enabled: false));
        }
        finally
        {
            SwissSpelling.IsEnabled = false;
        }
    }

    [Fact]
    public void TheGermanInterfaceCanTakeTheSwissSpelling()
    {
        try
        {
            L.Use(UiLanguage.German, swissSpelling: true);
            Assert.True(L.SwissSpelling);
            Assert.Equal("Grösse", L.T("Größe"));
            Assert.Equal("Die Datei ist 3 MB gross.", L.F("Die Datei ist {0} MB groß.", 3));
            Assert.Equal("Verlauf", L.T("Verlauf"));

            // English has no variant.
            L.Use(UiLanguage.English, swissSpelling: true);
            Assert.False(L.SwissSpelling);
        }
        finally
        {
            L.Use(UiLanguage.German);
        }
        Assert.Equal("Größe", L.T("Größe"));
    }
}

public sealed class SwissInterfaceTests
{
    private static CultureInfo C(string name) => CultureInfo.GetCultureInfo(name);

    /// Like the Mac app's de-CH variant: only "Automatisch" with a German and Swiss Windows.
    [Fact]
    public void TheSwissVariantFollowsWindows()
    {
        Assert.True(L.UsesSwissSpelling(AppLanguage.System, C("de-CH"), "DE"));
        Assert.True(L.UsesSwissSpelling(AppLanguage.System, C("de-DE"), "CH"));
        Assert.False(L.UsesSwissSpelling(AppLanguage.System, C("de-DE"), "DE"));
        Assert.False(L.UsesSwissSpelling(AppLanguage.System, C("de-AT"), "AT"));
        // Liechtenstein has no variant of its own on the Mac either.
        Assert.False(L.UsesSwissSpelling(AppLanguage.System, C("de-DE"), "LI"));
        // A French or English Windows in Switzerland: German is a fallback or English is shown.
        Assert.False(L.UsesSwissSpelling(AppLanguage.System, C("fr-CH"), "CH"));
        Assert.False(L.UsesSwissSpelling(AppLanguage.System, C("en-US"), "CH"));
        // A language picked in the app is plain German or English.
        Assert.False(L.UsesSwissSpelling(AppLanguage.German, C("de-CH"), "CH"));
        Assert.False(L.UsesSwissSpelling(AppLanguage.English, C("de-CH"), "CH"));
    }

    [Fact]
    public void TranslatesWithTheSwissSpelling()
    {
        Assert.Equal("Grösse", L.Translate("Größe", UiLanguage.German, swissSpelling: true));
        Assert.Equal("Größe", L.Translate("Größe", UiLanguage.German));
        // English texts stay as they are; a German text without English keeps its sharp s.
        Assert.Equal("History", L.Translate("Verlauf", UiLanguage.English, swissSpelling: true));
        Assert.Equal("Kein Text, groß", L.Translate("Kein Text, groß", UiLanguage.English, swissSpelling: true));
    }
}
