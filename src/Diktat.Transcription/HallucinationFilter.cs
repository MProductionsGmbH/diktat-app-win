using System.Text;
using System.Text.RegularExpressions;

namespace Diktat.Transcription;

/// Drops text Whisper invents: subtitle credits from its training data ("Untertitel im
/// Auftrag des ZDF, 2021") and polite closings on silence ("Thank you.", "Vielen Dank.").
/// Port of the macOS HallucinationFilter: it checks how much of the recording is loud
/// enough to be speech instead of trusting the model's no-speech probability.
public static partial class HallucinationFilter
{
    /// Credits and sign-offs that are never meant as dictation. Normalized, without years.
    public static readonly IReadOnlySet<string> Credits = new HashSet<string>
    {
        "untertitel im auftrag des zdf",
        "untertitel im auftrag des zdf für funk",
        "untertitelung im auftrag des zdf",
        "untertitelung des zdf",
        "untertitel des zdf",
        "untertitel der amara org community",
        "untertitel von der amara org community",
        "untertitel von amara org",
        "untertitel von stephanie geiges",
        "copyright wdr",
        "copyright swr",
        "vielen dank fürs zuschauen",
        "vielen dank für s zuschauen",
        "danke fürs zuschauen",
        "danke für s zuschauen",
        "abonniert den kanal",
        "bitte abonnieren",
        "abonniere meinen kanal",
        "subtitles by the amara org community",
        "subtitles by amara org",
        "thanks for watching",
        "thank you for watching",
        "thank you so much for watching",
        "please subscribe",
        "like and subscribe",
        "please like and subscribe",
    };

    /// Short phrases people do say, so they only count as invented on a silent recording.
    public static readonly IReadOnlySet<string> DoubtfulPhrases = new HashSet<string>
    {
        "vielen dank", "danke", "danke schön", "dankeschön", "tschüss", "tschüs",
        "bis zum nächsten mal", "bis bald", "bis dann", "das war s", "ende",
        "thank you", "thanks", "thank you very much", "you", "bye", "bye bye",
        "okay", "ok", "ja", "hmm", "hm", "mhm",
        "musik", "music", "applaus", "applause", "lachen",
        "swr", "wdr", "zdf", "ard",
    };

    /// Less speech than this and a doubtful phrase counts as invented.
    public const double MinimumVoicedSeconds = 0.3;

    /// Less speech than this in one segment while the rest has speech: room noise at the end.
    public const double MinimumVoicedSecondsInSegment = 0.2;

    public readonly record struct Segment(string Text, double VoicedSeconds);

    /// Which segments hold what was said: a segment with hardly any speech is dropped when
    /// other segments have speech.
    public static bool[] KeepFlags(IReadOnlyList<double> voicedSeconds)
    {
        var total = voicedSeconds.Sum();
        return voicedSeconds.Select(voiced =>
        {
            var isSilent = voiced < MinimumVoicedSecondsInSegment;
            var othersSpeak = total - voiced >= MinimumVoicedSeconds;
            return !(isSilent && othersSpeak);
        }).ToArray();
    }

    public static List<Segment> RemovingSilentSegments(IReadOnlyList<Segment> segments)
    {
        var flags = KeepFlags(segments.Select(s => s.VoicedSeconds).ToList());
        return segments.Where((_, i) => flags[i]).ToList();
    }

    /// True when the whole transcript is a credit, or a doubtful phrase on a recording with
    /// hardly any speech.
    public static bool IsInvented(string text, double voicedSeconds)
    {
        var key = PhraseKey(text);
        if (key.Length == 0) return false;
        if (Credits.Contains(key)) return true;
        return voicedSeconds < MinimumVoicedSeconds && DoubtfulPhrases.Contains(key);
    }

    /// Whisper's control tokens ("<|de|>", "<|3.20|>", "[_BEG_]") and whisper.cpp's
    /// annotations in capitals ("[BLANK_AUDIO]", "[MUSIC]") are no text.
    public static string StrippingTokens(string text) =>
        ControlTokens().Replace(text, "").Trim();

    [GeneratedRegex(@"<\|[^|]*\|>|\[_[A-Z]+_?\d*\]|\[[A-Z][A-Z_ ]*\]")]
    private static partial Regex ControlTokens();

    /// Seconds of audio loud enough to be speech: 30 ms frames above about -40 dBFS.
    public static double VoicedSeconds(ReadOnlySpan<float> samples, int sampleRate = 16_000)
    {
        var frameLength = Math.Max(1, (int)(sampleRate * 0.03));
        var voicedFrames = 0;
        for (var start = 0; start + frameLength <= samples.Length; start += frameLength)
        {
            float sum = 0;
            foreach (var sample in samples.Slice(start, frameLength)) sum += sample * sample;
            if (MathF.Sqrt(sum / frameLength) > 0.01f) voicedFrames++;
        }
        return (double)voicedFrames * frameLength / sampleRate;
    }

    /// Voiced seconds between two times of a 16 kHz recording.
    public static double VoicedSeconds(float[] samples, double startSeconds, double endSeconds)
    {
        var from = Math.Clamp((int)(startSeconds * 16_000), 0, samples.Length);
        var to = Math.Clamp((int)(endSeconds * 16_000), from, samples.Length);
        return VoicedSeconds(samples.AsSpan(from, to - from));
    }

    /// Removes credits at the end of a transcript ("... bis morgen. Untertitel im Auftrag des
    /// ZDF, 2021."). Works on any engine's text, so Parakeet output is covered too.
    public static string RemovingTrailingCredits(string text)
    {
        var result = text.Trim();
        while (TrailingCreditStart(result) is int cut)
        {
            result = result[..cut].Trim();
        }
        return result;
    }

    private static int? TrailingCreditStart(string text)
    {
        if (text.Length == 0) return null;
        var starts = SentenceStarts(text);
        foreach (var start in starts.TakeLast(3).Reverse())
        {
            if (Credits.Contains(PhraseKey(text[start..]))) return start;
        }
        return null;
    }

    /// Index 0 and every position after ".", "!", "?" or "…" plus whitespace, or after a line
    /// break. "Amara.org" stays one sentence.
    private static List<int> SentenceStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            var next = i + 1;
            if (next >= text.Length) break;
            var c = text[i];
            if (c is '\n' or '\r')
            {
                if (!(c == '\r' && text[next] == '\n')) starts.Add(next);
            }
            else if (".!?…".Contains(c) && char.IsWhiteSpace(text[next]))
            {
                starts.Add(next);
            }
        }
        return starts;
    }

    /// Lowercased letters and digits, everything else becomes a single space.
    public static string Normalized(string text)
    {
        var result = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && result.Length > 0) result.Append(' ');
                result.Append(c);
                pendingSpace = false;
            }
            else
            {
                pendingSpace = true;
            }
        }
        return result.ToString();
    }

    /// Like <see cref="Normalized"/>, without numbers, so "ZDF, 2021" and "ZDF 2017" match "zdf".
    public static string PhraseKey(string text) =>
        string.Join(' ', Normalized(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !w.All(char.IsDigit)));
}
