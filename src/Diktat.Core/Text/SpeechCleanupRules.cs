using Diktat.Core.Settings;

namespace Diktat.Core.Text;

/// The pure, model-free parts of the cleanup levels: the plausibility check of a model's answer
/// and the frame around the transcript. The rule-based "Leicht" level itself is
/// <see cref="TranscriptPostProcessor"/> (fillers, <see cref="TranscriptPostProcessor.CollapseStutters"/>,
/// <see cref="TranscriptPostProcessor.CollapseRepeatedPhrases"/>). The prompts and the model call
/// live in Diktat.AI.
public static class SpeechCleanupRules
{
    /// Shorter dictations have nothing to tidy up that is worth the wait for a model.
    public const int MinimumWords = 5;

    /// What the models answer when the recording held nothing (ModePrompts.nothingRecognizedMarker on macOS).
    public const string NothingRecognizedMarker = "KEINE_AUFNAHME_ERKANNT";

    /// A cleanup only removes or fixes words. An answer that is empty, much shorter, longer than
    /// the dictation or the "nothing recognized" marker, or an answer with few of the spoken words
    /// is not a cleanup of this text.
    public static bool IsPlausible(string candidate, string original, SpeechCleanupLevel level)
    {
        var trimmed = candidate.Trim();
        if (trimmed.Length == 0 || trimmed.Contains(NothingRecognizedMarker, StringComparison.Ordinal)) return false;
        double before = WordCount(original);
        double after = WordCount(trimmed);
        // A rewrite may reshape the sentences, so the style level gets more room.
        var (growth, shrink, sharedWords) = level switch
        {
            SpeechCleanupLevel.Style => (1.5, 0.4, 0.35),
            SpeechCleanupLevel.Strong => (1.2, 0.5, 0.6),
            _ => (1.1, 0.5, 0.6),
        };
        if (!(after >= before * shrink && after <= before * growth + 2)) return false;
        return SharedWordRatio(trimmed, original) >= sharedWords;
    }

    /// The dictation as the model gets it. Handed over bare, a question in the text ("Hattest du
    /// heute einen lustigen Tag") is answered instead of tidied up; in this frame it stays text.
    public static string FramedTranscript(string text, PromptLanguage language = PromptLanguage.German, SpeechCleanupLevel level = SpeechCleanupLevel.Medium)
    {
        if (language == PromptLanguage.English)
        {
            // "Clean up" makes a model copy the text in the written-style level, which has to rewrite it.
            var task = level == SpeechCleanupLevel.Style
                ? "Rewrite this transcript: fix spelling, capitalization, grammar and wording as instructed. Return only the rewritten text, without the markers."
                : "Clean up this transcript. Return only the cleaned text, without the markers.";
            return $"{task}\n<transcript>\n{text}\n</transcript>";
        }
        return $"Bereinige dieses Transkript. Gib nur den bereinigten Text zurück, ohne die Markierungen.\n<transkript>\n{text}\n</transkript>";
    }

    /// Takes the frame off again when the model repeats it.
    public static string Unframed(string answer) =>
        answer
            .Replace("<transkript>", "", StringComparison.Ordinal)
            .Replace("</transkript>", "", StringComparison.Ordinal)
            .Replace("<transcript>", "", StringComparison.Ordinal)
            .Replace("</transcript>", "", StringComparison.Ordinal)
            .Trim();

    /// The share of the answer's words that were in the dictation. A tidy-up keeps most of them;
    /// an answer to a question in the text shares almost none.
    public static double SharedWordRatio(string candidate, string original)
    {
        var answerWords = Words(candidate);
        if (answerWords.Count == 0) return 0;
        var known = new HashSet<string>(Words(original), StringComparer.Ordinal);
        return (double)answerWords.Count(known.Contains) / answerWords.Count;
    }

    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var lowered = text.ToLowerInvariant();
        var start = -1;
        for (var i = 0; i <= lowered.Length; i++)
        {
            var isWordChar = i < lowered.Length && (char.IsLetterOrDigit(lowered[i]) || char.GetUnicodeCategory(lowered[i]) is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark);
            if (isWordChar && start < 0) start = i;
            else if (!isWordChar && start >= 0)
            {
                words.Add(lowered[start..i]);
                start = -1;
            }
        }
        return words;
    }

    public static int WordCount(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
