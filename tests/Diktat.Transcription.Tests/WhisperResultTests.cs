using Diktat.Transcription.Engines;
using Whisper.net;

namespace Diktat.Transcription.Tests;

public class WhisperResultTests
{
    private static SegmentData Segment(string text, double start, double end, string language = "de", params WhisperToken[] tokens) =>
        new(text, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), 0, 0, 0, 0, language, tokens);

    private static WhisperToken Token(string text, double start, double end) =>
        new() { Text = text, Start = (long)(start * 100), End = (long)(end * 100) };

    [Fact]
    public void BlankAudioIsNoText()
    {
        var result = WhisperTranscriber.BuildResult([Segment(" [BLANK_AUDIO]", 0, 2, "en")], new float[32_000], null);
        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void AClosingInventedOnTrailingNoiseIsDropped()
    {
        // 2 s of speech, then 2 s of near silence that Whisper decoded as "Thank you."
        var samples = new float[64_000];
        TestAudio.Sine(200, 16_000, 2, 0.3f).CopyTo(samples, 0);
        var result = WhisperTranscriber.BuildResult(
            [
                Segment(" Wir treffen uns um drei.", 0, 2, "de", Token(" Wir", 0.1, 0.3), Token(" tref", 0.3, 0.5), Token("fen", 0.5, 0.7), Token(" uns", 0.7, 0.9), Token(" um", 1.0, 1.1), Token(" drei.", 1.2, 1.6), Token("[_TT_80]", 1.6, 1.6)),
                Segment(" Thank you.", 2, 4, "en", Token(" Thank", 2.5, 2.8), Token(" you.", 2.8, 3.0)),
            ],
            samples,
            null);

        Assert.Equal("Wir treffen uns um drei.", result.Text);
        Assert.Equal("de", result.Language);
        Assert.Equal(["Wir", "treffen", "uns", "um", "drei."], result.Words.Select(w => w.Text));
        Assert.Equal(0.3, result.Words[1].Start, 6);
        Assert.Equal(0.7, result.Words[1].End, 6);
    }

    [Fact]
    public void ThankYouOnASilentRecordingIsInvented()
    {
        var result = WhisperTranscriber.BuildResult([Segment(" Vielen Dank.", 0, 2)], new float[32_000], "de");
        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void SegmentsOfALaterPieceKeepTheirPlaceInTheRecording()
    {
        // Speech at 0-2 s and 31-33 s; the second segment comes from a piece that starts at 30 s.
        var samples = new float[34 * 16_000];
        TestAudio.Sine(200, 16_000, 2, 0.3f).CopyTo(samples, 0);
        TestAudio.Sine(200, 16_000, 2, 0.3f).CopyTo(samples, 31 * 16_000);
        var result = WhisperTranscriber.BuildResult(
            [
                Segment(" Erster Satz.", 0, 2, "de", Token(" Erster", 0.1, 0.5), Token(" Satz.", 0.5, 1.0)),
                Segment(" Zweiter Satz.", 1, 3, "de", Token(" Zweiter", 1.1, 1.6), Token(" Satz.", 1.6, 2.0)),
            ],
            samples,
            "de",
            [0, 30]);

        Assert.Equal("Erster Satz. Zweiter Satz.", result.Text);
        Assert.Equal(["Erster", "Satz.", "Zweiter", "Satz."], result.Words.Select(w => w.Text));
        Assert.Equal(31.1, result.Words[2].Start, 6);
        Assert.Equal(32.0, result.Words[3].End, 6);
    }

    [Fact]
    public void AClosingOnTheSilenceOfALaterPieceIsDropped()
    {
        // Speech only in the first 2 s. Without the offset, the second segment would be checked
        // against that speech and kept.
        var samples = new float[34 * 16_000];
        TestAudio.Sine(200, 16_000, 2, 0.3f).CopyTo(samples, 0);
        var result = WhisperTranscriber.BuildResult(
            [Segment(" Wir treffen uns um drei.", 0, 2), Segment(" Vielen Dank.", 0, 2)],
            samples,
            "de",
            [0, 30]);
        Assert.Equal("Wir treffen uns um drei.", result.Text);
    }
}

public class WhisperPiecesTests
{
    private const int Rate = 16_000;

    [Fact]
    public void UpToOneWindowIsOnePiece()
    {
        var samples = new float[30 * Rate];
        Assert.Equal([new AudioChunker.Chunk(0, samples.Length)], WhisperTranscriber.Pieces(samples));
    }

    [Fact]
    public void ALongRecordingIsCutInItsPauses()
    {
        // 75 s of tone with pauses at 24-25 s and 50-51 s: whisper.cpp alone would cut at 30 and 60 s.
        var samples = TestAudio.Sine(200, Rate, 75, 0.3f);
        Array.Clear(samples, 24 * Rate, Rate);
        Array.Clear(samples, 50 * Rate, Rate);

        var pieces = WhisperTranscriber.Pieces(samples);

        Assert.Equal(3, pieces.Count);
        Assert.InRange(pieces[0].End / (double)Rate, 24, 25);
        Assert.InRange(pieces[1].End / (double)Rate, 50, 51);
        Assert.Equal(0, pieces[0].Start);
        Assert.Equal(samples.Length, pieces[^1].End);
        Assert.All(pieces, p => Assert.True(p.Length <= WhisperTranscriber.WindowSeconds * Rate));
        // Nothing is left out between the pieces.
        for (var i = 1; i < pieces.Count; i++) Assert.Equal(pieces[i - 1].End, pieces[i].Start);
    }

    [Fact]
    public void ThePickedLanguageNeedsNoDetection()
    {
        var asked = 0;
        var spoken = WhisperTranscriber.SpokenLanguage("en", new float[90 * Rate], 3, _ => { asked++; return "de"; });
        Assert.Equal("en", spoken);
        Assert.Equal(0, asked);
    }

    [Fact]
    public void ALongRecordingIsAskedOnceForItsLanguage()
    {
        var heard = new List<int>();
        var spoken = WhisperTranscriber.SpokenLanguage(null, new float[90 * Rate], 3, start => { heard.Add(start.Length); return "de"; });
        Assert.Equal("de", spoken);
        // Once, with the first window only.
        Assert.Equal([30 * Rate], heard);
    }

    [Fact]
    public void OnePieceDetectsByItself()
    {
        var asked = 0;
        Assert.Null(WhisperTranscriber.SpokenLanguage(null, new float[10 * Rate], 1, _ => { asked++; return "de"; }));
        Assert.Equal(0, asked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    public void NoAnswerLeavesTheDetectionToEachPiece(string? answer)
    {
        Assert.Null(WhisperTranscriber.SpokenLanguage(null, new float[90 * Rate], 3, _ => answer));
    }
}
