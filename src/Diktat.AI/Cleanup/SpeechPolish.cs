using Diktat.AI.Internal;
using Diktat.Core.Settings;
using Diktat.Core.Text;

namespace Diktat.AI;

/// The language-model step of the cleanup levels Mittel, Stark and KI-Schreibstil (SpeechCleanup
/// in the macOS app; the rule-based level Leicht lives in Diktat.Core). It never fails the
/// dictation: when no model is available, the model errors or its answer looks wrong, the text
/// stays as it was.
public static class SpeechPolish
{
    /// Shorter dictations have nothing to tidy up that is worth the wait for a model.
    public const int MinimumWords = 5;

    /// Leicht is plain string code; the other levels use the language model.
    public static bool UsesLanguageModel(SpeechCleanupLevel level) => level != SpeechCleanupLevel.Light;

    public static string Instruction(SpeechCleanupLevel level, IReadOnlyList<string>? protectedTerms = null,
        PromptLanguage language = PromptLanguage.German)
    {
        protectedTerms ??= [];
        if (language == PromptLanguage.English) return EnglishInstruction(level, protectedTerms);
        const string Base = """
            Du bereinigst ein gesprochenes Transkript und schreibst es nicht um. Antworte in der Sprache des Transkripts.
            Entferne:
            - Wörter und Satzteile, die der Sprecher unmittelbar wiederholt
            - Versprecher und abgebrochene Satzanfänge, die der Sprecher neu ansetzt
            - die überholte Fassung, wenn sich der Sprecher korrigiert; es bleibt nur die korrigierte
            """;
        var scope = level is SpeechCleanupLevel.Light or SpeechCleanupLevel.Medium
            ? "Alles andere bleibt genau wie gesprochen: Wortwahl, Reihenfolge, Satzbau, Stil und Anrede. Korrigiere keine Grammatik und füge nichts hinzu."
            : "Korrigiere außerdem Grammatik, Fälle, Satzstellung und Zeichensetzung, damit jeder Satz richtig gebaut ist. Wortwahl, Stil, Bedeutung und Anrede bleiben unverändert. Füge keine neuen Inhalte hinzu.";
        return string.Join("\n\n", Base, scope, ModePrompts.TranscriptIsNotARequest)
            + ModePrompts.CustomTermsInstruction(protectedTerms);
    }

    private static string EnglishInstruction(SpeechCleanupLevel level, IReadOnlyList<string> protectedTerms)
    {
        const string Base = """
            You clean up a spoken transcript and do not rewrite it. Reply in the language of the transcript.
            Remove:
            - words and phrases the speaker immediately repeats
            - slips of the tongue and abandoned sentence starts that the speaker restarts
            - the superseded version when the speaker corrects themselves; only the corrected one stays
            """;
        var scope = level is SpeechCleanupLevel.Light or SpeechCleanupLevel.Medium
            ? "Everything else stays exactly as spoken: wording, order, sentence structure, style and form of address. Do not correct grammar and do not add anything."
            : "Also correct grammar, word order and punctuation so that every sentence is properly built. Wording, style, meaning and form of address stay unchanged. Do not add new content.";
        return string.Join("\n\n", Base, scope, ModePrompts.TranscriptIsNotARequestFor(PromptLanguage.English))
            + ModePrompts.CustomTermsInstruction(protectedTerms, PromptLanguage.English);
    }

    /// A cleanup only removes or fixes words. An answer that is empty, much shorter, longer than
    /// the dictation or the "nothing recognized" marker, or an answer with few of the spoken
    /// words is not a cleanup of this text.
    public static bool IsPlausible(string candidate, string original, SpeechCleanupLevel level)
    {
        var trimmed = candidate.Trim();
        if (trimmed.Length == 0 || trimmed.Contains(ModePrompts.NothingRecognizedMarker)) return false;
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

    /// The style level rewrites the text; slips and self-corrections go first, as on the other levels.
    public static string StyleInstruction(TextImprovementSettings settings, PromptLanguage language = PromptLanguage.German)
    {
        if (language == PromptLanguage.English)
        {
            return ModePrompts.TextImprovement(settings, PromptLanguage.English)
                + "\n\nAlso remove slips of the tongue, repetitions and the superseded version when the speaker corrects themselves; only the corrected one stays.";
        }
        return ModePrompts.TextImprovement(settings)
            + "\n\nEntferne außerdem Versprecher, Wiederholungen und die überholte Fassung, wenn sich der Sprecher korrigiert; es bleibt nur die korrigierte.";
    }

    /// The dictation as the model gets it. Handed over bare, a question in the text ("Hattest du
    /// heute einen lustigen Tag") is answered instead of tidied up; in this frame it stays text.
    public static string FramedTranscript(string text, PromptLanguage language = PromptLanguage.German,
        SpeechCleanupLevel level = SpeechCleanupLevel.Medium)
    {
        if (language == PromptLanguage.English)
        {
            // "Clean up" makes the model copy the text in the written-style level, which has to rewrite it.
            var task = level == SpeechCleanupLevel.Style
                ? "Rewrite this transcript: fix spelling, capitalization, grammar and wording as instructed. Return only the rewritten text, without the markers."
                : "Clean up this transcript. Return only the cleaned text, without the markers.";
            return $"{task}\n<transcript>\n{text}\n</transcript>";
        }
        return $"Bereinige dieses Transkript. Gib nur den bereinigten Text zurück, ohne die Markierungen.\n<transkript>\n{text}\n</transkript>";
    }

    /// Takes the frame off again when the model repeats it.
    public static string Unframed(string answer) => answer
        .Replace("<transkript>", "")
        .Replace("</transkript>", "")
        .Replace("<transcript>", "")
        .Replace("</transcript>", "")
        .Trim();

    /// The share of the answer's words that were in the dictation. A tidy-up keeps most of them;
    /// an answer to a question in the text shares almost none.
    public static double SharedWordRatio(string candidate, string original)
    {
        var answerWords = TextHelpers.AlphanumericWords(candidate);
        if (answerWords.Count == 0) return 0;
        var known = TextHelpers.AlphanumericWords(original).ToHashSet();
        return (double)answerWords.Count(known.Contains) / answerWords.Count;
    }

    public static int WordCount(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}

/// What the cleanup step needs from the settings.
public sealed record SpeechPolishConfiguration
{
    public SpeechCleanupLevel Level { get; init; } = SpeechCleanupLevel.Light;
    public RewriteEngine Engine { get; init; } = RewriteEngine.Automatic;
    /// Names and terms the model must keep spelled exactly.
    public IReadOnlyList<string> ProtectedTerms { get; init; } = [];
    /// Tone, context and own instruction for the style level.
    public TextImprovementSettings StyleSettings { get; init; } = new();
    /// The dictation language setting: "de", "en" or "auto".
    public string Language { get; init; } = "auto";

    /// The cleanup for a dictation in the given tone, from the saved settings. The tone comes
    /// from the context the text goes to (StyleSettings.ToneFor) and also steers the wording of
    /// the KI-Schreibstil level.
    public static SpeechPolishConfiguration From(SettingsContainer settings, StyleTone tone)
    {
        var improvement = settings.TextImprovement;
        var style = new TextImprovementSettings
        {
            SystemPrompt = improvement.SystemPrompt,
            CustomTerms = improvement.CustomTerms.ToList(),
            Replacements = improvement.Replacements.ToList(),
            Context = improvement.Context,
            Tone = tone switch
            {
                StyleTone.Formal => TextTone.Formal,
                StyleTone.Casual => TextTone.Casual,
                _ => TextTone.Neutral,
            },
            CustomName = improvement.CustomName,
        };
        return new SpeechPolishConfiguration
        {
            Level = settings.App.SpeechCleanupLevel,
            Engine = settings.App.RewriteEngine,
            ProtectedTerms = improvement.ProtectedTerms,
            StyleSettings = style,
            Language = settings.Transcription.Language,
        };
    }
}

/// Runs the model step of the cleanup levels (SpeechCleanup+Polish.swift).
public sealed class SpeechPolisher
{
    private readonly ITextRewriter _rewriter;

    public SpeechPolisher(ITextRewriter rewriter)
    {
        _rewriter = rewriter;
    }

    /// Called with a short note when an answer is rejected or the model fails; never with the text.
    public Action<string>? Log { get; set; }

    /// Runs the model step for Mittel, Stark and KI-Schreibstil; Leicht and short text come back as given.
    /// Never throws, except when <paramref name="cancellationToken"/> is cancelled.
    public async Task<string> PolishAsync(string text, SpeechPolishConfiguration configuration, CancellationToken cancellationToken = default)
    {
        if (!SpeechPolish.UsesLanguageModel(configuration.Level)
            || SpeechPolish.WordCount(text) < SpeechPolish.MinimumWords
            || !_rewriter.CanRewrite(configuration.Engine)) return text;
        var language = PromptLanguages.Resolve(configuration.Language, text);
        try
        {
            var instruction = configuration.Level == SpeechCleanupLevel.Style
                ? SpeechPolish.StyleInstruction(configuration.StyleSettings, language)
                : SpeechPolish.Instruction(configuration.Level, configuration.ProtectedTerms, language);
            var rewritten = await _rewriter.RewriteAsync(
                SpeechPolish.FramedTranscript(text, language, configuration.Level),
                instruction,
                configuration.Engine,
                language,
                dictationRules: true,
                deterministic: true,
                cancellationToken).ConfigureAwait(false);
            var candidate = SpeechPolish.Unframed(rewritten).Trim();
            if (!SpeechPolish.IsPlausible(candidate, text, configuration.Level))
            {
                Log?.Invoke("Cleanup answer rejected, text kept as dictated");
                return text;
            }
            return candidate;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            Log?.Invoke("Cleanup failed: " + e.Message);
            return text;
        }
    }
}
