using System.Net;
using System.Text.Json.Nodes;

namespace Diktat.AI.Tests;

public sealed class AIClientTests
{
    private const string OpenAIAnswer = """{"choices":[{"message":{"role":"assistant","content":"  Wir treffen uns am Donnerstag.  "}}]}""";
    private const string AnthropicAnswer = """{"content":[{"type":"thinking","thinking":"..."},{"type":"text","text":"Hallo "},{"type":"text","text":"Welt"}],"stop_reason":"end_turn"}""";

    [Fact]
    public async Task OpenAICompatibleRequestHasModelMessagesAndBearerKey()
    {
        var handler = FakeHandler.Json(OpenAIAnswer);
        using var client = new AIClient(handler);
        var config = new AIProviderConfig(AIProvider.Mistral, baseUrl: "https://api.mistral.ai/v1/ ");

        var answer = await client.CompleteAsync("Anweisung", "Text", config, "key-123");

        Assert.Equal("Wir treffen uns am Donnerstag.", answer);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://api.mistral.ai/v1/chat/completions", sent.Uri.ToString());
        Assert.Equal("Bearer key-123", sent.Headers["authorization"]);
        Assert.StartsWith("application/json", sent.Headers["content-type"]);
        var body = sent.Json.AsObject();
        Assert.Equal("mistral-small-latest", body["model"]!.GetValue<string>());
        Assert.Equal("system", body["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("Anweisung", body["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal("user", body["messages"]![1]!["role"]!.GetValue<string>());
        Assert.Equal("Text", body["messages"]![1]!["content"]!.GetValue<string>());
        // No temperature: several current models only accept their default.
        Assert.False(body.ContainsKey("temperature"));
        Assert.False(body.ContainsKey("reasoning_effort"));
    }

    [Fact]
    public async Task Qwen3OnALocalServerRunsWithoutThinkingAndWithoutKey()
    {
        var handler = FakeHandler.Json("""{"choices":[{"message":{"content":"<think>hm</think>\nOK"}}]}""");
        using var client = new AIClient(handler);
        var answer = await client.CompleteAsync("Anweisung", "Text", new AIProviderConfig(AIProvider.Custom, model: "qwen3.5:9b"), null);

        Assert.Equal("OK", answer);
        var sent = handler.Requests[0];
        Assert.Equal("http://localhost:11434/v1/chat/completions", sent.Uri.ToString());
        Assert.False(sent.Headers.ContainsKey("authorization"));
        Assert.Equal("none", sent.Json["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("Anweisung\n\n/no_think", sent.Json["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnthropicRequestUsesMessagesApiEffortAndFallbacks()
    {
        var handler = FakeHandler.Json(AnthropicAnswer);
        using var client = new AIClient(handler);
        var answer = await client.CompleteAsync("Anweisung", "Text", new AIProviderConfig(AIProvider.Anthropic), "sk-ant-1");

        Assert.Equal("Hallo Welt", answer);
        var sent = handler.Requests[0];
        Assert.Equal("https://api.anthropic.com/v1/messages", sent.Uri.ToString());
        Assert.Equal("sk-ant-1", sent.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", sent.Headers["anthropic-version"]);
        Assert.Equal("server-side-fallback-2026-07-01", sent.Headers["anthropic-beta"]);
        Assert.False(sent.Headers.ContainsKey("authorization"));
        var body = sent.Json.AsObject();
        Assert.Equal("claude-opus-5", body["model"]!.GetValue<string>());
        Assert.Equal(16_000, body["max_tokens"]!.GetValue<int>());
        Assert.Equal("Anweisung", body["system"]!.GetValue<string>());
        Assert.Equal("user", body["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("Text", body["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal("low", body["output_config"]!["effort"]!.GetValue<string>());
        Assert.Equal("default", body["fallbacks"]!.GetValue<string>());
    }

    [Fact]
    public async Task OlderAnthropicModelsGetNeitherEffortNorFallbacks()
    {
        var handler = FakeHandler.Json(AnthropicAnswer);
        using var client = new AIClient(handler);
        await client.CompleteAsync("A", "T", new AIProviderConfig(AIProvider.Anthropic, model: "claude-haiku-4-5"), "k");
        var sent = handler.Requests[0];
        Assert.False(sent.Json.AsObject().ContainsKey("output_config"));
        Assert.False(sent.Json.AsObject().ContainsKey("fallbacks"));
        Assert.False(sent.Headers.ContainsKey("anthropic-beta"));

        await client.CompleteAsync("A", "T", new AIProviderConfig(AIProvider.Anthropic, model: "claude-sonnet-4-6"), "k");
        Assert.Equal("low", handler.Requests[1].Json["output_config"]!["effort"]!.GetValue<string>());
        Assert.False(handler.Requests[1].Json.AsObject().ContainsKey("fallbacks"));
    }

    [Fact]
    public async Task AnthropicRefusalIsReported()
    {
        using var client = new AIClient(FakeHandler.Json("""{"content":[],"stop_reason":"refusal"}"""));
        var error = await Assert.ThrowsAsync<AIClientException>(() =>
            client.CompleteAsync("A", "T", new AIProviderConfig(AIProvider.Anthropic), "k"));
        Assert.Equal(AIClientErrorKind.Refused, error.Kind);
        Assert.Equal("Der Modell-Anbieter hat die Anfrage abgelehnt.", error.Message);
    }

    [Fact]
    public async Task MissingKeyIsReportedBeforeAnyRequest()
    {
        var handler = FakeHandler.Json(OpenAIAnswer);
        using var client = new AIClient(handler);
        var error = await Assert.ThrowsAsync<AIClientException>(() => client.CompleteAsync("A", "T", new AIProviderConfig(AIProvider.Gemini), null));
        Assert.Equal("Für Google Gemini fehlt der API-Key.", error.Message);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(401, """{"error":{"message":"bad key"}}""", "Der API-Key wurde abgelehnt. Bitte in den Einstellungen prüfen.")]
    [InlineData(403, "", "Der API-Key wurde abgelehnt. Bitte in den Einstellungen prüfen.")]
    [InlineData(429, "", "Zu viele Anfragen oder das Guthaben beim Anbieter ist aufgebraucht.")]
    [InlineData(400, """{"error":{"message":"model not found"}}""", "Fehler vom Modell-Anbieter (400): model not found")]
    [InlineData(404, """{"error":"model 'x' not found"}""", "Fehler vom Modell-Anbieter (404): model 'x' not found")]
    [InlineData(500, "<html>", "Fehler vom Modell-Anbieter (500).")]
    public async Task HttpErrorsBecomeGermanMessages(int status, string body, string message)
    {
        using var client = new AIClient(FakeHandler.Json(body, (HttpStatusCode)status));
        var error = await Assert.ThrowsAsync<AIClientException>(() => client.CompleteAsync("A", "T", new AIProviderConfig(AIProvider.OpenAI), "k"));
        Assert.Equal(AIClientErrorKind.Http, error.Kind);
        Assert.Equal(status, error.Status);
        Assert.Equal(message, error.Message);
    }

    [Theory]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{"choices":[{"message":{"content":"   "}}]}""")]
    [InlineData("""{"choices":[{"message":{"content":"<think>nur Gedanken</think>"}}]}""")]
    [InlineData("not json")]
    public async Task EmptyAnswersAreReported(string body)
    {
        using var client = new AIClient(FakeHandler.Json(body));
        var error = await Assert.ThrowsAsync<AIClientException>(() => client.CompleteAsync("A", "T", new AIProviderConfig(AIProvider.OpenAI), "k"));
        Assert.Equal("Keine Antwort vom Modell-Anbieter erhalten. Bitte nochmal versuchen.", error.Message);
    }

    [Fact]
    public async Task InvalidAddressIsReported()
    {
        using var client = new AIClient(FakeHandler.Json(OpenAIAnswer));
        var error = await Assert.ThrowsAsync<AIClientException>(() =>
            client.CompleteAsync("A", "T", new AIProviderConfig(AIProvider.Custom, baseUrl: "kein server"), null));
        Assert.Equal("Die Adresse des Modell-Anbieters ist ungültig.", error.Message);
    }

    [Fact]
    public async Task UnreachableServerIsReported()
    {
        using var client = new AIClient(new FakeHandler(_ => throw new HttpRequestException("refused")));
        var error = await Assert.ThrowsAsync<AIClientException>(() =>
            client.CompleteAsync("A", "T", new AIProviderConfig(AIProvider.Custom), null));
        Assert.Equal(AIClientErrorKind.Unreachable, error.Kind);
    }

    [Fact]
    public async Task ConnectionTestAsksForOK()
    {
        var handler = FakeHandler.Json(OpenAIAnswer);
        using var client = new AIClient(handler);
        await client.TestConnectionAsync(new AIProviderConfig(AIProvider.OpenAI), "k");
        Assert.Equal("Antworte nur mit OK.", handler.Requests[0].Json["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal("Test", handler.Requests[0].Json["messages"]![1]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void ProviderDefaultsMatchTheMac()
    {
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai", AIProvider.Gemini.DefaultBaseUrl());
        Assert.Equal("claude-opus-5", AIProvider.Anthropic.DefaultModel());
        Assert.Equal("Eigene Adresse", AIProvider.Custom.DisplayName());
        Assert.False(AIProvider.Custom.RequiresApiKey());
        Assert.Equal("ai-provider/openAI", AIProviderStore.SecretName(AIProvider.OpenAI));
    }
}
