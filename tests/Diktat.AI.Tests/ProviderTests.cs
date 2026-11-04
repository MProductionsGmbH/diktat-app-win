using System.Net;
using Diktat.Core.Settings;
using Diktat.Core.Support;

namespace Diktat.AI.Tests;

public sealed class LocalProviderTests
{
    [Fact]
    public void OllamaAndLMStudioAddressesAreLocalServers()
    {
        Assert.True(new AIProviderConfig(AIProvider.Custom).IsLocalServer);
        Assert.True(new AIProviderConfig(AIProvider.Custom, baseUrl: "http://127.0.0.1:1234/v1").IsLocalServer);
        Assert.True(new AIProviderConfig(AIProvider.Custom, baseUrl: "http://studio.local:11434/v1").IsLocalServer);
    }

    [Fact]
    public void HostedProvidersAreNotLocalServers()
    {
        Assert.False(new AIProviderConfig(AIProvider.OpenAI).IsLocalServer);
        Assert.False(new AIProviderConfig(AIProvider.Custom, baseUrl: "https://proxy.example.com/v1").IsLocalServer);
    }

    [Fact]
    public void StripsThinkingBlocksOfReasoningModels()
    {
        Assert.Equal("Wir treffen uns am Donnerstag.",
            AIClient.StrippingReasoning("<think>\nErst prüfen.\nDann kürzen.\n</think>\n\nWir treffen uns am Donnerstag."));
        Assert.Equal("Kein Denkblock hier.", AIClient.StrippingReasoning("Kein Denkblock hier."));
        Assert.Equal("", AIClient.StrippingReasoning("<think>nur Gedanken</think>"));
    }

    [Fact]
    public void ThinkingIsSwitchedOffOnlyForQwen3OnALocalServer()
    {
        Assert.True(AIClient.SwitchesOffThinking(new AIProviderConfig(AIProvider.Custom, model: "qwen3.5:9b")));
        Assert.False(AIClient.SwitchesOffThinking(new AIProviderConfig(AIProvider.Custom, model: "llama3.2")));
        Assert.False(AIClient.SwitchesOffThinking(new AIProviderConfig(AIProvider.OpenAI, model: "qwen3:8b")));
    }

    [Fact]
    public void NoThinkSwitchOnlyForQwen3OnALocalServer()
    {
        var qwen = new AIProviderConfig(AIProvider.Custom, model: "qwen3:8b");
        Assert.EndsWith("/no_think", AIClient.SystemMessage("Anweisung", qwen));
        Assert.Equal("Anweisung", AIClient.SystemMessage("Anweisung", new AIProviderConfig(AIProvider.Custom, model: "llama3.2")));
        var hosted = new AIProviderConfig(AIProvider.Custom, model: "qwen3:8b", baseUrl: "https://proxy.example.com/v1");
        Assert.Equal("Anweisung", AIClient.SystemMessage("Anweisung", hosted));
    }

    [Fact]
    public void OnlyLoopbackAddressesAreOnThisPC()
    {
        Assert.True(new AIProviderConfig(AIProvider.Custom).IsOnThisPC);
        Assert.True(new AIProviderConfig(AIProvider.Custom, baseUrl: "http://127.0.0.1:1234/v1").IsOnThisPC);
        Assert.True(new AIProviderConfig(AIProvider.Custom, baseUrl: "http://[::1]:11434/v1").IsOnThisPC);
        // Another device in the home network is a local server, but not this PC: no meetings.
        var studio = new AIProviderConfig(AIProvider.Custom, baseUrl: "http://studio.local:11434/v1");
        Assert.True(studio.IsLocalServer);
        Assert.False(studio.IsOnThisPC);
        Assert.False(new AIProviderConfig(AIProvider.Custom, baseUrl: "https://proxy.example.com/v1").IsOnThisPC);
        Assert.False(new AIProviderConfig(AIProvider.OpenAI, baseUrl: "http://localhost:11434/v1").IsOnThisPC);
    }
}

public sealed class LocalServerDiscoveryTests
{
    [Fact]
    public void ReadsOllamaModelsSortedAndWithoutEmbeddingModels()
    {
        const string json = """{"models":[{"name":"qwen3.5:9b"},{"name":"nomic-embed-text:latest"},{"name":"gemma4:12b"}]}""";
        Assert.Equal(["gemma4:12b", "qwen3.5:9b"], LocalServerDiscovery.OllamaModels(json));
    }

    [Fact]
    public void ReadsLMStudioModels()
    {
        const string json = """{"object":"list","data":[{"id":"qwen3-8b"},{"id":"text-embedding-nomic"}]}""";
        Assert.Equal(["qwen3-8b"], LocalServerDiscovery.OpenAIModels(json));
    }

    [Fact]
    public void UnreadableAnswerGivesNoModels()
    {
        Assert.Empty(LocalServerDiscovery.OllamaModels("<html>"));
        Assert.Empty(LocalServerDiscovery.OpenAIModels("{}"));
    }

    [Fact]
    public void SortsNumbersByValue() =>
        Assert.Equal(["model2", "Model10"], LocalServerDiscovery.OllamaModels("""{"models":[{"name":"Model10"},{"name":"model2"}]}"""));

    [Fact]
    public async Task FindsTheServersThatAnswer()
    {
        var handler = new FakeHandler(request => request.RequestUri!.Port == 11434
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"models":[{"name":"llama3.2"}]}""") }
            : throw new HttpRequestException("connection refused"));
        var servers = await new LocalServerDiscovery(handler).DiscoverAsync();
        var server = Assert.Single(servers);
        Assert.Equal("Ollama", server.Name);
        Assert.Equal(LocalServerDiscovery.OllamaBaseUrl, server.BaseUrl);
        Assert.Equal(["llama3.2"], server.Models);
    }
}

public sealed class AIProviderStoreTests
{
    [Fact]
    public void PersistsTheConfigAsJsonAndTheKeyInTheSecretStore()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "ai-provider.json");
        var secrets = new InMemorySecretStore();
        var store = new AIProviderStore(path, secrets);
        Assert.Null(store.Config);
        Assert.False(store.IsConfigured);

        store.Config = new AIProviderConfig(AIProvider.OpenRouter);
        store.SaveKey(AIProvider.OpenRouter, "  sk-or-1234567890abcd  ");
        Assert.True(store.IsConfigured);
        Assert.Equal("sk-or-1234567890abcd", secrets.Get("ai-provider/openRouter"));
        Assert.DoesNotContain("sk-or", File.ReadAllText(path));
        Assert.Contains("\"openRouter\"", File.ReadAllText(path));
        Assert.Contains("\"baseURL\"", File.ReadAllText(path));

        var reloaded = new AIProviderStore(path, secrets);
        Assert.Equal(new AIProviderConfig(AIProvider.OpenRouter), reloaded.Config);
        Assert.Equal("sk-…abcd", reloaded.MaskedKey(AIProvider.OpenRouter));

        store.SaveKey(AIProvider.OpenRouter, " ");
        Assert.False(store.IsConfigured);
        store.Config = null;
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ALocalServerNeedsNoKey()
    {
        using var directory = new TempDirectory();
        var store = new AIProviderStore(Path.Combine(directory.Path, "p.json"), new InMemorySecretStore())
        {
            Config = new AIProviderConfig(AIProvider.Custom, model: "qwen3.5:9b"),
        };
        Assert.True(store.IsConfigured);
    }

    [Fact]
    public void ReadsTheMacFormat()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "p.json");
        File.WriteAllText(path, """{"provider":"anthropic","model":"claude-opus-5","baseURL":"https://api.anthropic.com/v1"}""");
        Assert.Equal(new AIProviderConfig(AIProvider.Anthropic), new AIProviderStore(path, new InMemorySecretStore()).Config);
    }

    [Fact]
    public void ShortKeysAreFullyMasked()
    {
        Assert.Equal("••••••••", AIProviderStore.Mask("abc"));
        Assert.Null(AIProviderStore.Mask(null));
    }
}

public sealed class TextRewriterTests
{
    private static (TextRewriter Rewriter, FakeHandler Handler, AIProviderStore Store, TempDirectory Directory) Make(AIProviderConfig? config, string? key = null)
    {
        var directory = new TempDirectory();
        var store = new AIProviderStore(Path.Combine(directory.Path, "p.json"), new InMemorySecretStore());
        if (config is not null) store.Config = config;
        if (key is not null && config is not null) store.SaveKey(config.Provider, key);
        var handler = FakeHandler.Json("""{"choices":[{"message":{"content":"Fertig."}}]}""");
        return (new TextRewriter(store, new AIClient(handler)), handler, store, directory);
    }

    [Fact]
    public async Task WithoutAProviderItSaysWhatIsMissing()
    {
        var (rewriter, _, _, directory) = Make(null);
        using var _d = directory;
        Assert.False(rewriter.CanRewrite(RewriteEngine.Automatic));
        var error = await Assert.ThrowsAsync<TextRewriterException>(() => rewriter.RewriteAsync("Hallo", "Anweisung", RewriteEngine.Automatic));
        Assert.Equal("Für diesen Modus braucht Diktat einen Modell-Anbieter: einen API-Key oder einen lokalen Server wie Ollama in den Einstellungen.", error.Message);
    }

    [Fact]
    public async Task BothEnginesUseTheProviderAndAddTheDictationRules()
    {
        var (rewriter, handler, _, directory) = Make(new AIProviderConfig(AIProvider.OpenAI), "sk-test-123456789");
        using var _d = directory;
        Assert.True(rewriter.CanRewrite(RewriteEngine.Automatic));
        Assert.Equal("Fertig.", await rewriter.RewriteAsync("Ich wollte sagen, dass wir uns morgen treffen.", "Anweisung", RewriteEngine.Automatic));
        var system = handler.Requests[0].Json["messages"]![0]!["content"]!.GetValue<string>();
        Assert.StartsWith("Anweisung\n\n" + ModePrompts.SpokenTranscriptRules, system);
        Assert.Equal("Bearer sk-test-123456789", handler.Requests[0].Headers["authorization"]);

        await rewriter.RewriteAsync("Text", "Nur das", RewriteEngine.Provider, dictationRules: false);
        Assert.Equal("Nur das", handler.Requests[1].Json["messages"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task MeetingsUseOnlyAServerOnThisPC()
    {
        var (hosted, _, _, directory) = Make(new AIProviderConfig(AIProvider.OpenAI), "sk-test-123456789");
        using var _d = directory;
        Assert.Null(hosted.ServerOnThisPC);
        await Assert.ThrowsAsync<TextRewriterException>(() => hosted.RewriteOnServerOnThisPCAsync("Text", "Anweisung"));

        var (network, _, _, directory2) = Make(new AIProviderConfig(AIProvider.Custom, baseUrl: "http://studio.local:11434/v1"));
        using var _d2 = directory2;
        Assert.Null(network.ServerOnThisPC);

        var (local, handler, _, directory3) = Make(new AIProviderConfig(AIProvider.Custom));
        using var _d3 = directory3;
        Assert.NotNull(local.ServerOnThisPC);
        Assert.Equal("Fertig.", await local.RewriteOnServerOnThisPCAsync("Text", "Anweisung"));
        Assert.Equal("Anweisung", handler.Requests[0].Json["messages"]![0]!["content"]!.GetValue<string>());
        Assert.False(handler.Requests[0].Headers.ContainsKey("authorization"));
    }
}
