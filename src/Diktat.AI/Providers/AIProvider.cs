using System.Text.Json.Serialization;

namespace Diktat.AI;

/// A language model provider the user connects with their own API key. The JSON values
/// ("openAI", "openRouter", ...) are those of the macOS app.
public enum AIProvider
{
    OpenAI,
    Anthropic,
    Gemini,
    Mistral,
    OpenRouter,
    /// Any OpenAI-compatible endpoint: Azure, a company proxy, Ollama or LM Studio on the PC.
    Custom,
}

public enum AIApiStyle
{
    OpenAICompatible,
    Anthropic,
}

public static class AIProviderInfo
{
    public static readonly AIProvider[] All =
        [AIProvider.OpenAI, AIProvider.Anthropic, AIProvider.Gemini, AIProvider.Mistral, AIProvider.OpenRouter, AIProvider.Custom];

    /// The raw value of the macOS app, e.g. "openAI"; also part of the secret's name.
    public static string RawValue(this AIProvider provider) => provider switch
    {
        AIProvider.OpenAI => "openAI",
        AIProvider.Anthropic => "anthropic",
        AIProvider.Gemini => "gemini",
        AIProvider.Mistral => "mistral",
        AIProvider.OpenRouter => "openRouter",
        _ => "custom",
    };

    public static string DisplayName(this AIProvider provider) => provider switch
    {
        AIProvider.OpenAI => "OpenAI",
        AIProvider.Anthropic => "Anthropic (Claude)",
        AIProvider.Gemini => "Google Gemini",
        AIProvider.Mistral => "Mistral",
        AIProvider.OpenRouter => "OpenRouter",
        _ => "Eigene Adresse",
    };

    public static AIApiStyle ApiStyle(this AIProvider provider) =>
        provider == AIProvider.Anthropic ? AIApiStyle.Anthropic : AIApiStyle.OpenAICompatible;

    /// Base URL; for OpenAI-compatible providers "/chat/completions" is appended.
    public static string DefaultBaseUrl(this AIProvider provider) => provider switch
    {
        AIProvider.OpenAI => "https://api.openai.com/v1",
        AIProvider.Anthropic => "https://api.anthropic.com/v1",
        AIProvider.Gemini => "https://generativelanguage.googleapis.com/v1beta/openai",
        AIProvider.Mistral => "https://api.mistral.ai/v1",
        AIProvider.OpenRouter => "https://openrouter.ai/api/v1",
        _ => "http://localhost:11434/v1",
    };

    /// Suggested model, editable in the settings.
    public static string DefaultModel(this AIProvider provider) => provider switch
    {
        AIProvider.OpenAI => "gpt-4o-mini",
        AIProvider.Anthropic => "claude-opus-5",
        AIProvider.Gemini => "gemini-2.5-flash",
        AIProvider.Mistral => "mistral-small-latest",
        AIProvider.OpenRouter => "openai/gpt-4o-mini",
        _ => "llama3.2",
    };

    /// Local servers like Ollama run without a key.
    public static bool RequiresApiKey(this AIProvider provider) => provider != AIProvider.Custom;

    /// Where the user gets a key, or null.
    public static string? KeyHelpUrl(this AIProvider provider) => provider switch
    {
        AIProvider.OpenAI => "https://platform.openai.com/api-keys",
        AIProvider.Anthropic => "https://console.anthropic.com/settings/keys",
        AIProvider.Gemini => "https://aistudio.google.com/apikey",
        AIProvider.Mistral => "https://console.mistral.ai/api-keys",
        AIProvider.OpenRouter => "https://openrouter.ai/keys",
        _ => null,
    };
}

/// Which provider is connected, with which model and address. The key itself lives in the
/// secret store (DPAPI on Windows).
public sealed record AIProviderConfig
{
    [JsonPropertyName("provider")] public AIProvider Provider { get; init; }
    [JsonPropertyName("model")] public string Model { get; init; } = "";
    [JsonPropertyName("baseURL")] public string BaseUrl { get; init; } = "";

    public AIProviderConfig() { }

    public AIProviderConfig(AIProvider provider, string? model = null, string? baseUrl = null)
    {
        Provider = provider;
        Model = model ?? provider.DefaultModel();
        BaseUrl = baseUrl ?? provider.DefaultBaseUrl();
    }

    private string? Host
    {
        get
        {
            if (Provider != AIProvider.Custom) return null;
            return Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
                ? uri.DnsSafeHost.Trim('[', ']').ToLowerInvariant()
                : null;
        }
    }

    /// True when the address is a server on this PC itself (Ollama, LM Studio on localhost):
    /// the text does not leave the PC. Meetings may use only this.
    [JsonIgnore]
    public bool IsOnThisPC => Host is "localhost" or "127.0.0.1" or "::1";

    /// True when the address is a server on this PC or in the local network: the text does
    /// not leave the home network.
    [JsonIgnore]
    public bool IsLocalServer => Host is { } host && (IsOnThisPC || host.EndsWith(".local", StringComparison.Ordinal));
}
