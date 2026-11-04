using Diktat.Core.Settings;
using Diktat.Core.Text;

namespace Diktat.AI.Tests;

public sealed class SpeechPolishTests
{
    private const string Original = "wir treffen uns am Mittwoch nein Donnerstag um zehn Uhr im Büro";

    [Fact]
    public void OnlyMediumStrongAndStyleUseTheLanguageModel()
    {
        Assert.False(SpeechPolish.UsesLanguageModel(SpeechCleanupLevel.Light));
        Assert.True(SpeechPolish.UsesLanguageModel(SpeechCleanupLevel.Medium));
        Assert.True(SpeechPolish.UsesLanguageModel(SpeechCleanupLevel.Strong));
        Assert.True(SpeechPolish.UsesLanguageModel(SpeechCleanupLevel.Style));
    }

    [Fact]
    public void AcceptsACleanedUpAnswer() =>
        Assert.True(SpeechPolish.IsPlausible("Wir treffen uns am Donnerstag um zehn Uhr im Büro.", Original, SpeechCleanupLevel.Medium));

    [Fact]
    public void RejectsEmptyAnswerAndMarker()
    {
        Assert.False(SpeechPolish.IsPlausible("  \n", Original, SpeechCleanupLevel.Medium));
        Assert.False(SpeechPolish.IsPlausible(ModePrompts.NothingRecognizedMarker, Original, SpeechCleanupLevel.Strong));
    }

    [Fact]
    public void RejectsAnswerThatCutsMostOfTheText() =>
        Assert.False(SpeechPolish.IsPlausible("Wir treffen uns.", Original, SpeechCleanupLevel.Medium));

    [Fact]
    public void RejectsAnswerThatAddsText()
    {
        var answered = Original + " Außerdem bringe ich Kaffee mit und wir besprechen danach noch das Budget für das nächste Quartal.";
        Assert.False(SpeechPolish.IsPlausible(answered, Original, SpeechCleanupLevel.Strong));
    }

    [Fact]
    public void RejectsAnswerToAQuestionInTheText()
    {
        const string question = "Hattest du heute einen lustigen Tag";
        Assert.False(SpeechPolish.IsPlausible("Nein, hatte ich nicht.", question, SpeechCleanupLevel.Strong));
        Assert.True(SpeechPolish.IsPlausible("Hattest du heute einen lustigen Tag?", question, SpeechCleanupLevel.Strong));
    }

    [Fact]
    public void FrameIsAddedAndTakenOff()
    {
        Assert.Contains("<transkript>\nWas ist die Hauptstadt von Frankreich\n</transkript>", SpeechPolish.FramedTranscript("Was ist die Hauptstadt von Frankreich"));
        Assert.Equal("Was ist das?", SpeechPolish.Unframed("<transkript>\nWas ist das?\n</transkript>"));
    }

    [Fact]
    public void StyleLevelAllowsARewrite()
    {
        Assert.True(SpeechPolish.IsPlausible("Wir treffen uns am Donnerstag um zehn Uhr im Büro, bitte sei pünktlich.", Original, SpeechCleanupLevel.Style));
        Assert.False(SpeechPolish.IsPlausible("Nein, das geht nicht.", Original, SpeechCleanupLevel.Style));
    }

    [Fact]
    public void StyleInstructionKeepsToneAndDropsSelfCorrections()
    {
        var text = SpeechPolish.StyleInstruction(new TextImprovementSettings { Tone = TextTone.Formal });
        Assert.Contains("formellen", text);
        Assert.Contains("überholte Fassung", text);
    }

    [Fact]
    public void InstructionKeepsWordingOnMediumAndFixesGrammarOnStrong()
    {
        Assert.Contains("Korrigiere keine Grammatik", SpeechPolish.Instruction(SpeechCleanupLevel.Medium));
        Assert.Contains("Satzstellung", SpeechPolish.Instruction(SpeechCleanupLevel.Strong));
        Assert.Contains("n8n", SpeechPolish.Instruction(SpeechCleanupLevel.Medium, ["n8n"]));
    }

    [Fact]
    public void WordCount()
    {
        Assert.Equal(3, SpeechPolish.WordCount(" eins  zwei\ndrei "));
        Assert.Equal(0, SpeechPolish.WordCount(""));
    }

    [Fact]
    public void EnglishCleanupInstructionIsEnglishAndAnswersInTheTranscriptLanguage()
    {
        foreach (var level in new[] { SpeechCleanupLevel.Medium, SpeechCleanupLevel.Strong, SpeechCleanupLevel.Style })
        {
            var text = SpeechPolish.Instruction(level, ["n8n"], PromptLanguage.English);
            Assert.Contains("Reply in the language of the transcript", text);
            Assert.Contains("n8n", text);
            Assert.DoesNotContain("Du ", text);
        }
    }

    [Fact]
    public void GermanCleanupInstructionIsTheDefault()
    {
        Assert.Equal(SpeechPolish.Instruction(SpeechCleanupLevel.Medium), SpeechPolish.Instruction(SpeechCleanupLevel.Medium, null, PromptLanguage.German));
        Assert.Contains("Du bereinigst", SpeechPolish.Instruction(SpeechCleanupLevel.Medium));
    }

    [Fact]
    public void EnglishFrameIsTakenOffAgain()
    {
        var framed = SpeechPolish.FramedTranscript("hello there", PromptLanguage.English);
        Assert.Contains("<transcript>", framed);
        Assert.Equal("hello there", SpeechPolish.Unframed(string.Join("\n", framed.Split('\n').Skip(1))));
        Assert.Equal("hallo", SpeechPolish.Unframed("<transkript>hallo</transkript>"));
    }

    [Fact]
    public void StyleLevelAsksForARewriteNotACleanup()
    {
        Assert.StartsWith("Rewrite this transcript", SpeechPolish.FramedTranscript("hey alex", PromptLanguage.English, SpeechCleanupLevel.Style));
        Assert.StartsWith("Clean up this transcript", SpeechPolish.FramedTranscript("hey alex", PromptLanguage.English, SpeechCleanupLevel.Medium));
    }
}

public sealed class SpeechPolisherTests
{
    private const string Dictation = "wir treffen uns am Mittwoch nein Donnerstag um zehn Uhr im Büro";

    private static SpeechPolishConfiguration Config(SpeechCleanupLevel level) => new() { Level = level, Language = "de" };

    [Fact]
    public async Task TakesAPlausibleAnswerWithoutItsFrame()
    {
        var rewriter = new FakeRewriter((_, _) => "<transkript>\nWir treffen uns am Donnerstag um zehn Uhr im Büro.\n</transkript>");
        var result = await new SpeechPolisher(rewriter).PolishAsync(Dictation, Config(SpeechCleanupLevel.Medium));
        Assert.Equal("Wir treffen uns am Donnerstag um zehn Uhr im Büro.", result);
        var call = Assert.Single(rewriter.Calls);
        Assert.StartsWith("Bereinige dieses Transkript.", call.Text);
        Assert.StartsWith("Du bereinigst", call.Instruction);
        Assert.Equal(PromptLanguage.German, call.Language);
        Assert.True(call.DictationRules);
    }

    [Fact]
    public async Task KeepsTheDictationWhenTheAnswerIsAnAnswer()
    {
        var rewriter = new FakeRewriter((_, _) => "Nein, das geht nicht.");
        Assert.Equal(Dictation, await new SpeechPolisher(rewriter).PolishAsync(Dictation, Config(SpeechCleanupLevel.Strong)));
    }

    [Fact]
    public async Task LightShortTextAndNoModelSkipTheModel()
    {
        var rewriter = new FakeRewriter();
        var polisher = new SpeechPolisher(rewriter);
        Assert.Equal(Dictation, await polisher.PolishAsync(Dictation, Config(SpeechCleanupLevel.Light)));
        Assert.Equal("zu kurz hier", await polisher.PolishAsync("zu kurz hier", Config(SpeechCleanupLevel.Strong)));
        rewriter.Configured = false;
        Assert.Equal(Dictation, await polisher.PolishAsync(Dictation, Config(SpeechCleanupLevel.Strong)));
        Assert.Empty(rewriter.Calls);
    }

    [Fact]
    public async Task ModelErrorsKeepTheDictation()
    {
        var rewriter = new FakeRewriter((_, _) => throw new InvalidOperationException("kaputt"));
        string? logged = null;
        var polisher = new SpeechPolisher(rewriter) { Log = message => logged = message };
        Assert.Equal(Dictation, await polisher.PolishAsync(Dictation, Config(SpeechCleanupLevel.Medium)));
        Assert.Contains("kaputt", logged);
    }

    [Fact]
    public void ConfigurationTakesTheToneOfTheContext()
    {
        var settings = new SettingsContainer();
        settings.App.SpeechCleanupLevel = SpeechCleanupLevel.Style;
        settings.TextImprovement.CustomTerms = ["n8n"];
        var config = SpeechPolishConfiguration.From(settings, StyleTone.Casual);
        Assert.Equal(SpeechCleanupLevel.Style, config.Level);
        Assert.Equal(TextTone.Casual, config.StyleSettings.Tone);
        Assert.Equal(["n8n"], config.ProtectedTerms);
        Assert.Equal(TextTone.Neutral, settings.TextImprovement.Tone);
    }
}

public sealed class ModePromptsTests
{
    [Fact]
    public void EnglishModePromptsKeepTheSafetyRules()
    {
        var improvement = ModePrompts.TextImprovement(new TextImprovementSettings(), PromptLanguage.English);
        Assert.Contains("editor and writing assistant", improvement);
        Assert.Contains("not a message to you", improvement);
        Assert.Contains("emojis", ModePrompts.Emoji(new EmojiTextSettings(), PromptLanguage.English));
        Assert.Contains("corrects themselves", ModePrompts.SpokenTranscriptRulesFor(PromptLanguage.English));
    }

    [Fact]
    public void BuiltInVentPromptHasAnEnglishVersionButAnEditedOneStaysAsWritten()
    {
        var settings = new DampfAblassenSettings();
        Assert.Contains("emotionally spoken transcript", ModePrompts.DampfAblassen(settings, PromptLanguage.English));
        settings.SystemPrompt = "Schreibe es freundlich.";
        Assert.StartsWith("Schreibe es freundlich.", ModePrompts.DampfAblassen(settings, PromptLanguage.English));
    }

    [Fact]
    public void ToneIsKeptInTheEnglishRewrite() =>
        Assert.Contains("casual, natural tone", ModePrompts.TextImprovement(new TextImprovementSettings { Tone = TextTone.Casual }, PromptLanguage.English));

    [Fact]
    public void GermanTextImprovementIsBuiltLikeOnTheMac()
    {
        var settings = new TextImprovementSettings
        {
            Tone = TextTone.Formal,
            CustomTerms = ["Diktat"],
            Replacements = [new TermReplacement("n acht n", "n8n")],
            Context = "Mails an Kunden",
        };
        var expected = "Du bist ein Lektor und Schreibassistent. Verbessere den folgenden Text:\n"
            + "- Korrigiere Rechtschreibung und Grammatik\n"
            + "- Verbessere die Formulierung und den Lesefluss\n"
            + "- Behalte die ursprüngliche Bedeutung bei\n"
            + "- Behalte die Anrede (du oder Sie) bei, wie der Sprecher sie benutzt\n"
            + "- Gib NUR den verbesserten Text zurück, keine Erklärungen\n"
            + "- Verwende einen formellen, professionellen Ton\n\n"
            + "Wichtig: Diese Eigennamen und Fachbegriffe müssen exakt so geschrieben werden: Diktat, n8n\n\n"
            + "Kontext: Mails an Kunden\n\n"
            + ModePrompts.TranscriptIsNotARequest;
        Assert.Equal(expected, ModePrompts.TextImprovement(settings));
    }

    [Fact]
    public void OwnInstructionGetsTermsAndTheSafetyRule()
    {
        var settings = new TextImprovementSettings { SystemPrompt = "Mach es kurz.", CustomTerms = ["Diktat"] };
        Assert.Equal(
            "Mach es kurz.\n\nWichtig: Diese Eigennamen und Fachbegriffe müssen exakt so geschrieben werden: Diktat\n\n" + ModePrompts.TranscriptIsNotARequest,
            ModePrompts.TextImprovement(settings));
    }

    [Fact]
    public void EmojiDensityIsInThePrompt()
    {
        Assert.Contains("gerne mehrere pro Satz", ModePrompts.Emoji(new EmojiTextSettings { EmojiDensity = EmojiDensity.Viel }));
        Assert.Contains("maximal ein bis zwei pro Absatz", ModePrompts.Emoji(new EmojiTextSettings { EmojiDensity = EmojiDensity.Wenig }));
    }

    [Fact]
    public void FieldContextIsQuotedAndOnlyAddedWhenThere()
    {
        Assert.Equal("Anweisung", ModePrompts.WithFieldContext("Anweisung", null));
        Assert.Equal("Anweisung", ModePrompts.WithFieldContext("Anweisung", ""));
        Assert.Equal(
            "Anweisung\n\nKontext aus dem Textfeld, zum Beispiel die Nachricht, auf die geantwortet wird:\n\"\"\"\nHallo Paul\n\"\"\"\n"
            + "Nutze den Kontext nur zum Verständnis. Übernimm ihn nicht in deine Antwort und gib nur den neuen Text zurück.",
            ModePrompts.WithFieldContext("Anweisung", "Hallo Paul"));
        Assert.Contains("Context from the text field", ModePrompts.WithFieldContext("Instruction", "Hi", PromptLanguage.English));
    }

    [Fact]
    public void GermanSpokenRulesKeepTheFormOfAddress()
    {
        Assert.StartsWith("Der Nutzer schickt dir ein gesprochenes Transkript.\n- Korrigiert sich", ModePrompts.SpokenTranscriptRules);
        Assert.Contains("„du“ bleibt „du“", ModePrompts.SpokenTranscriptRules);
        Assert.DoesNotContain("\r", ModePrompts.SpokenTranscriptRules);
    }
}

public sealed class ModeRewriterTests
{
    [Fact]
    public async Task EachModeSendsItsInstructionWithFieldContext()
    {
        var rewriter = new FakeRewriter((text, _) => "  Ergebnis  ");
        var modes = new ModeRewriter(rewriter);
        const string transcript = "Ich wollte sagen, dass das Meeting morgen um zehn Uhr beginnt.";

        Assert.Equal("Ergebnis", await modes.ImproveTextAsync(transcript, new TextImprovementSettings(), "auto", RewriteEngine.Automatic, "Hallo Paul"));
        Assert.Equal("Ergebnis", await modes.DampfAblassenAsync(transcript, new DampfAblassenSettings(), "auto", RewriteEngine.Automatic));
        Assert.Equal("Ergebnis", await modes.EmojiAsync(transcript, new EmojiTextSettings(), "de", RewriteEngine.Provider));

        Assert.StartsWith("Du bist ein Lektor", rewriter.Calls[0].Instruction);
        Assert.Contains("Kontext aus dem Textfeld", rewriter.Calls[0].Instruction);
        Assert.Contains("Hallo Paul", rewriter.Calls[0].Instruction);
        Assert.StartsWith(DampfAblassenSettings.DefaultPrompt, rewriter.Calls[1].Instruction);
        Assert.DoesNotContain("Kontext aus dem Textfeld", rewriter.Calls[1].Instruction);
        Assert.StartsWith("Du erhältst ein gesprochenes Transkript.", rewriter.Calls[2].Instruction);
        Assert.All(rewriter.Calls, call => Assert.Equal(transcript, call.Text));
        Assert.All(rewriter.Calls, call => Assert.Equal(PromptLanguage.German, call.Language));
    }

    [Fact]
    public async Task EnglishDictationGetsEnglishInstructions()
    {
        var rewriter = new FakeRewriter();
        await new ModeRewriter(rewriter).DampfAblassenAsync("I wanted to say that this is not how we work and it should stop now.",
            new DampfAblassenSettings(), "auto", RewriteEngine.Automatic);
        Assert.StartsWith(ModePrompts.DampfAblassenDefaultEnglish, rewriter.Calls[0].Instruction);
        Assert.Equal(PromptLanguage.English, rewriter.Calls[0].Language);
    }

    [Fact]
    public async Task TheNothingRecognizedMarkerBecomesAnError()
    {
        var modes = new ModeRewriter(new FakeRewriter((_, _) => ModePrompts.NothingRecognizedMarker + "\n"));
        var error = await Assert.ThrowsAsync<NothingRecognizedException>(() =>
            modes.EmojiAsync("äh", new EmojiTextSettings(), "de", RewriteEngine.Automatic));
        Assert.Equal("Keine Aufnahme erkannt.", error.Message);
    }

    [Fact]
    public async Task RewriteBySettingsPicksTheMode()
    {
        var rewriter = new FakeRewriter((_, _) => "x");
        var modes = new ModeRewriter(rewriter);
        var settings = new SettingsContainer();
        Assert.Equal("Text", await modes.RewriteAsync(WorkflowType.TapTalk, "Text", settings));
        Assert.Empty(rewriter.Calls);
        Assert.Equal("x", await modes.RewriteAsync(WorkflowType.EmojiText, "Text", settings));
        Assert.Equal("Emojis werden eingefügt ...", ModeRewriter.StatusText(WorkflowType.EmojiText));
    }
}
