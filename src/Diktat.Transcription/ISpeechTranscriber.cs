namespace Diktat.Transcription;

/// A local speech model that turns 16 kHz mono audio into text. Implementations run one
/// transcription at a time; concurrent calls wait for each other.
public interface ISpeechTranscriber : IDisposable
{
    /// The catalog id of the loaded model.
    string ModelId { get; }

    /// <param name="samples16kMono">Samples at 16 kHz, one channel, roughly -1 ... 1.</param>
    /// <param name="language">"auto" (or empty) lets the model detect the language; otherwise
    /// a code such as "de" or "en" (only the first two letters are used).</param>
    Task<TranscriptionResult> TranscribeAsync(float[] samples16kMono, string language, CancellationToken ct = default);
}

/// What a model heard. <see cref="Words"/> carry times in seconds from the start of the
/// samples, so a meeting transcript can be assigned to speakers.
public sealed record TranscriptionResult(string Text, string? Language, IReadOnlyList<TimedWord> Words)
{
    public static TranscriptionResult Empty { get; } = new("", null, []);

    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);
}

/// A transcribed word with its position in the recording, in seconds.
public readonly record struct TimedWord(string Text, double Start, double End)
{
    /// Joins SentencePiece/BPE tokens into words: a token starting with a space or "▁" begins
    /// a new word. Works for Parakeet (sherpa-onnx tokens) and Whisper tokens.
    public static List<TimedWord> FromTokens(IEnumerable<(string Text, double Start, double End)> tokens)
    {
        var words = new List<TimedWord>();
        foreach (var token in tokens)
        {
            var piece = token.Text.Replace('▁', ' ');
            var startsWord = piece.StartsWith(' ') || words.Count == 0;
            var trimmed = piece.Trim();
            if (trimmed.Length == 0) continue;
            if (startsWord)
            {
                words.Add(new TimedWord(trimmed, token.Start, token.End));
            }
            else
            {
                var last = words[^1];
                words[^1] = new TimedWord(last.Text + trimmed, last.Start, Math.Max(last.End, token.End));
            }
        }
        return words;
    }

    public TimedWord Shifted(double seconds) => new(Text, Start + seconds, End + seconds);
}

/// Language codes as the settings store them ("auto", "de", "de-CH", "en").
public static class TranscriptionLanguage
{
    public const string Auto = "auto";

    /// Null for automatic detection, otherwise the two-letter code in lower case.
    public static string? Code(string? language)
    {
        var spoken = (language ?? "").Trim().ToLowerInvariant();
        if (spoken.Length < 2 || spoken == Auto) return null;
        var code = spoken[..2];
        return code.All(char.IsAsciiLetterLower) ? code : null;
    }
}
