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
}
