using Diktat.Core.Text;
using Diktat.Transcription.Meeting;

namespace Diktat.Transcription.Tests;

public class MeetingTranscriberTests
{
    private static List<TimedWord> Words(double start, params string[] words) =>
        words.Select((w, i) => new TimedWord(w, start + i * 0.4, start + i * 0.4 + 0.3)).ToList();

    [Fact]
    public async Task OnlineMeetingMicrophoneIsIchAndTheSystemSoundIsSplitIntoPeople()
    {
        var mic = new float[16_000 * 30];
        var system = new float[16_000 * 30];
        // Mic: own words at 0 s and 20 s, plus echo of the others at 5 s (heard via the speakers).
        var micWords = Words(0, "Hallo", "zusammen", "wie", "geht's")
            .Concat(Words(5.1, "Uns", "geht", "es", "gut", "danke"))
            .Concat(Words(20, "Dann", "fangen", "wir", "an"))
            .ToList();
        var systemWords = Words(5, "Uns", "geht", "es", "gut", "danke")
            .Concat(Words(10, "Ich", "habe", "eine", "Frage"))
            .ToList();
        var transcriber = new FakeTranscriber("fake", "")
        {
            Handler = samples => new TranscriptionResult("x", "de", ReferenceEquals(samples, mic) ? micWords : systemWords),
        };
        var diarizer = new FakeDiarizer([new SpeakerTurn(1, 4.5, 8), new SpeakerTurn(2, 9.5, 12)]);

        var result = await new MeetingTranscriber(transcriber, diarizer).TranscribeAsync(mic, system, "auto");

        Assert.Same(system, diarizer.Input);
        Assert.Null(result.Notice);
        Assert.Equal(
            [
                ("Ich", "Hallo zusammen wie geht's"),
                ("Person 1", "Uns geht es gut danke"),
                ("Person 2", "Ich habe eine Frage"),
                ("Ich", "Dann fangen wir an"),
            ],
            result.Segments.Select(s => (s.SpeakerLabel, s.Text)));
        Assert.Equal(2, result.OtherSpeakerCount);
    }

    [Fact]
    public async Task RoomMeetingDiarizesTheMicrophone()
    {
        var mic = new float[16_000 * 10];
        var transcriber = new FakeTranscriber("fake", "")
        {
            Handler = _ => new TranscriptionResult("x", "de", Words(0, "Guten", "Morgen").Concat(Words(3, "Morgen", "Anna")).ToList()),
        };
        var diarizer = new FakeDiarizer([new SpeakerTurn(1, 0, 1.2), new SpeakerTurn(2, 2.8, 4)]);

        var result = await new MeetingTranscriber(transcriber, diarizer).TranscribeAsync(mic, null, "de");

        Assert.Same(mic, diarizer.Input);
        Assert.Equal([(1, "Guten Morgen"), (2, "Morgen Anna")], result.Segments.Select(s => (s.Speaker, s.Text)));
    }

    [Fact]
    public async Task WithoutTheDiarizationModelEveryoneIsPerson1WithANotice()
    {
        var transcriber = new FakeTranscriber("fake", "") { Handler = _ => new TranscriptionResult("x", null, Words(0, "Eins", "zwei")) };
        var result = await new MeetingTranscriber(transcriber, null).TranscribeAsync(new float[16_000], null, "auto");
        Assert.Equal("Person 1", Assert.Single(result.Segments).SpeakerLabel);
        Assert.Contains("Sprecher-Erkennung", result.Notice);
    }

    [Fact]
    public async Task NoSpeechIsAnError()
    {
        var transcriber = new FakeTranscriber("fake", "");
        var error = await Assert.ThrowsAsync<MeetingTranscriptionException>(() =>
            new MeetingTranscriber(transcriber, new FakeDiarizer([])).TranscribeAsync(new float[16_000], null, "auto"));
        Assert.Equal("In der Aufnahme wurde keine Sprache erkannt.", error.Message);
    }

    private sealed class FakeDiarizer(IReadOnlyList<SpeakerTurn> turns) : ISpeakerDiarizer
    {
        public float[]? Input { get; private set; }

        public Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(float[] samples16kMono, int? speakerCount = null, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            Input = samples16kMono;
            return Task.FromResult(turns);
        }
    }
}

public class SpeakerAlignmentTests
{
    [Fact]
    public void NormalizeTurnsNumbersByFirstAppearanceMergesAndDropsScraps()
    {
        var turns = SpeakerAlignment.NormalizeTurns(
        [
            (5.0, 6.0, 7),
            (0.0, 2.0, 3),
            (2.5, 4.0, 3),   // same speaker, 0.5 s later: merged
            (4.0, 4.2, 9),   // 0.2 s: dropped, and 9 leaves no gap in the numbering
            (8.0, 9.0, 11),
        ]);
        Assert.Equal(
            [new SpeakerTurn(1, 0, 4), new SpeakerTurn(2, 5, 6), new SpeakerTurn(3, 8, 9)],
            turns);
    }

    [Fact]
    public void WithoutTurnsParagraphsBreakAtPauses()
    {
        var words = new List<TimedWord> { new("Eins", 0, 0.3), new("zwei", 0.4, 0.7), new("drei", 3.0, 3.3) };
        var paragraphs = SpeakerAlignment.Paragraphs(words, []);
        Assert.Equal(["Eins zwei", "drei"], paragraphs.Select(p => p.Text));
        Assert.Equal(0.7, paragraphs[0].End);
    }

    [Fact]
    public void EchoFilterKeepsOwnWordsWithHeadphones()
    {
        var mic = new List<TimedWord> { new("Mach", 10, 10.3), new("ich", 10.4, 10.6) };
        var system = new List<TimedWord> { new("Kannst", 0, 0.3), new("du", 0.4, 0.5), new("das", 0.6, 0.8) };
        Assert.Equal(mic, EchoFilter.RemoveEcho(mic, system));
    }
}

public class HallucinationFilterTests
{
    [Theory]
    [InlineData("Wir sehen uns morgen. Untertitel im Auftrag des ZDF, 2021.", "Wir sehen uns morgen.")]
    [InlineData("Thanks for watching!", "")]
    [InlineData("Das ist ein Test.", "Das ist ein Test.")]
    [InlineData("Ich war auf Amara.org.", "Ich war auf Amara.org.")]
    public void RemovesTrailingCredits(string input, string expected)
    {
        Assert.Equal(expected, HallucinationFilter.RemovingTrailingCredits(input));
    }

    [Fact]
    public void ClosingsOnSilenceAreInvented()
    {
        Assert.True(HallucinationFilter.IsInvented("Vielen Dank.", voicedSeconds: 0));
        Assert.False(HallucinationFilter.IsInvented("Vielen Dank.", voicedSeconds: 2));
        Assert.True(HallucinationFilter.IsInvented("Untertitel des ZDF 2017", voicedSeconds: 5));
    }

    [Fact]
    public void VoicedSecondsCountsLoudFrames()
    {
        var samples = new float[32_000];
        TestAudio.Sine(300, 16_000, 1.0, 0.3f).CopyTo(samples, 0);
        Assert.InRange(HallucinationFilter.VoicedSeconds(samples), 0.95, 1.05);
        Assert.Equal("", HallucinationFilter.StrippingTokens("<|de|>[_BEG_] "));
    }

    [Fact]
    public void LanguageCodes()
    {
        Assert.Null(TranscriptionLanguage.Code("auto"));
        Assert.Null(TranscriptionLanguage.Code(""));
        Assert.Equal("de", TranscriptionLanguage.Code("de-CH"));
        Assert.Equal("en", TranscriptionLanguage.Code(" EN "));
    }

    [Fact]
    public void TokensBecomeWords()
    {
        var words = TimedWord.FromTokens([("▁Hal", 0.0, 0.1), ("lo", 0.1, 0.2), ("▁Welt", 0.3, 0.5), (".", 0.5, 0.55), (" again", 0.6, 0.8)]);
        Assert.Equal([new TimedWord("Hallo", 0, 0.2), new TimedWord("Welt.", 0.3, 0.55), new TimedWord("again", 0.6, 0.8)], words);
    }
}
