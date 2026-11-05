using Diktat.Core.Settings;
using Diktat.Core.Text;

namespace Diktat.AI;

/// Rewrites a transcript with an instruction. On the Mac the engine "automatic" prefers Apple
/// Intelligence on the device; on Windows it prefers the model the app runs itself (Gemma 4, when
/// downloaded) and falls back to the provider the user connected (a model server on this PC like
/// Ollama counts as a provider). "provider" asks the provider first. The engine stays in the
/// settings so a settings export from the Mac imports unchanged.
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

    /// The model the app runs itself when one is installed, else null. Text stays on the PC.
    ILocalTextModel? BuiltInModel { get; }

    /// The model server on this PC and nothing else, for features that promise the text stays
    /// on the PC whatever the engine setting. Throws <see cref="TextRewriterException"/> when no
    /// such server is set up.
    Task<string> RewriteOnServerOnThisPCAsync(string text, string instruction, CancellationToken cancellationToken = default);
}

/// No model can run the AI step.
public sealed class TextRewriterException : Exception
{
    public static string NoModelMessage => L.T("Für diesen Modus braucht Diktat ein Sprachmodell: das KI-Modell unter Einstellungen › Modelle, einen lokalen Server wie Ollama oder einen Modell-Anbieter mit API-Key.");

    public TextRewriterException() : base(NoModelMessage) { }

    public TextRewriterException(string message) : base(message) { }
}

public sealed class TextRewriter : ITextRewriter
{
    private readonly AIProviderStore _store;
    private readonly AIClient _client;
    private readonly ILocalTextModel? _local;

    public TextRewriter(AIProviderStore store, AIClient client, ILocalTextModel? local = null)
    {
        _store = store;
        _client = client;
        _local = local;
    }

    public ILocalTextModel? BuiltInModel => _local is { IsAvailable: true } local ? local : null;

    /// Either engine runs with the built-in model or the provider; only the order differs.
    public bool CanRewrite(RewriteEngine engine) => _store.IsConfigured || BuiltInModel is not null;

    /// Tokens the answer may take: about as long as the text, with room for emoji and markup.
    internal static int MaxTokensFor(string text) => Math.Clamp(text.Length / 2 + 256, 512, 4096);

    public async Task<string> RewriteAsync(string text, string instruction, RewriteEngine engine, PromptLanguage? language = null,
        bool dictationRules = true, bool deterministic = false, CancellationToken cancellationToken = default)
    {
        var rulesLanguage = language ?? PromptLanguages.Detect(text);
        var fullInstruction = dictationRules ? instruction + "\n\n" + ModePrompts.SpokenTranscriptRulesFor(rulesLanguage) : instruction;
        var config = _store.IsConfigured ? _store.Config : null;
        var local = BuiltInModel;
        if (local is not null && (engine == RewriteEngine.Automatic || config is null))
        {
            try
            {
                var answer = await local.CompleteAsync(fullInstruction, text, MaxTokensFor(text), cancellationToken).ConfigureAwait(false);
                // A cut-off answer would lose the end of the dictation.
                if (answer.CutOff) throw new TextRewriterException(L.T("Die Antwort des Modells wurde zu lang."));
                return answer.Text;
            }
            catch (Exception) when (config is not null && !cancellationToken.IsCancellationRequested)
            {
                // The provider takes over.
            }
        }
        if (config is null) throw new TextRewriterException();
        try
        {
            return await _client.CompleteAsync(fullInstruction, text, config, _store.Key(config.Provider), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (local is not null && engine == RewriteEngine.Provider && !cancellationToken.IsCancellationRequested)
        {
            // "Immer Modell-Anbieter" and the provider failed: the model on this PC takes over.
            var answer = await local.CompleteAsync(fullInstruction, text, MaxTokensFor(text), cancellationToken).ConfigureAwait(false);
            if (answer.CutOff) throw new TextRewriterException(L.T("Die Antwort des Modells wurde zu lang."));
            return answer.Text;
        }
    }

    public AIProviderConfig? ServerOnThisPC =>
        _store.Config is { IsOnThisPC: true } config && _store.IsConfigured ? config : null;

    public async Task<string> RewriteOnServerOnThisPCAsync(string text, string instruction, CancellationToken cancellationToken = default)
    {
        var config = ServerOnThisPC ?? throw new TextRewriterException();
        return await _client.CompleteAsync(instruction, text, config, _store.Key(config.Provider), cancellationToken).ConfigureAwait(false);
    }
}
