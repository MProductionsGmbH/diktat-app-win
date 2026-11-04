using Diktat.Core.Settings;
using Diktat.Core.Text;

namespace Diktat.AI;

/// The model said there was nothing to work with ("Keine Aufnahme erkannt.").
public sealed class NothingRecognizedException : Exception
{
    public const string DefaultMessage = "Keine Aufnahme erkannt.";

    public NothingRecognizedException() : base(DefaultMessage) { }
}

/// The AI step of KI-Schreibstil, WTF and :-): transcript (and, when allowed, the text around
/// the cursor) in, finished text out. Recording, transcription, post-processing and pasting are
/// the app's; it hands over the post-processed transcript.
///
/// Errors: <see cref="TextRewriterException"/> (no model), <see cref="AIClientException"/>
/// (provider), <see cref="NothingRecognizedException"/>. Each message is German text for the user.
public sealed class ModeRewriter
{
    private readonly ITextRewriter _rewriter;

    public ModeRewriter(ITextRewriter rewriter)
    {
        _rewriter = rewriter;
    }

    /// The status line while the AI step runs.
    public static string StatusText(WorkflowType mode) => mode switch
    {
        WorkflowType.DampfAblassen => "Wird umformuliert ...",
        WorkflowType.EmojiText => "Emojis werden eingefügt ...",
        _ => "Text wird verbessert ...",
    };

    public bool CanRewrite(RewriteEngine engine) => _rewriter.CanRewrite(engine);

    /// KI-Schreibstil.
    /// <param name="languageSetting">The dictation language setting: "de", "en" or "auto".</param>
    /// <param name="fieldContext">Text around the cursor, null when not allowed or not readable.</param>
    public async Task<string> ImproveTextAsync(string transcript, TextImprovementSettings settings, string languageSetting,
        RewriteEngine engine, string? fieldContext = null, CancellationToken cancellationToken = default)
    {
        var language = PromptLanguages.Resolve(languageSetting, transcript);
        var instruction = ModePrompts.WithFieldContext(ModePrompts.TextImprovement(settings, language), fieldContext, language);
        var improved = await _rewriter.RewriteAsync(transcript, instruction, engine, language, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return improved.Trim();
    }

    /// WTF: a calm message from frustrated speech.
    public async Task<string> DampfAblassenAsync(string transcript, DampfAblassenSettings settings, string languageSetting,
        RewriteEngine engine, string? fieldContext = null, CancellationToken cancellationToken = default)
    {
        var language = PromptLanguages.Resolve(languageSetting, transcript);
        var instruction = ModePrompts.WithFieldContext(ModePrompts.DampfAblassen(settings, language), fieldContext, language);
        var answer = (await _rewriter.RewriteAsync(transcript, instruction, engine, language, cancellationToken: cancellationToken)
            .ConfigureAwait(false)).Trim();
        if (answer == ModePrompts.NothingRecognizedMarker) throw new NothingRecognizedException();
        return answer;
    }

    /// :-) the text with emojis.
    public async Task<string> EmojiAsync(string transcript, EmojiTextSettings settings, string languageSetting,
        RewriteEngine engine, string? fieldContext = null, CancellationToken cancellationToken = default)
    {
        var language = PromptLanguages.Resolve(languageSetting, transcript);
        var instruction = ModePrompts.WithFieldContext(ModePrompts.Emoji(settings, language), fieldContext, language);
        var result = (await _rewriter.RewriteAsync(transcript, instruction, engine, language, cancellationToken: cancellationToken)
            .ConfigureAwait(false)).Trim();
        if (result == ModePrompts.NothingRecognizedMarker) throw new NothingRecognizedException();
        return result;
    }

    /// The AI step of a mode with the saved settings. Modes without one (Diktat) return the transcript.
    public Task<string> RewriteAsync(WorkflowType mode, string transcript, SettingsContainer settings, string? fieldContext = null,
        CancellationToken cancellationToken = default)
    {
        var language = settings.Transcription.Language;
        var engine = settings.App.RewriteEngine;
        return mode switch
        {
            WorkflowType.TextImprover => ImproveTextAsync(transcript, settings.TextImprovement, language, engine, fieldContext, cancellationToken),
            WorkflowType.DampfAblassen => DampfAblassenAsync(transcript, settings.DampfAblassen, language, engine, fieldContext, cancellationToken),
            WorkflowType.EmojiText => EmojiAsync(transcript, settings.EmojiText, language, engine, fieldContext, cancellationToken),
            _ => Task.FromResult(transcript),
        };
    }
}
