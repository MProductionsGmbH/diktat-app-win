using System.Text;

namespace Diktat.Core.Text;

/// Rejects recordings too short to hold a word and transcripts that look like recognizer noise.
public static class TranscriptionQualityService
{
    public static readonly TimeSpan MinimumRecordingDuration = TimeSpan.FromSeconds(0.3);

    public static bool ShouldRejectRecording(TimeSpan duration) => duration < MinimumRecordingDuration;

    public static string CleanedTranscript(string text) => text.Trim();

    public static bool IsLikelyArtifact(string text, TimeSpan recordingDuration)
    {
        var cleaned = CleanedTranscript(text);
        if (cleaned.Length == 0) return true;

        var words = cleaned.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var letters = cleaned.EnumerateRunes().Count(r => Rune.IsLetter(r) || TextScan.IsMark(r));
        if (letters == 0) return true;

        var seconds = recordingDuration.TotalSeconds;
        var characters = TextScan.CharacterCount(cleaned);
        if (seconds < 0.55 && (words >= 5 || characters >= 32)) return true;
        if (seconds < 0.8 && characters >= 56) return true;
        return false;
    }
}
