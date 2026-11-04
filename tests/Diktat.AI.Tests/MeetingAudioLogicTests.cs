using Diktat.AI.Meetings;

namespace Diktat.AI.Tests;

public sealed class EchoFilterTests
{
    /// Words from a sentence, one every 0.4 s from the start.
    private static List<TimedWord> Words(string sentence, double start) =>
        sentence.Split(' ').Select((word, index) =>
        {
            var time = start + index * 0.4;
            return new TimedWord(word, time, time + 0.3);
        }).ToList();

    private static string Text(IEnumerable<TimedWord> words) => string.Join(" ", words.Select(w => w.Text));

    [Fact]
    public void RemovesWhatCameOutOfTheSpeakers()
    {
        var system = Words("Das hier ist mein Live-Kanal und nicht mein Hauptkanal.", 10);
        // The microphone heard it 0.3 s later, between two sentences of its own.
        var microphone = Words("Das ist jetzt ein Video.", 4)
            .Concat(Words("Das hier ist mein Live-Kanal und nicht mein Hauptkanal.", 10.3))
            .Concat(Words("Jetzt sage ich wieder was.", 16)).ToList();
        Assert.Equal("Das ist jetzt ein Video. Jetzt sage ich wieder was.", Text(EchoFilter.RemoveEcho(microphone, system)));
    }

    [Fact]
    public void FindsTheShiftBetweenTheTracks()
    {
        var system = Words("Wir sehen uns am Donnerstag im Büro und besprechen den Plan.", 5);
        var microphone = Words("Wir sehen uns am Donnerstag im Büro und besprechen den Plan.", 6.4);
        Assert.Empty(EchoFilter.RemoveEcho(microphone, system));
    }

    [Fact]
    public void KeepsSingleWordsThatHappenToMatch()
    {
        var system = Words("Ja genau so machen wir das.", 20);
        var microphone = Words("Ja klar, ich kümmere mich drum heute.", 20.2);
        Assert.Equal("Ja klar, ich kümmere mich drum heute.", Text(EchoFilter.RemoveEcho(microphone, system)));
    }

    [Fact]
    public void SingleLettersDoNotBreakTheEcho()
    {
        var system = Words("I have a rather, I don't know, a bit embarrassing problem.", 40);
        var microphone = Words("I have a rather, I a a", 40.2);
        Assert.Empty(EchoFilter.RemoveEcho(microphone, system));
    }

    [Fact]
    public void DropsShortScrapsWhileOthersTalk()
    {
        // As in a real call without headphones: the microphone garbled "a little squad work which"
        // and heard the rest of the sentence cleanly.
        var system = Words("And we have again planned a little squad work which comes to the topic of ownership.", 10);
        var microphone = Words("soul I", 10.4)
            .Concat(Words("a little spot work with", 12.1))
            .Concat(Words("comes to the topic of ownership.", 14.1)).ToList();
        Assert.Empty(EchoFilter.RemoveEcho(microphone, system));
    }

    [Fact]
    public void KeepsShortRepliesWhileOthersTalkWithHeadphones()
    {
        // No echo anywhere: the microphone only hears the person at this PC.
        var system = Words("Wer kann sich bis morgen um die Zahlen für den Bericht kümmern?", 10);
        var microphone = Words("Mach ich.", 12).Concat(Words("Ja, übernehme ich.", 14)).ToList();
        Assert.Equal("Mach ich. Ja, übernehme ich.", Text(EchoFilter.RemoveEcho(microphone, system)));
    }

    [Fact]
    public void KeepsShortRepliesWithAMatchingWord()
    {
        // "ich" happens to be in both tracks at the same moment; one word is no echo.
        var system = Words("Das mache ich dann am besten selbst.", 10);
        var microphone = Words("Danke, ich", 10.8);
        Assert.Equal("Danke, ich", Text(EchoFilter.RemoveEcho(microphone, system)));
    }

    [Fact]
    public void KeepsShortRepliesInAPause()
    {
        var system = Words("Können wir die Zahlen bis Freitag haben?", 0);
        var microphone = Words("Ja, genau.", 6);
        Assert.Equal("Ja, genau.", Text(EchoFilter.RemoveEcho(microphone, system)));
    }

    [Fact]
    public void KeepsOwnSentencesDuringCrosstalk()
    {
        var system = Words("Dann gehen wir jetzt in die Räume und treffen uns um halb wieder.", 10);
        var microphone = Words("Ich bereite für Donnerstag eine kleine Demo mit lokalen Modellen vor.", 11);
        Assert.Equal("Ich bereite für Donnerstag eine kleine Demo mit lokalen Modellen vor.", Text(EchoFilter.RemoveEcho(microphone, system)));
    }

    [Fact]
    public void RemovesGarbledWordsInsideTheEcho()
    {
        var system = Words("Ihr habt vielleicht meinen Hauptkanal schon abonniert vielen Dank.", 30);
        var microphone = Words("Ihr habt vielleicht meinen Hauptkanal schon abonniert vielen Dank.", 30.2);
        microphone[4] = microphone[4] with { Text = "Hauptkranich" };
        Assert.Empty(EchoFilter.RemoveEcho(microphone, system));
    }

    [Fact]
    public void KeepsEverythingWithoutEcho()
    {
        var system = Words("Können wir die Zahlen bis Freitag haben?", 3);
        var microphone = Words("Ja, das schaffe ich bis Donnerstag.", 7);
        Assert.Equal(microphone, EchoFilter.RemoveEcho(microphone, system));
        Assert.Equal(microphone, EchoFilter.RemoveEcho(microphone, []));
    }
}

public sealed class SpeakerAlignmentTests
{
    [Fact]
    public void JoinsTokensIntoWords()
    {
        (string, double, double)[] tokens = [("▁Guten", 0.0, 0.3), ("▁Mor", 0.3, 0.5), ("gen", 0.5, 0.7), (" zusammen", 0.8, 1.2)];
        Assert.Equal(
            [new TimedWord("Guten", 0.0, 0.3), new TimedWord("Morgen", 0.3, 0.7), new TimedWord("zusammen", 0.8, 1.2)],
            TimedWord.Words(tokens));
    }

    [Fact]
    public void AssignsWordsToTheTurnTheyFallIn()
    {
        TimedWord[] words =
        [
            new("Hallo", 0.0, 0.4), new("zusammen.", 0.5, 0.9), new("Hi!", 2.0, 2.3), new("Weiter.", 3.1, 3.5),
        ];
        SpeakerTurn[] turns = [new(1, 0.0, 1.0), new(2, 1.8, 2.6), new(1, 3.0, 4.0)];
        Assert.Equal(
            [new SpeakerAlignment.Paragraph(1, 0.0, "Hallo zusammen."), new SpeakerAlignment.Paragraph(2, 2.0, "Hi!"), new SpeakerAlignment.Paragraph(1, 3.1, "Weiter.")],
            SpeakerAlignment.Paragraphs(words, turns));
    }

    [Fact]
    public void SplitsWhereSomeoneOnTheOtherTrackTalks()
    {
        // A one-on-one call: the system track holds one person, the microphone answers in between.
        TimedWord[] words = [new("Kannst", 0.0, 0.3), new("du?", 0.4, 0.7), new("Danke.", 3.0, 3.4)];
        SpeakerTurn[] turns = [new(1, 0.0, 4.0)];
        Assert.Equal(
            [new SpeakerAlignment.Paragraph(1, 0.0, "Kannst du?"), new SpeakerAlignment.Paragraph(1, 3.0, "Danke.")],
            SpeakerAlignment.Paragraphs(words, turns, [1.2]));
        Assert.Single(SpeakerAlignment.Paragraphs(words, turns));
    }

    [Fact]
    public void WordBetweenTurnsGoesToTheClosestOne()
    {
        TimedWord[] words = [new("Ja", 1.2, 1.3)];
        SpeakerTurn[] turns = [new(1, 0, 1.0), new(2, 1.5, 2.0)];
        Assert.Equal(1, SpeakerAlignment.Paragraphs(words, turns)[0].Speaker);
    }

    [Fact]
    public void WithoutTurnsLongPausesStartNewParagraphs()
    {
        TimedWord[] words = [new("Erster", 0.0, 0.4), new("Satz.", 0.5, 0.9), new("Zweiter.", 3.0, 3.4)];
        var paragraphs = SpeakerAlignment.Paragraphs(words, []);
        Assert.Equal(["Erster Satz.", "Zweiter."], paragraphs.Select(p => p.Text));
        Assert.Equal([1], paragraphs.Select(p => p.Speaker).Distinct());
    }
}

public sealed class SpeakerTurnsTests
{
    [Fact]
    public void NumbersSpeakersByFirstAppearanceMergesAndDropsShortTurns()
    {
        DiarizationSegment[] segments =
        [
            new("spk_b", 5.0, 6.0),
            new("spk_a", 0.0, 2.0),
            new("spk_a", 2.5, 4.0),
            new("spk_a", 7.0, 7.2),
        ];
        Assert.Equal([new SpeakerTurn(1, 0.0, 4.0), new SpeakerTurn(2, 5.0, 6.0)], SpeakerTurns.FromSegments(segments));
    }
}

public sealed class MeetingTranscriptTests
{
    [Fact]
    public void OnlineMeetingPutsTheMicrophoneFirstAsSpeakerZeroAndSplitsTheOtherSide()
    {
        TimedWord[] system = [new("Kannst", 0.0, 0.3), new("du?", 0.4, 0.7), new("Danke.", 3.0, 3.4)];
        TimedWord[] microphone = [new("Ja,", 1.2, 1.4), new("klar.", 1.5, 1.8)];
        var turns = MeetingTranscript.BuildTurns(microphone, system, [new SpeakerTurn(1, 0.0, 4.0)], text => text.ToUpperInvariant());
        Assert.Equal(["1:KANNST DU?", "0:JA, KLAR.", "1:DANKE."], turns.Select(t => $"{t.Speaker}:{t.Text}"));
    }
}

public sealed class TextPartsTests
{
    private const string Text = "Ich: Wir fangen mit den Zahlen an.\nAndere: Die liegen seit gestern vor.\nIch: Gut, dann weiter.";

    [Fact]
    public void ShortTextFitsWhole()
    {
        Assert.Equal(Text.Length, TextParts.EndOfLeadingPart(Text, 1_000));
        Assert.Equal(0, TextParts.StartOfTrailingPart(Text, 1_000));
    }

    [Fact]
    public void LeadingPartEndsAfterAWholeLine() =>
        Assert.Equal("Ich: Wir fangen mit den Zahlen an.\n", Text[..TextParts.EndOfLeadingPart(Text, 50)]);

    [Fact]
    public void LeadingPartOfOneLongLineEndsAfterAWord()
    {
        const string line = "Andere: Die liegen seit gestern vor und sind vollständig.";
        Assert.Equal("Andere: Die liegen ", line[..TextParts.EndOfLeadingPart(line, 20)]);
    }

    [Fact]
    public void TrailingPartStartsAtAWholeLine() =>
        Assert.Equal("Ich: Gut, dann weiter.", Text[TextParts.StartOfTrailingPart(Text, 40)..]);

    [Fact]
    public void PartsCoverEverything()
    {
        var rest = Text;
        var parts = new List<string>();
        while (rest.Length > 0)
        {
            var end = TextParts.EndOfLeadingPart(rest, 30);
            parts.Add(rest[..end]);
            rest = rest[end..];
        }
        Assert.Equal(Text, string.Concat(parts));
        Assert.All(parts, part => Assert.True(part.Length <= 30));
    }
}

public sealed class ListDisplayTests
{
    [Fact]
    public void BulletPointsGetABullet() => Assert.Equal(
        "Entscheidungen\n• Der Workshop findet online statt\n\nTo-dos\n• Paul: Angebot schicken",
        ListDisplay.Bullets("Entscheidungen\n- Der Workshop findet online statt\n\nTo-dos\n- Paul: Angebot schicken"));

    [Fact]
    public void OtherDashesStay() =>
        Assert.Equal("Zahlen -5 % zum Vorjahr\n-Keine", ListDisplay.Bullets("Zahlen -5 % zum Vorjahr\n-Keine"));
}
