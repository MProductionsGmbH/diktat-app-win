using Diktat.Core.Settings;
using Diktat.Core.Text;

namespace Diktat.AI;

/// Rewrites a transcript with an instruction. On the Mac the engine "automatic" prefers Apple
/// Intelligence on the device; Windows has no such model, so both engines use the provider the
/// user connected (a model server on this PC like Ollama counts as a provider). The engine stays
/// in the settings so a settings export from the Mac imports unchanged.
public interface ITextRewriter
{
    /// Whether any model can run the AI step with this engine setting.
    bool CanRewrite(RewriteEngine engine);

    /// <param name="language">The language of the dictation, for the rules below; detected from the text when null.</param>
    /// <param name="dictationRules">Adds the rules for reading dictated speech (self-corrections, du/Sie).
    /// Their examples name weekdays and numbers, which a small model copies into summaries.</param>
    /// <param name="deterministic">Kept for parity with the Mac, where it picks greedy sampling on the
    /// device. Providers keep their default sampling, so it has no effect here.</param>
    Task<string> RewriteAsync(string text, string instruction, RewriteEngine engine, PromptLanguage? language = null,
        bool dictationRules = true, bool deterministic = false, CancellationToken cancellationToken = default);

    /// The connected provider when it is a model server on this PC itself (Ollama, LM Studio on
    /// localhost), else null. Meetings may use only this: the text never leaves the PC.
    AIProviderConfig? ServerOnThisPC { get; }

    /// The model server on this PC and nothing else, for features that promise the text stays
    /// on the PC whatever the engine setting. Throws <see cref="TextRewriterException"/> when no
    /// such server is set up.
    Task<string> RewriteOnServerOnThisPCAsync(string text, string instruction, CancellationToken cancellationToken = default);
}

/// No model can run the AI step.
public sealed class TextRewriterException : Exception
{
    public const string NoModelMessage = "Für diesen Modus braucht Diktat einen Modell-Anbieter: einen API-Key oder einen lokalen Server wie Ollama in den Einstellungen.";

    public TextRewriterException() : base(NoModelMessage) { }
}

public sealed class TextRewriter : ITextRewriter
{
    private readonly AIProviderStore _store;
    private readonly AIClient _client;

    public TextRewriter(AIProviderStore store, AIClient client)
    {
        _store = store;
        _client = client;
    }

    /// Both engines use the provider on Windows.
    public bool CanRewrite(RewriteEngine engine) => _store.IsConfigured;

    public async Task<string> RewriteAsync(string text, string instruction, RewriteEngine engine, PromptLanguage? language = null,
        bool dictationRules = true, bool deterministic = false, CancellationToken cancellationToken = default)
    {
        var rulesLanguage = language ?? PromptLanguages.Detect(text);
        var fullInstruction = dictationRules ? instruction + "\n\n" + ModePrompts.SpokenTranscriptRulesFor(rulesLanguage) : instruction;
        var config = _store.Config;
        if (config is null || !_store.IsConfigured) throw new TextRewriterException();
        return await _client.CompleteAsync(fullInstruction, text, config, _store.Key(config.Provider), cancellationToken).ConfigureAwait(false);
    }

    public AIProviderConfig? ServerOnThisPC =>
        _store.Config is { IsOnThisPC: true } config && _store.IsConfigured ? config : null;

    public async Task<string> RewriteOnServerOnThisPCAsync(string text, string instruction, CancellationToken cancellationToken = default)
    {
        var config = ServerOnThisPC ?? throw new TextRewriterException();
        return await _client.CompleteAsync(instruction, text, config, _store.Key(config.Provider), cancellationToken).ConfigureAwait(false);
    }
}
