using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Diktat.Core.Settings;
using Diktat.Core.Text;

namespace Diktat.AI.Tests;

/// Answers HTTP requests without the network and remembers what was sent.
internal sealed class FakeHandler : HttpMessageHandler
{
    public sealed record Sent(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, string Body)
    {
        public JsonNode Json => JsonNode.Parse(Body)!;
    }

    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
    public List<Sent> Requests { get; } = new();

    public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    public static FakeHandler Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(_ => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers) headers[header.Key.ToLowerInvariant()] = string.Join(",", header.Value);
        }
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new Sent(request.Method, request.RequestUri!, headers, body));
        return _respond(request);
    }
}

/// A rewriter that answers with a function and remembers what it got.
internal sealed class FakeRewriter : ITextRewriter
{
    public sealed record Call(string Text, string Instruction, RewriteEngine Engine, PromptLanguage? Language, bool DictationRules);

    private readonly Func<string, string, string> _answer;
    public List<Call> Calls { get; } = new();
    public bool Configured { get; set; } = true;
    public AIProviderConfig? ServerOnThisPC { get; set; }
    public ILocalTextModel? BuiltInModel { get; set; }

    public FakeRewriter(Func<string, string, string>? answer = null)
    {
        _answer = answer ?? ((text, _) => text);
    }

    public bool CanRewrite(RewriteEngine engine) => Configured;

    public Task<string> RewriteAsync(string text, string instruction, RewriteEngine engine, PromptLanguage? language = null,
        bool dictationRules = true, bool deterministic = false, CancellationToken cancellationToken = default)
    {
        Calls.Add(new Call(text, instruction, engine, language, dictationRules));
        if (!Configured) throw new TextRewriterException();
        return Task.FromResult(_answer(text, instruction));
    }

    public Task<string> RewriteOnServerOnThisPCAsync(string text, string instruction, CancellationToken cancellationToken = default)
    {
        if (ServerOnThisPC is null) throw new TextRewriterException();
        Calls.Add(new Call(text, instruction, RewriteEngine.Provider, null, false));
        return Task.FromResult(_answer(text, instruction));
    }
}

/// A built-in model that answers with a fixed text.
internal sealed class FakeLocalModel(string answer, bool cutOff = false) : ILocalTextModel
{
    public bool IsAvailable { get; set; } = true;
    public string Name => "Gemma 4 E2B";
    public List<string> Instructions { get; } = new();

    public Task<LocalTextAnswer> CompleteAsync(string instruction, string text, int maxTokens, CancellationToken cancellationToken = default)
    {
        Instructions.Add(instruction);
        return Task.FromResult(new LocalTextAnswer(answer, cutOff));
    }
}

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "diktat-ai-tests-" + Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}
