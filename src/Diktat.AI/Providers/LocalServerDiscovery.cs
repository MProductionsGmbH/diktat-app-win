using System.Text.Json;
using System.Text.RegularExpressions;

namespace Diktat.AI;

/// Finds a model server on this PC, so nobody has to type an address: Ollama answers on port
/// 11434 and LM Studio on 1234, on every PC that runs them with the default settings. Microsoft's
/// Foundry Local picks its port when it starts; its command line tool tells which one.
public sealed partial class LocalServerDiscovery
{
    public sealed record Server(string Name, string BaseUrl, IReadOnlyList<string> Models)
    {
        /// The OpenAI-compatible base URL identifies the server.
        public string Id => BaseUrl;
    }

    public const string OllamaBaseUrl = "http://localhost:11434/v1";
    public const string LMStudioBaseUrl = "http://localhost:1234/v1";
    public const string FoundryLocalName = "Foundry Local";

    /// A server that does not answer within this is not there.
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private readonly HttpMessageHandler? _handler;
    private readonly Func<CancellationToken, Task<string?>>? _foundryStatus;

    /// <param name="handler">For tests: answers instead of the network.</param>
    /// <param name="foundryStatus">What "foundry service status" prints, or null when Foundry
    /// Local is not installed. Without it Foundry Local is not looked for.</param>
    public LocalServerDiscovery(HttpMessageHandler? handler = null, Func<CancellationToken, Task<string?>>? foundryStatus = null)
    {
        _handler = handler;
        _foundryStatus = foundryStatus;
    }

    /// The OpenAI-compatible address in the output of "foundry service status", e.g. "Model
    /// management service is running on http://127.0.0.1:5272/openai/status". Only an address
    /// on this PC counts; null when the service does not run.
    public static string? FoundryBaseUrl(string? statusOutput)
    {
        if (string.IsNullOrEmpty(statusOutput)) return null;
        var match = LocalAddress().Match(statusOutput);
        if (!match.Success || !int.TryParse(match.Groups["port"].Value, out var port) || port is < 1 or > 65535) return null;
        return $"http://{match.Groups["host"].Value}:{port}/v1";
    }

    [GeneratedRegex(@"https?://(?<host>127\.0\.0\.1|localhost|\[::1\]):(?<port>\d{1,5})(?![\d])", RegexOptions.IgnoreCase)]
    private static partial Regex LocalAddress();

    /// Names from Ollama's /api/tags. Models that only create embeddings cannot write text.
    public static IReadOnlyList<string> OllamaModels(string json) => Names(json, "models", "name");

    /// Ids from an OpenAI-style /v1/models list, which LM Studio serves.
    public static IReadOnlyList<string> OpenAIModels(string json) => Names(json, "data", "id");

    private static IReadOnlyList<string> Names(string json, string listKey, string nameKey)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(listKey, out var list)
                || list.ValueKind != JsonValueKind.Array) return [];
            var names = new List<string>();
            foreach (var item in list.EnumerateArray())
            {
                // Like Swift's Decodable: one malformed entry makes the whole answer unreadable.
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(nameKey, out var name)
                    || name.ValueKind != JsonValueKind.String) return [];
                names.Add(name.GetString()!);
            }
            return WritingModels(names);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> WritingModels(IEnumerable<string> names) =>
        names.Where(name => !name.ToLowerInvariant().Contains("embed"))
            .OrderBy(name => name, NaturalStringComparer.Instance)
            .ToList();

    /// Asks all servers at once.
    public async Task<IReadOnlyList<Server>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        using var http = _handler is null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false);
        http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
        var ollama = FetchAsync(http, "http://localhost:11434/api/tags", cancellationToken);
        var lmStudio = FetchAsync(http, "http://localhost:1234/v1/models", cancellationToken);
        var foundry = FoundryAsync(http, cancellationToken);
        var servers = new List<Server>();
        if (await ollama.ConfigureAwait(false) is { } ollamaJson)
        {
            servers.Add(new Server("Ollama", OllamaBaseUrl, OllamaModels(ollamaJson)));
        }
        if (await lmStudio.ConfigureAwait(false) is { } lmStudioJson)
        {
            servers.Add(new Server("LM Studio", LMStudioBaseUrl, OpenAIModels(lmStudioJson)));
        }
        if (await foundry.ConfigureAwait(false) is { } foundryServer) servers.Add(foundryServer);
        return servers;
    }

    private async Task<Server?> FoundryAsync(HttpClient http, CancellationToken cancellationToken)
    {
        if (_foundryStatus is null) return null;
        string? status;
        try
        {
            status = await _foundryStatus(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        if (FoundryBaseUrl(status) is not { } baseUrl) return null;
        // Its own models list, in the OpenAI format.
        return await FetchAsync(http, baseUrl + "/models", cancellationToken).ConfigureAwait(false) is { } json
            ? new Server(FoundryLocalName, baseUrl, OpenAIModels(json))
            : null;
    }

    private static async Task<string?> FetchAsync(HttpClient http, string address, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var response = await http.GetAsync(address, timeout.Token).ConfigureAwait(false);
            if ((int)response.StatusCode != 200) return null;
            return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}

/// Finder-like order ("model2" before "model10"), case-insensitive, like Swift's
/// localizedStandardCompare.
internal sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var startX = i;
                var startY = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;
                var numberX = x[startX..i].TrimStart('0');
                var numberY = y[startY..j].TrimStart('0');
                if (numberX.Length != numberY.Length) return numberX.Length.CompareTo(numberY.Length);
                var byDigits = string.CompareOrdinal(numberX, numberY);
                if (byDigits != 0) return byDigits;
                continue;
            }
            var byChar = string.Compare(x[i].ToString(), y[j].ToString(), StringComparison.OrdinalIgnoreCase);
            if (byChar != 0) return byChar;
            i++;
            j++;
        }
        var byLength = (x.Length - i).CompareTo(y.Length - j);
        return byLength != 0 ? byLength : string.CompareOrdinal(x, y);
    }
}
