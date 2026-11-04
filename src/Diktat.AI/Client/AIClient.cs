using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Diktat.AI;

/// One instruction plus one text in, one text out. Talks directly to the provider, so the
/// text never passes through a Diktat server.
public sealed class AIClient : IDisposable
{
    /// Requests to hosted OpenAI-compatible providers.
    public static readonly TimeSpan HostedTimeout = TimeSpan.FromSeconds(60);
    /// A local model may first have to be loaded into memory; Anthropic gets the same.
    public static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(120);

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    /// <param name="handler">For tests: answers instead of the network.</param>
    public AIClient(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        // Timeouts are per request, see below.
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _ownsClient = true;
    }

    public AIClient(HttpClient client)
    {
        _http = client;
        _ownsClient = false;
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }

    /// <param name="apiKey">The provider's key; null for local servers without one.</param>
    public async Task<string> CompleteAsync(string instruction, string text, AIProviderConfig config, string? apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(apiKey)) apiKey = null;
        if (config.Provider.RequiresApiKey() && apiKey is null)
        {
            throw AIClientException.MissingKey(config.Provider);
        }
        return config.Provider.ApiStyle() switch
        {
            AIApiStyle.Anthropic => await AnthropicAsync(instruction, text, config, apiKey ?? "", cancellationToken).ConfigureAwait(false),
            _ => await OpenAICompatibleAsync(instruction, text, config, apiKey, cancellationToken).ConfigureAwait(false),
        };
    }

    /// Sends a tiny request to check address, key and model.
    public Task TestConnectionAsync(AIProviderConfig config, string? apiKey, CancellationToken cancellationToken = default) =>
        CompleteAsync("Antworte nur mit OK.", "Test", config, apiKey, cancellationToken);

    // MARK: OpenAI-compatible (OpenAI, Gemini, Mistral, OpenRouter, Ollama, LM Studio, ...)

    internal static HttpRequestMessage BuildOpenAICompatibleRequest(string instruction, string text, AIProviderConfig config, string? apiKey)
    {
        var url = Endpoint(config.BaseUrl, "/chat/completions");
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (apiKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        }
        // No temperature: several current models only accept their default.
        var body = new JsonObject
        {
            ["model"] = config.Model,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = SystemMessage(instruction, config) },
                new JsonObject { ["role"] = "user", ["content"] = text },
            },
        };
        if (SwitchesOffThinking(config))
        {
            body["reasoning_effort"] = "none";
        }
        request.Content = JsonContent(body);
        return request;
    }

    private async Task<string> OpenAICompatibleAsync(string instruction, string text, AIProviderConfig config, string? apiKey,
        CancellationToken cancellationToken)
    {
        using var request = BuildOpenAICompatibleRequest(instruction, text, config, apiKey);
        var json = await SendAsync(request, config.IsLocalServer ? LongTimeout : HostedTimeout, cancellationToken).ConfigureAwait(false);
        var content = Get(json, "choices") is JsonArray { Count: > 0 } choices
            ? StringValue(Get(Get(choices[0], "message"), "content"))
            : null;
        if (content is null || content.Trim().Length == 0) throw AIClientException.EmptyResponse();
        var answer = StrippingReasoning(content);
        if (answer.Length == 0) throw AIClientException.EmptyResponse();
        return answer;
    }

    /// Qwen3 thinks before it answers, which takes long and is not wanted for a rewrite. Local
    /// servers get reasoning_effort "none" (Ollama: 0.3 s instead of 5 s and more with Qwen3.5);
    /// other models never see it.
    public static bool SwitchesOffThinking(AIProviderConfig config) =>
        config.IsLocalServer && config.Model.ToLowerInvariant().Contains("qwen3");

    /// Qwen3.0 and 3.8 also take "/no_think" in the instruction; Qwen3.5 ignores it, which is
    /// why reasoning_effort is sent as well.
    public static string SystemMessage(string instruction, AIProviderConfig config) =>
        SwitchesOffThinking(config) ? instruction + "\n\n/no_think" : instruction;

    private static readonly Regex ThinkBlock = new("<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// Reasoning models (Qwen3, DeepSeek-R1) put their thoughts in think tags in front of the
    /// answer. Only the answer is text for the user.
    public static string StrippingReasoning(string content) => ThinkBlock.Replace(content, "").Trim();

    // MARK: Anthropic Messages API

    /// Models for which Anthropic retries on a suitable model server-side if a safety classifier declines.
    private static readonly string[] FallbackModels = ["claude-opus-5", "claude-fable-5", "claude-fable-5-1"];

    internal static HttpRequestMessage BuildAnthropicRequest(string instruction, string text, AIProviderConfig config, string apiKey)
    {
        var url = Endpoint(config.BaseUrl, "/messages");
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        var body = new JsonObject
        {
            ["model"] = config.Model,
            ["max_tokens"] = 16_000,
            ["system"] = instruction,
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = text } },
        };
        if (SupportsEffort(config.Model))
        {
            // Short rewrites need little reasoning; keeps answers fast and cheap.
            body["output_config"] = new JsonObject { ["effort"] = "low" };
        }
        if (FallbackModels.Contains(config.Model))
        {
            request.Headers.TryAddWithoutValidation("anthropic-beta", "server-side-fallback-2026-07-01");
            body["fallbacks"] = "default";
        }
        request.Content = JsonContent(body);
        return request;
    }

    private async Task<string> AnthropicAsync(string instruction, string text, AIProviderConfig config, string apiKey,
        CancellationToken cancellationToken)
    {
        using var request = BuildAnthropicRequest(instruction, text, config, apiKey);
        var json = await SendAsync(request, LongTimeout, cancellationToken).ConfigureAwait(false);
        if (StringValue(Get(json, "stop_reason")) == "refusal")
        {
            throw AIClientException.Refused();
        }
        var builder = new StringBuilder();
        if (Get(json, "content") is JsonArray blocks)
        {
            foreach (var block in blocks)
            {
                if (StringValue(Get(block, "type")) == "text" && StringValue(Get(block, "text")) is { } part) builder.Append(part);
            }
        }
        var answer = builder.ToString().Trim();
        if (answer.Length == 0) throw AIClientException.EmptyResponse();
        return answer;
    }

    /// effort exists on the current Opus, Sonnet and Fable models; Haiku 4.5 and older models
    /// reject it with a 400.
    public static bool SupportsEffort(string model) =>
        new[] { "claude-opus-5", "claude-fable", "claude-mythos", "claude-sonnet-5", "claude-opus-4-8",
                "claude-opus-4-7", "claude-opus-4-6", "claude-sonnet-4-6" }
            .Any(prefix => model.StartsWith(prefix, StringComparison.Ordinal));

    // MARK: Helpers

    private async Task<JsonNode?> SendAsync(HttpRequestMessage request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        HttpResponseMessage response;
        string body;
        try
        {
            response = await _http.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw AIClientException.TimedOut(e);
        }
        catch (HttpRequestException e)
        {
            throw AIClientException.Unreachable(e);
        }

        using (response)
        {
            JsonNode? json = null;
            try { json = JsonNode.Parse(body); }
            catch (Exception e) when (e is JsonException or ArgumentException) { }
            var status = (int)response.StatusCode;
            if (status is < 200 or >= 300)
            {
                throw AIClientException.Http(status, ErrorMessage(json));
            }
            return json as JsonObject;
        }
    }

    /// Both API styles nest the message under "error".
    private static string? ErrorMessage(JsonNode? json)
    {
        var error = Get(json, "error");
        if (error is JsonObject nested) return StringValue(nested["message"]);
        return StringValue(error);
    }

    private static JsonNode? Get(JsonNode? node, string key) => node is JsonObject obj ? obj[key] : null;

    private static string? StringValue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static Uri Endpoint(string baseUrl, string path)
    {
        var address = TrimmedBase(baseUrl) + path;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw AIClientException.InvalidAddress();
        }
        return uri;
    }

    private static string TrimmedBase(string baseUrl) => Internal.TextHelpers.Trim(baseUrl, c => c == '/' || char.IsWhiteSpace(c));

    private static StringContent JsonContent(JsonObject body)
    {
        var content = new StringContent(body.ToJsonString(), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }
}
