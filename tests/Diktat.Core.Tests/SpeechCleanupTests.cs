using Diktat.Core.Settings;
using Diktat.Core.Text;

namespace Diktat.Core.Tests;

// The rule-based part of SpeechCleanupTests and EnglishDictationTests of the macOS app. The
// prompt tests (instruction, styleInstruction, ModePrompts) belong to Diktat.AI.
public class SpeechCleanupTests
{
    private const string Original = "wir treffen uns am Mittwoch nein Donnerstag um zehn Uhr im Büro";

    [Fact]
    public void OnlyTheLightLevelRunsWithoutALanguageModel()
    {
        Assert.False(SpeechCleanupLevel.Light.UsesLanguageModel());
        Assert.True(SpeechCleanupLevel.Medium.UsesLanguageModel());
        Assert.True(SpeechCleanupLevel.Strong.UsesLanguageModel());
        Assert.True(SpeechCleanupLevel.Style.UsesLanguageModel());
    }

    [Fact]
    public void LevelsHaveNamesAndDetails()
    {
        Assert.Equal(new[] { "Leicht", "Mittel", "Stark", "KI-Schreibstil" }, SpeechCleanupLevelInfo.AllCases.Select(l => l.DisplayName()));
        Assert.Contains("ohne KI", SpeechCleanupLevel.Light.Detail());
    }

    [Fact]
    public void AcceptsACleanedUpAnswer() =>
        Assert.True(SpeechCleanupRules.IsPlausible("Wir treffen uns am Donnerstag um zehn Uhr im Büro.", Original, SpeechCleanupLevel.Medium));

    [Fact]
    public void RejectsEmptyAnswerAndMarker()
    {
        Assert.False(SpeechCleanupRules.IsPlausible("  \n", Original, SpeechCleanupLevel.Medium));
        Assert.False(SpeechCleanupRules.IsPlausible(SpeechCleanupRules.NothingRecognizedMarker, Original, SpeechCleanupLevel.Strong));
    }

    [Fact]
    public void RejectsAnswerThatCutsMostOfTheText() =>
        Assert.False(SpeechCleanupRules.IsPlausible("Wir treffen uns.", Original, SpeechCleanupLevel.Medium));

    [Fact]
    public void RejectsAnswerThatAddsText()
    {
        var answered = Original + " Außerdem bringe ich Kaffee mit und wir besprechen danach noch das Budget für das nächste Quartal.";
        Assert.False(SpeechCleanupRules.IsPlausible(answered, Original, SpeechCleanupLevel.Strong));
    }

    [Fact]
    public void RejectsAnswerToAQuestionInTheText()
    {
        const string question = "Hattest du heute einen lustigen Tag";
        Assert.False(SpeechCleanupRules.IsPlausible("Nein, hatte ich nicht.", question, SpeechCleanupLevel.Strong));
        Assert.True(SpeechCleanupRules.IsPlausible("Hattest du heute einen lustigen Tag?", question, SpeechCleanupLevel.Strong));
    }

    [Fact]
    public void FrameIsAddedAndTakenOff()
    {
        var framed = SpeechCleanupRules.FramedTranscript("Was ist die Hauptstadt von Frankreich");
        Assert.Contains("<transkript>\nWas ist die Hauptstadt von Frankreich\n</transkript>", framed);
        Assert.Equal("Was ist das?", SpeechCleanupRules.Unframed("<transkript>\nWas ist das?\n</transkript>"));
    }

    [Fact]
    public void StyleLevelAllowsARewrite()
    {
        const string rewritten = "Wir treffen uns am Donnerstag um zehn Uhr im Büro, bitte sei pünktlich.";
        Assert.True(SpeechCleanupRules.IsPlausible(rewritten, Original, SpeechCleanupLevel.Style));
        Assert.False(SpeechCleanupRules.IsPlausible("Nein, das geht nicht.", Original, SpeechCleanupLevel.Style));
    }

    [Fact]
    public void WordCount()
    {
        Assert.Equal(3, SpeechCleanupRules.WordCount(" eins  zwei\ndrei "));
        Assert.Equal(0, SpeechCleanupRules.WordCount(""));
    }

    [Fact]
    public void EnglishFrameIsTakenOffAgain()
    {
        var framed = SpeechCleanupRules.FramedTranscript("hello there", PromptLanguage.English);
        Assert.Contains("<transcript>", framed);
        Assert.Equal("hello there", SpeechCleanupRules.Unframed(string.Join("\n", framed.Split('\n').Skip(1))));
        Assert.Equal("hallo", SpeechCleanupRules.Unframed("<transkript>hallo</transkript>"));
    }

    [Fact]
    public void StyleLevelAsksForARewriteNotACleanup()
    {
        Assert.StartsWith("Rewrite this transcript", SpeechCleanupRules.FramedTranscript("hey alex", PromptLanguage.English, SpeechCleanupLevel.Style));
        Assert.StartsWith("Clean up this transcript", SpeechCleanupRules.FramedTranscript("hey alex", PromptLanguage.English, SpeechCleanupLevel.Medium));
    }
}

public class EnglishDictationTests
{
    [Fact]
    public void LanguageFollowsWhatWasSpoken()
    {
        Assert.Equal(PromptLanguage.English, PromptLanguages.Detect("I wanted to say that the meeting tomorrow starts at ten o'clock."));
        Assert.Equal(PromptLanguage.German, PromptLanguages.Detect("Ich wollte noch sagen, dass das Meeting morgen um zehn Uhr beginnt."));
    }

    [Fact]
    public void TooLittleTextCountsAsGerman() => Assert.Equal(PromptLanguage.German, PromptLanguages.Detect(""));

    [Fact]
    public void EnglishFillerWordsGoWhenTheLanguageIsDetected()
    {
        var processor = new TranscriptPostProcessor { CorrectCustomTerms = false, RemoveFillerWords = true };
        Assert.Equal("So I think we should meet on Friday afternoon.", processor.Process("So um I think we should uh meet on Friday afternoon."));
    }

    [Fact]
    public void GermanUmStaysAWord()
    {
        var processor = new TranscriptPostProcessor { CorrectCustomTerms = false, RemoveFillerWords = true };
        Assert.Equal("Wir treffen uns um zehn Uhr im Büro, in der Zentrale.", processor.Process("Wir treffen uns um zehn Uhr im Büro, äh, in der Zentrale."));
    }

    [Fact]
    public void LikelyEnglishNeedsEnglishWords()
    {
        Assert.True(LanguageGuesser.IsLikelyEnglish("Find us "));
        Assert.False(LanguageGuesser.IsLikelyEnglish("Schreib an , danke Meier."));
        Assert.False(LanguageGuesser.IsLikelyEnglish("Wichtiger Punkt "));
        Assert.False(LanguageGuesser.IsLikelyEnglish(" "));
    }

    // "um" is a German word

    /// German sentences with a few English words used to count as English, where "um" is a
    /// hesitation sound.
    [Theory]
    [InlineData("Zoom Call um 9")]
    [InlineData("Call um 3.")]
    [InlineData("Lunch um 12?")]
    [InlineData("Okay, um 5.")]
    [InlineData("Team Call um 10, danach Lunch.")]
    [InlineData("Call with the team um 10.")]
    [InlineData("Meeting with you um 3.")]
    public void GermanUmStaysInMixedSentences(string text)
    {
        var processor = new TranscriptPostProcessor { CorrectCustomTerms = false, RemoveFillerWords = true };
        Assert.Equal(text, processor.Process(text));
    }

    [Fact]
    public void FillerLanguageNeedsClearEnglish()
    {
        Assert.Equal("en", PromptLanguages.FillerLanguageCode("Well, um, that is a good question and I will look into it."));
        Assert.Equal("en", PromptLanguages.FillerLanguageCode("I, um, think so."));
        Assert.Equal("de", PromptLanguages.FillerLanguageCode("Zoom Call um 9"));
        Assert.Equal("de", PromptLanguages.FillerLanguageCode("Call with the team um 10."));
        Assert.Equal("de", PromptLanguages.FillerLanguageCode("Eu tenho um carro"));
        Assert.Equal("de", PromptLanguages.FillerLanguageCode("Wir treffen uns um drei."));
    }

    [Fact]
    public void ClearEnglishLosesItsHesitations()
    {
        var processor = new TranscriptPostProcessor { CorrectCustomTerms = false, RemoveFillerWords = true };
        Assert.Equal("I, think so.", processor.Process("I, um, think so."));
        var text = processor.Process("Um, so, um, I was, um, thinking that we, um, could meet.");
        Assert.DoesNotContain("um", text.Split(' ', ',', '.'), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void OtherLanguagesKeepTheirUm()
    {
        var processor = new TranscriptPostProcessor { CorrectCustomTerms = false, RemoveFillerWords = true };
        Assert.Equal("Eu tenho um carro.", processor.Process("Eu tenho um carro."));
    }
}
