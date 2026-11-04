using System.Text;
using System.Text.RegularExpressions;

namespace Diktat.Core.Text;

/// Drops text Whisper invents: subtitle credits from its training data ("Untertitel im Auftrag
/// des ZDF, 2021") and polite closings on silence ("Thank you.", "Vielen Dank."). Whisper's
/// no-speech probability is not reliable for these cases, so Diktat checks how much of the
/// recording is loud enough to be speech instead.
public static class HallucinationFilter
{
    /// Credits and sign-offs that are never meant as dictation. Normalized, without years.
    public static readonly IReadOnlySet<string> Credits = new HashSet<string>(StringComparer.Ordinal)
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
    public static readonly IReadOnlySet<string> DoubtfulPhrases = new HashSet<string>(StringComparer.Ordinal)
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

    /// Less speech than this in a stretch of the recording while the rest has speech: room noise
    /// at the end, which Whisper decodes as a window of its own and turns into "Thank you.".
    public const double MinimumVoicedSecondsInSegment = 0.2;

    /// A stretch of the recording that Whisper decoded on its own, with how much speech it holds.
    public sealed record Segment(string Text, double VoicedSeconds);

    /// Which segments hold what was said: a segment with hardly any speech is dropped when other
    /// segments have speech. A recording with no speech at all is left to <see cref="IsInvented"/>.
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

    public static IReadOnlyList<Segment> RemovingSilentSegments(IReadOnlyList<Segment> segments)
    {
        var flags = KeepFlags(segments.Select(s => s.VoicedSeconds).ToList());
        return segments.Where((_, index) => flags[index]).ToList();
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

    private static readonly Regex ControlToken = new(@"<\|[^|]*\|>", RegexOptions.CultureInvariant);

    /// Whisper's control tokens ("&lt;|de|&gt;", "&lt;|3.20|&gt;") are no text.
    public static string StrippingTokens(string text) => ControlToken.Replace(text, "").Trim();

    /// Seconds of audio loud enough to be speech: 30 ms frames above about -40 dBFS.
    public static double VoicedSeconds(ReadOnlySpan<float> samples, double sampleRate = 16_000)
    {
        var frameLength = Math.Max(1, (int)(sampleRate * 0.03));
        var voicedFrames = 0;
        for (var start = 0; start + frameLength <= samples.Length; start += frameLength)
        {
            float sum = 0;
            for (var index = start; index < start + frameLength; index++) sum += samples[index] * samples[index];
            if (MathF.Sqrt(sum / frameLength) > 0.01f) voicedFrames++;
        }
        return (double)voicedFrames * frameLength / sampleRate;
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

    /// Start of the last one to three sentences when together they are a credit.
    private static int? TrailingCreditStart(string text)
    {
        if (text.Length == 0) return null;
        var starts = SentenceStarts(text);
        for (var i = starts.Count - 1; i >= Math.Max(0, starts.Count - 3); i--)
        {
            if (Credits.Contains(PhraseKey(text[starts[i]..]))) return starts[i];
        }
        return null;
    }

    /// Index 0 and every position after ".", "!", "?" or "…" plus whitespace, or after a line
    /// break. "Amara.org" stays one sentence.
    private static List<int> SentenceStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];
            var next = index + 1;
            if (TextScan.IsNewline(c) && next < text.Length)
            {
                // "\r\n" is one line break.
                if (c == '\r' && text[next] == '\n') continue;
                starts.Add(next);
            }
            else if (".!?…".Contains(c) && next < text.Length && char.IsWhiteSpace(text[next]))
            {
                starts.Add(next);
            }
        }
        return starts;
    }

    /// Lowercased letters and digits, everything else becomes a single space.
    public static string Normalized(string text)
    {
        var lowered = text.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        var result = new StringBuilder(lowered.Length);
        var pendingSpace = false;
        var previousWasWord = false;
        for (var i = 0; i < lowered.Length;)
        {
            var rune = TextScan.RuneAt(lowered, i, out var length);
            if (TextScan.IsLetterOrNumber(rune) || (previousWasWord && TextScan.IsMark(rune)))
            {
                if (pendingSpace && result.Length > 0) result.Append(' ');
                result.Append(lowered, i, length);
                pendingSpace = false;
                previousWasWord = true;
            }
            else
            {
                pendingSpace = true;
                previousWasWord = false;
            }
            i += length;
        }
        return result.ToString();
    }

    /// Like <see cref="Normalized"/>, without numbers, so "ZDF, 2021" and "ZDF 2017" match "zdf".
    public static string PhraseKey(string text) =>
        string.Join(" ", Normalized(text)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => !word.EnumerateRunes().All(Rune.IsNumber)));
}
