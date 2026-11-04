using System.Text.Json.Nodes;
using Diktat.Core.Settings;

namespace Diktat.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "diktat-settings-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    /// The layout of a macOS export ("Einstellungen exportieren"), trimmed, with made-up terms.
    private const string MacExport = """
        {"textImprovement":{"customTerms":["Kontoro","Brunnmatt","Zellweger-Huber","n8n"],"replacements":[{"spoken":"btw","written":"by the way"}],"context":"","customName":"","tone":"neutral","systemPrompt":""},
         "transcription":{"language":"de"},
         "app":{"restoreClipboardAfterPaste":true,"hotkeyMode":"hold","hasMigratedToSetupAssistant":true,"rewriteEngine":"provider","customHotkeys":{},"styleSettings":{"tones":{"work":"formal"}},"lastSeenWhatsNewVersion":"1.15","appModeRules":[{"bundleID":"com.apple.mail","appName":"Mail","mode":"textImprover"}],"fnAloneStartsDictation":true,"hasAutoSelectedFastLocalModel":false,"fitToSurroundingText":true,"muteOutputWhileRecording":true,"showsRecordingIndicator":true,"removeFillerWordsEnabled":true,"speechCleanupLevel":"medium","historyRetention":"day","hasSeenOnboarding":true,"websiteModeRules":[{"host":"mail.google.com","mode":"textImprover"}],"hasCheckedParakeetDefault":true,"aiSeesFieldContext":true,"automaticParagraphs":true,"correctCustomTermsEnabled":true,"selectedLocalTranscriptionModelName":"openai_whisper-large-v3-v20240930_turbo_632MB","summarizesMeetings":true},
         "dampfAblassen":{"customName":"","systemPrompt":"Schreib es freundlich."},
         "emojiText":{"customName":"","emojiDensity":"mittel"}}
        """;

    [Fact]
    public void MissingFileLoadsDefaults()
    {
        var settings = new SettingsStore(SettingsPath).Load();
        Assert.Equal(SpeechCleanupLevel.Light, settings.App.SpeechCleanupLevel);
        Assert.Equal("auto", settings.Transcription.Language);
    }

    [Fact]
    public void SaveAndLoadRoundTrip()
    {
        var store = new SettingsStore(SettingsPath);
        var settings = new SettingsContainer();
        settings.App.SpeechCleanupLevel = SpeechCleanupLevel.Strong;
        settings.App.SetHotkey(WorkflowType.TapTalk, new KeyCombo { Ctrl = true, Alt = true, Key = 0x44 });
        settings.App.StyleSettings.SetTone(StyleTone.Casual, StyleContext.Work);
        settings.TextImprovement.CustomTerms = ["Kontoro"];
        store.Save(settings);

        var loaded = store.Load();
        Assert.Equal(SpeechCleanupLevel.Strong, loaded.App.SpeechCleanupLevel);
        Assert.Equal(0x44, loaded.App.HotkeyFor(WorkflowType.TapTalk)!.Key);
        Assert.Equal(StyleTone.Casual, loaded.App.StyleSettings.ToneFor(StyleContext.Work));
        Assert.Equal(new[] { "Kontoro" }, loaded.TextImprovement.CustomTerms);
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void CorruptFileKeepsABackupAndLoadsDefaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "{ das ist kaputt");
        var store = new SettingsStore(SettingsPath);
        var settings = store.Load();
        Assert.Equal(SpeechCleanupLevel.Light, settings.App.SpeechCleanupLevel);
        Assert.Equal("{ das ist kaputt", File.ReadAllText(store.BackupPath));
    }

    [Fact]
    public void ABadValueOnlyDropsThatValue()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """{"app":{"speechCleanupLevel":"ultra","historyRetention":"week"},"transcription":{"language":"en"}}""");
        var settings = new SettingsStore(SettingsPath).Load();
        Assert.Equal(SpeechCleanupLevel.Light, settings.App.SpeechCleanupLevel);
        Assert.Equal(HistoryRetention.Week, settings.App.HistoryRetention);
        Assert.Equal("en", settings.Transcription.Language);
    }

    [Fact]
    public void TheMacExportFormatDeserializes()
    {
        var settings = SettingsStore.FromJson(JsonNode.Parse(MacExport)!.AsObject());
        Assert.Equal(new[] { "Kontoro", "Brunnmatt", "Zellweger-Huber", "n8n" }, settings.TextImprovement.CustomTerms);
        Assert.Equal("by the way", settings.TextImprovement.Replacements.Single().Written);
        Assert.Equal(TextTone.Neutral, settings.TextImprovement.Tone);
        Assert.Equal(RewriteEngine.Provider, settings.App.RewriteEngine);
        Assert.Equal(SpeechCleanupLevel.Medium, settings.App.SpeechCleanupLevel);
        Assert.Equal(StyleTone.Formal, settings.App.StyleSettings.ToneFor(StyleContext.Work));
        Assert.True(settings.App.AutomaticParagraphs);
        Assert.True(settings.App.AiSeesFieldContext);
        Assert.Equal("mail.google.com", settings.App.WebsiteModeRules.Single().Host);
        Assert.Equal(EmojiDensity.Mittel, settings.EmojiText.EmojiDensity);
        Assert.Equal("Schreib es freundlich.", settings.DampfAblassen.SystemPrompt);
        // Mac-only keys have no Windows property.
        Assert.Empty(settings.App.AppModeRules);
        Assert.Empty(settings.App.Hotkeys);
    }

    [Fact]
    public void ImportMergesIntoTheCurrentSettings()
    {
        var store = new SettingsStore(SettingsPath);
        var current = new SettingsContainer();
        current.App.HasSeenOnboarding = false;
        current.App.TranscriptionModel = "parakeet-tdt-0.6b-v3";
        current.App.SetHotkey(WorkflowType.TapTalk, new KeyCombo { Ctrl = true, Shift = true });
        current.App.AppModeRules = [new AppModeRule { ProcessName = "outlook.exe", AppName = "Outlook", Mode = WorkflowType.TextImprover }];
        current.TextImprovement.CustomTerms = ["Windows-Begriff", "n8n"];
        current.TextImprovement.Replacements = [new TermReplacement("btw", "übrigens"), new TermReplacement("en acht en", "n8n")];
        store.Save(current);

        var merged = store.ImportMacExport(MacExport);

        // From the export.
        Assert.Equal(SpeechCleanupLevel.Medium, merged.App.SpeechCleanupLevel);
        Assert.Equal(RewriteEngine.Provider, merged.App.RewriteEngine);
        Assert.True(merged.App.AutomaticParagraphs);
        Assert.Equal("Schreib es freundlich.", merged.DampfAblassen.SystemPrompt);
        Assert.Equal("auto", merged.Transcription.Language); // "de" was never a choice on the Mac
        // Lists are combined; the export wins for the same spoken words.
        Assert.Equal(new[] { "Windows-Begriff", "n8n", "Kontoro", "Brunnmatt", "Zellweger-Huber" }, merged.TextImprovement.CustomTerms);
        Assert.Equal(new[] { "n8n", "by the way" }, merged.TextImprovement.Replacements.Select(r => r.Written));
        Assert.Equal("mail.google.com", merged.App.WebsiteModeRules.Single().Host);
        // What only Windows has stays, and so does the Windows setup state.
        Assert.False(merged.App.HasSeenOnboarding);
        Assert.Equal("parakeet-tdt-0.6b-v3", merged.App.TranscriptionModel);
        Assert.True(merged.App.HotkeyFor(WorkflowType.TapTalk)!.Shift);
        Assert.Equal("outlook.exe", merged.App.AppModeRules.Single().ProcessName);
        // And it was saved.
        Assert.Equal(SpeechCleanupLevel.Medium, store.Load().App.SpeechCleanupLevel);
    }

    [Fact]
    public void ImportIgnoresBadValuesAndRejectsOtherFiles()
    {
        var current = new SettingsContainer();
        current.App.SpeechCleanupLevel = SpeechCleanupLevel.Strong;
        var merged = SettingsStore.MergeMacExport(current, """{"app":{"speechCleanupLevel":"ultra","playsStartSound":false}}""");
        Assert.Equal(SpeechCleanupLevel.Strong, merged.App.SpeechCleanupLevel);
        Assert.False(merged.App.PlaysStartSound);

        Assert.Throws<FormatException>(() => SettingsStore.MergeMacExport(current, "kein JSON"));
        Assert.Throws<FormatException>(() => SettingsStore.MergeMacExport(current, """{"foo":1}"""));
    }
}
