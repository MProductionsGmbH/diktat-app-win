using System.Text.Json;
using Diktat.Core.Settings;
using Diktat.Core.Text;

namespace Diktat.Core.Tests;

public class StyleContextTests
{
    [Fact]
    public void KnownAppsFallIntoTheirContext()
    {
        Assert.Equal(StyleContext.Personal, StyleContextInfo.Detect("WhatsApp.exe", null));
        Assert.Equal(StyleContext.Personal, StyleContextInfo.Detect("whatsapp.root.exe", null));
        Assert.Equal(StyleContext.Work, StyleContextInfo.Detect("slack.exe", null));
        Assert.Equal(StyleContext.Work, StyleContextInfo.Detect("ms-teams.exe", null));
        Assert.Equal(StyleContext.Email, StyleContextInfo.Detect("OUTLOOK.EXE", null));
        Assert.Equal(StyleContext.Email, StyleContextInfo.Detect("olk.exe", null));
        Assert.Equal(StyleContext.Email, StyleContextInfo.Detect(@"C:\Program Files\eM Client\MailClient.exe", null));
        Assert.Equal(StyleContext.Other, StyleContextInfo.Detect("notepad.exe", null));
        Assert.Equal(StyleContext.Other, StyleContextInfo.Detect(null, null));
    }

    [Fact]
    public void WebsiteInABrowserWinsOverTheBrowser()
    {
        Assert.Equal(StyleContext.Email, StyleContextInfo.Detect("chrome.exe", "mail.google.com"));
        Assert.Equal(StyleContext.Work, StyleContextInfo.Detect("msedge.exe", "app.slack.com"));
        Assert.Equal(StyleContext.Other, StyleContextInfo.Detect("chrome.exe", "example.com"));
    }

    [Fact]
    public void HostMatchesSubdomainsButNotLookalikes()
    {
        Assert.Equal(StyleContext.Work, StyleContextInfo.Detect(null, "eu.linkedin.com"));
        Assert.Equal(StyleContext.Other, StyleContextInfo.Detect(null, "notlinkedin.com"));
    }

    [Fact]
    public void DefaultTonesAndOverrides()
    {
        var settings = new StyleSettings();
        Assert.Equal(StyleTone.Formal, settings.ToneFor(StyleContext.Email));
        Assert.Equal(StyleTone.Casual, settings.ToneFor(StyleContext.Personal));
        settings.SetTone(StyleTone.Formal, StyleContext.Personal);
        Assert.Equal(StyleTone.Formal, settings.ToneFor(StyleContext.Personal));
        Assert.Equal(StyleTone.Formal, settings.ToneFor(StyleContext.Email));
        Assert.Equal(StyleTone.Casual, StyleContext.Personal.DefaultTone());
    }

    [Fact]
    public void SettingsSurviveEncoding()
    {
        var settings = new StyleSettings();
        settings.SetTone(StyleTone.Casual, StyleContext.Work);
        var json = JsonSerializer.Serialize(settings, SettingsJson.Options);
        var decoded = JsonSerializer.Deserialize<StyleSettings>(json, SettingsJson.Options)!;
        Assert.Equal(StyleTone.Casual, decoded.ToneFor(StyleContext.Work));
    }

    [Fact]
    public void ToneExamplesShowWhatEachToneChanges()
    {
        Assert.EndsWith("Termin.", StyleTone.Formal.ExampleCleaned());
        Assert.EndsWith("Termin", StyleTone.Casual.ExampleCleaned());
        Assert.NotEqual(StyleTone.Formal.ExampleRewritten(), StyleTone.Casual.ExampleRewritten());
        Assert.NotEqual(StyleTone.Formal.ExampleRewritten(), StyleTone.Neutral.ExampleRewritten());
        Assert.False(StyleTone.Casual.ExampleRewritten().EndsWith('.'));
    }

    [Fact]
    public void CasualToneDropsOnlyTheFinalPeriod()
    {
        Assert.Equal("Bis morgen. Ich freue mich", StyleTone.Casual.Apply("Bis morgen. Ich freue mich."));
        Assert.Equal("Kommst du morgen?", StyleTone.Casual.Apply("Kommst du morgen?"));
        Assert.Equal("Wir sehen uns...", StyleTone.Casual.Apply("Wir sehen uns..."));
        Assert.Equal("Bis morgen.", StyleTone.Formal.Apply("Bis morgen."));
        Assert.Equal("Bis morgen.", StyleTone.Neutral.Apply("Bis morgen."));
    }

    [Fact]
    public void NamesAndRewriteTones()
    {
        Assert.Equal("Persönlich", StyleContext.Personal.DisplayName());
        Assert.Equal("Locker", StyleTone.Casual.DisplayName());
        Assert.Equal(TextTone.Formal, StyleTone.Formal.RewriteTone());
        Assert.Contains("Outlook", StyleContext.Email.AppsNote());
    }
}

public class ModeRulesTests
{
    private static AppSettings Settings() => new()
    {
        AppModeRules = [new AppModeRule { ProcessName = "outlook.exe", AppName = "Outlook", Mode = WorkflowType.TextImprover }],
        WebsiteModeRules =
        [
            new WebsiteModeRule { Host = "google.com", Mode = WorkflowType.EmojiText },
            new WebsiteModeRule { Host = "mail.google.com", Mode = WorkflowType.TextImprover },
            new WebsiteModeRule { Host = "web.whatsapp.com", Mode = WorkflowType.EmojiText },
        ],
    };

    [Fact]
    public void AppRuleAppliesToTheDiktatShortcutOnly()
    {
        Assert.Equal(WorkflowType.TextImprover, ModeRules.Resolve(WorkflowType.TapTalk, "OUTLOOK.EXE", null, Settings()));
        Assert.Equal(WorkflowType.DampfAblassen, ModeRules.Resolve(WorkflowType.DampfAblassen, "outlook.exe", null, Settings()));
        Assert.Equal(WorkflowType.TapTalk, ModeRules.Resolve(WorkflowType.TapTalk, "notepad.exe", null, Settings()));
    }

    [Fact]
    public void WebsiteRuleWinsAndTheMostSpecificHostWins()
    {
        Assert.Equal(WorkflowType.TextImprover, ModeRules.Resolve(WorkflowType.TapTalk, "chrome.exe", "mail.google.com", Settings()));
        Assert.Equal(WorkflowType.EmojiText, ModeRules.Resolve(WorkflowType.TapTalk, "chrome.exe", "docs.google.com", Settings()));
        Assert.Equal(WorkflowType.TapTalk, ModeRules.Resolve(WorkflowType.TapTalk, "chrome.exe", "notgoogle.com", Settings()));
    }

    [Fact]
    public void UnavailableModeFallsBack()
    {
        Assert.Equal(WorkflowType.TapTalk, ModeRules.Resolve(WorkflowType.TapTalk, "outlook.exe", null, Settings(), mode => !mode.UsesLanguageModel()));
        // An unavailable website rule leaves the app rule.
        var settings = Settings();
        settings.AppModeRules.Add(new AppModeRule { ProcessName = "msedge.exe", Mode = WorkflowType.DampfAblassen });
        Assert.Equal(WorkflowType.DampfAblassen, ModeRules.Resolve(WorkflowType.TapTalk, "msedge.exe", "mail.google.com", settings, mode => mode != WorkflowType.TextImprover));
    }

    [Fact]
    public void ProcessNamesAreNormalized()
    {
        Assert.Equal("outlook.exe", ProcessNames.Normalize(@"C:\Program Files\Microsoft Office\OUTLOOK.EXE"));
        Assert.Equal("slack.exe", ProcessNames.Normalize("Slack"));
        Assert.True(ProcessNames.IsBrowser("MSEdge.exe"));
        Assert.False(ProcessNames.IsBrowser("winword.exe"));
    }
}
