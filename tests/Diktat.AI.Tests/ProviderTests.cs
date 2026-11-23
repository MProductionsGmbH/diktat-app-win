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

    [Theory]
    [InlineData("🟢 Model management service is running on http://127.0.0.1:5272/openai/status", "http://127.0.0.1:5272/v1")]
    [InlineData("Service is running on http://localhost:22334/", "http://localhost:22334/v1")]
    [InlineData("running on http://[::1]:5273/openai/status", "http://[::1]:5273/v1")]
    public void ReadsTheFoundryLocalAddress(string status, string expected) =>
        Assert.Equal(expected, LocalServerDiscovery.FoundryBaseUrl(status));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("🔴 Model management service is not running!")]
    [InlineData("running on http://192.168.1.20:5272/openai/status")]
    [InlineData("running on http://127.0.0.1:99999/")]
    public void FoundryLocalWithoutAnAddressOnThisPCIsNotThere(string? status) =>
        Assert.Null(LocalServerDiscovery.FoundryBaseUrl(status));

    [Fact]
    public async Task FindsFoundryLocalOnTheAddressItsToolNames()
    {
        var handler = new FakeHandler(request => request.RequestUri!.AbsoluteUri == "http://127.0.0.1:5272/v1/models"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"object":"list","data":[{"id":"Phi-4-mini-instruct-generic-cpu"},{"id":"qwen2.5-7b-instruct-generic-cpu"}]}""") }
            : throw new HttpRequestException("connection refused"));
        var discovery = new LocalServerDiscovery(handler, _ => Task.FromResult<string?>("🟢 Model management service is running on http://127.0.0.1:5272/openai/status"));

        var server = Assert.Single(await discovery.DiscoverAsync());
        Assert.Equal(LocalServerDiscovery.FoundryLocalName, server.Name);
        Assert.Equal("http://127.0.0.1:5272/v1", server.BaseUrl);
        Assert.Equal(["Phi-4-mini-instruct-generic-cpu", "qwen2.5-7b-instruct-generic-cpu"], server.Models);
        // A server on this PC: meetings may use it too.
        Assert.True(new AIProviderConfig(AIProvider.Custom, server.Models[0], server.BaseUrl).IsOnThisPC);
    }

    [Fact]
    public async Task AFailingFoundryToolDoesNotStopTheSearch()
    {
        var handler = new FakeHandler(request => request.RequestUri!.Port == 11434
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"models":[{"name":"llama3.2"}]}""") }
            : throw new HttpRequestException("connection refused"));
        var discovery = new LocalServerDiscovery(handler, _ => throw new InvalidOperationException("foundry not installed"));
        Assert.Equal("Ollama", Assert.Single(await discovery.DiscoverAsync()).Name);
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

/// The key of "Eigene Adresse" belongs to one server.
public sealed class CustomServerKeyTests
{
    private static AIProviderConfig Custom(string address) => new(AIProvider.Custom, baseUrl: address);

    [Fact]
    public void AnotherHostIsAnotherServer()
    {
        Assert.True(Custom("https://b.example.com/v1").MovesCustomServer(Custom("https://a.example.com/v1")));
        Assert.True(Custom("http://localhost:11434/v1").MovesCustomServer(Custom("https://proxy.example.com/v1")));
    }

    [Fact]
    public void AnotherPortOrSchemeIsAnotherServer()
    {
        Assert.True(Custom("http://localhost:1234/v1").MovesCustomServer(Custom("http://localhost:11434/v1")));
        Assert.True(Custom("http://proxy.example.com/v1").MovesCustomServer(Custom("https://proxy.example.com/v1")));
    }

    [Fact]
    public void AnEditedPathKeepsTheServer()
    {
        Assert.False(Custom("https://proxy.example.com/api/v1").MovesCustomServer(Custom("https://proxy.example.com/v1")));
        Assert.False(Custom("https://Proxy.Example.com/v1").MovesCustomServer(Custom("https://proxy.example.com/v1")));
        // The default port written out is the same server.
        Assert.False(Custom("https://proxy.example.com:443/v1").MovesCustomServer(Custom("https://proxy.example.com/v1")));
    }

    [Fact]
    public void NamedProvidersNeverMove()
    {
        var openAI = new AIProviderConfig(AIProvider.OpenAI);
        Assert.False(openAI.MovesCustomServer(Custom("https://proxy.example.com/v1")));
        Assert.False(Custom("https://proxy.example.com/v1").MovesCustomServer(openAI));
        Assert.False(Custom("https://proxy.example.com/v1").MovesCustomServer(null));
    }

    [Fact]
    public void TheStoreDeletesTheKeyWhenTheServerChanges()
    {
        using var directory = new TempDirectory();
        var secrets = new InMemorySecretStore();
        var store = new AIProviderStore(Path.Combine(directory.Path, "p.json"), secrets)
        {
            Config = Custom("https://proxy.example.com/v1"),
        };
        store.SaveKey(AIProvider.Custom, "sk-proxy-1234567890");
        store.SaveKey(AIProvider.OpenAI, "sk-openai-1234567890");

        store.Config = Custom("https://proxy.example.com/api/v1") with { Model = "other" };
        Assert.Equal("sk-proxy-1234567890", store.Key(AIProvider.Custom));

        store.Config = Custom("https://other.example.com/v1");
        Assert.Null(store.Key(AIProvider.Custom));
        // Only the key of "Eigene Adresse" goes.
        Assert.Equal("sk-openai-1234567890", store.Key(AIProvider.OpenAI));
    }

    [Fact]
    public void SwitchingToANamedProviderKeepsTheCustomKey()
    {
        using var directory = new TempDirectory();
        var store = new AIProviderStore(Path.Combine(directory.Path, "p.json"), new InMemorySecretStore())
        {
            Config = Custom("https://proxy.example.com/v1"),
        };
        store.SaveKey(AIProvider.Custom, "sk-proxy-1234567890");
        store.Config = new AIProviderConfig(AIProvider.OpenAI);
        Assert.Equal("sk-proxy-1234567890", store.Key(AIProvider.Custom));
    }

    [Theory]
    [InlineData(AIProvider.OpenAI)]
    [InlineData(null)]
    public void ADetourOverAnotherProviderDoesNotTakeTheKeyToAnotherServer(AIProvider? detour)
    {
        using var directory = new TempDirectory();
        var store = new AIProviderStore(Path.Combine(directory.Path, "p.json"), new InMemorySecretStore())
        {
            Config = Custom("https://a.example.com/v1"),
        };
        store.SaveKey(AIProvider.Custom, "sk-proxy-1234567890");
        store.Config = detour is { } provider ? new AIProviderConfig(provider) : null;

        // Back to "Eigene Adresse" with its default address, a server on this PC.
        store.Config = new AIProviderConfig(AIProvider.Custom);
        Assert.Null(store.Key(AIProvider.Custom));
        store.Config = Custom("https://a.example.com/v1");
        Assert.Null(store.Key(AIProvider.Custom));
    }

    [Fact]
    public void ADetourBackToTheSameServerKeepsTheKey()
    {
        using var directory = new TempDirectory();
        var store = new AIProviderStore(Path.Combine(directory.Path, "p.json"), new InMemorySecretStore())
        {
            Config = Custom("https://a.example.com/v1"),
        };
        store.SaveKey(AIProvider.Custom, "sk-proxy-1234567890");
        store.Config = new AIProviderConfig(AIProvider.OpenAI);
        store.Config = Custom("https://a.example.com/api/v1");
        Assert.Equal("sk-proxy-1234567890", store.Key(AIProvider.Custom));
    }

    [Theory]
    [InlineData("http://localhost:11434/v1")]
    [InlineData("http://localhost:1234/v1")]
    [InlineData("https://a.example.com/v1")]
    public void AKeyFromAnOlderVersionSavedBeforeADetourIsNeverSent(string address)
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "p.json");
        var secrets = new InMemorySecretStore();
        // An older version: the key of a.example.com, then OpenAI was picked.
        new AIProviderStore(path, secrets) { Config = new AIProviderConfig(AIProvider.OpenAI) };
        secrets.Set(AIProviderStore.SecretName(AIProvider.Custom), "sk-proxy-1234567890");

        var store = new AIProviderStore(path, secrets) { Config = Custom(address) };
        Assert.Null(store.Key(AIProvider.Custom));
    }

    [Fact]
    public void AKeyFromAnOlderVersionBelongsToTheAddressSetUpThen()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "p.json");
        var secrets = new InMemorySecretStore();
        new AIProviderStore(path, secrets) { Config = Custom("https://a.example.com/v1") };
        // Stored without the server, as before.
        secrets.Set(AIProviderStore.SecretName(AIProvider.Custom), "sk-proxy-1234567890");

        var store = new AIProviderStore(path, secrets);
        Assert.Equal("sk-proxy-1234567890", store.Key(AIProvider.Custom));
        store.Config = Custom("https://b.example.com/v1");
        Assert.Null(store.Key(AIProvider.Custom));
    }
}

public sealed class TextRewriterTests
{
    private static (TextRewriter Rewriter, FakeHandler Handler, AIProviderStore Store, TempDirectory Directory) Make(AIProviderConfig? config, string? key = null,
        ILocalTextModel? local = null)
    {
        var directory = new TempDirectory();
        var store = new AIProviderStore(Path.Combine(directory.Path, "p.json"), new InMemorySecretStore());
        if (config is not null) store.Config = config;
        if (key is not null && config is not null) store.SaveKey(config.Provider, key);
        var handler = FakeHandler.Json("""{"choices":[{"message":{"content":"Fertig."}}]}""");
        return (new TextRewriter(store, new AIClient(handler), local), handler, store, directory);
    }

    [Fact]
    public async Task WithoutAProviderItSaysWhatIsMissing()
    {
        var (rewriter, _, _, directory) = Make(null);
        using var _d = directory;
        Assert.False(rewriter.CanRewrite(RewriteEngine.Automatic));
        var error = await Assert.ThrowsAsync<TextRewriterException>(() => rewriter.RewriteAsync("Hallo", "Anweisung", RewriteEngine.Automatic));
        Assert.Equal("Für diesen Modus braucht Diktat ein Sprachmodell: das KI-Modell unter Einstellungen › Modelle, einen lokalen Server wie Ollama oder einen Modell-Anbieter mit API-Key.", error.Message);
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

    [Fact]
    public async Task TheBuiltInModelAloneIsEnough()
    {
        var local = new FakeLocalModel("Vom PC.");
        var (rewriter, handler, _, directory) = Make(null, local: local);
        using var _d = directory;
        Assert.True(rewriter.CanRewrite(RewriteEngine.Automatic));
        Assert.True(rewriter.CanRewrite(RewriteEngine.Provider));
        Assert.Same(local, rewriter.BuiltInModel);
        Assert.Equal("Vom PC.", await rewriter.RewriteAsync("Hallo", "Anweisung", RewriteEngine.Provider));
        Assert.StartsWith("Anweisung\n\n", local.Instructions[0]);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AutomaticPrefersTheBuiltInModelAndProviderTheProvider()
    {
        var local = new FakeLocalModel("Vom PC.");
        var (rewriter, handler, _, directory) = Make(new AIProviderConfig(AIProvider.OpenAI), "sk-test-123456789", local);
        using var _d = directory;
        Assert.Equal("Vom PC.", await rewriter.RewriteAsync("Hallo", "Anweisung", RewriteEngine.Automatic));
        Assert.Empty(handler.Requests);
        Assert.Equal("Fertig.", await rewriter.RewriteAsync("Hallo", "Anweisung", RewriteEngine.Provider));
        Assert.Single(handler.Requests);
        Assert.Single(local.Instructions);
    }

    [Fact]
    public async Task AFailingOrCutOffBuiltInModelHandsOverToTheProvider()
    {
        var (rewriter, handler, _, directory) = Make(new AIProviderConfig(AIProvider.OpenAI), "sk-test-123456789", new FakeLocalModel("Halb", cutOff: true));
        using var _d = directory;
        Assert.Equal("Fertig.", await rewriter.RewriteAsync("Hallo", "Anweisung", RewriteEngine.Automatic));
        Assert.Single(handler.Requests);

        var (alone, _, _, directory2) = Make(null, local: new FakeLocalModel("Halb", cutOff: true));
        using var _d2 = directory2;
        var error = await Assert.ThrowsAsync<TextRewriterException>(() => alone.RewriteAsync("Hallo", "Anweisung", RewriteEngine.Automatic));
        Assert.Equal("Die Antwort des Modells wurde zu lang.", error.Message);
    }

    [Fact]
    public void AnUninstalledBuiltInModelDoesNotCount()
    {
        var (rewriter, _, _, directory) = Make(null, local: new FakeLocalModel("x") { IsAvailable = false });
        using var _d = directory;
        Assert.Null(rewriter.BuiltInModel);
        Assert.False(rewriter.CanRewrite(RewriteEngine.Automatic));
    }

    /// "Alles auf diesem PC" in the main window is said only while no text leaves the PC.
    [Fact]
    public void TheProviderInUseIsNamedOnlyWhenTextLeavesThePC()
    {
        var (none, _, _, d1) = Make(null);
        using var _d1 = d1;
        Assert.Null(none.ProviderInUse(RewriteEngine.Automatic));

        var (hosted, _, _, d2) = Make(new AIProviderConfig(AIProvider.OpenAI), "sk-test-123456789");
        using var _d2 = d2;
        Assert.Equal("OpenAI", hosted.ProviderInUse(RewriteEngine.Automatic));

        // Without its key the provider is not used.
        var (noKey, _, _, d3) = Make(new AIProviderConfig(AIProvider.OpenAI));
        using var _d3 = d3;
        Assert.Null(noKey.ProviderInUse(RewriteEngine.Provider));

        var (onThisPC, _, _, d4) = Make(new AIProviderConfig(AIProvider.Custom, model: "llama3.2"));
        using var _d4 = d4;
        Assert.Null(onThisPC.ProviderInUse(RewriteEngine.Provider));

        var (inTheNetwork, _, _, d5) = Make(new AIProviderConfig(AIProvider.Custom, model: "llama3.2", baseUrl: "http://studio.local:11434/v1"));
        using var _d5 = d5;
        Assert.Equal("studio.local", inTheNetwork.ProviderInUse(RewriteEngine.Automatic));

        // The built-in model goes first with "Modell auf dem PC zuerst", the provider with "Immer Modell-Anbieter".
        var (both, _, _, d6) = Make(new AIProviderConfig(AIProvider.OpenAI), "sk-test-123456789", new FakeLocalModel("Vom PC."));
        using var _d6 = d6;
        Assert.Null(both.ProviderInUse(RewriteEngine.Automatic));
        Assert.Equal("OpenAI", both.ProviderInUse(RewriteEngine.Provider));
    }

    [Theory]
    [InlineData(10, 512)]
    [InlineData(2_000, 1_256)]
    [InlineData(100_000, 4_096)]
    public void TheAnswerMayBeAboutAsLongAsTheText(int length, int tokens) =>
        Assert.Equal(tokens, TextRewriter.MaxTokensFor(new string('a', length)));
}
