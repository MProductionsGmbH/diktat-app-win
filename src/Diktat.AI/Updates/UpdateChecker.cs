using System.Text.Json;
using System.Text.RegularExpressions;

namespace Diktat.AI.Updates;

/// The newest release on GitHub.
/// <param name="DownloadUrl">The installer (Diktat-Setup-*.exe) or the portable zip
/// (Diktat-*-win-x64.zip); the release page when neither is attached.</param>
public sealed record UpdateInfo(string Version, bool IsNewer, string DownloadUrl, string ReleasePageUrl, string? ReleaseNotes);

/// The update check failed. <see cref="Exception.Message"/> is German text for the user.
public sealed class UpdateCheckException : Exception
{
    public UpdateCheckException(string message, Exception? inner = null) : base(message, inner) { }
}

/// Looks for a newer Diktat on GitHub. There is no automatic install: the app opens the
/// download page and the person runs the installer.
public sealed class UpdateChecker
{
    public const string LatestReleaseUrl = "https://api.github.com/repos/MProductionsGmbH/diktat-app-win/releases/latest";
    public const string ReleasesPageUrl = "https://github.com/MProductionsGmbH/diktat-app-win/releases";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private const string FailedMessage = "Die Suche nach Updates ist fehlgeschlagen. Bitte später nochmal versuchen.";

    private static readonly Regex SetupAsset = new(@"^Diktat-Setup-.+\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ZipAsset = new(@"^Diktat-.+-win-x64\.zip$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly HttpMessageHandler? _handler;

    /// <param name="handler">For tests: answers instead of the network.</param>
    public UpdateChecker(HttpMessageHandler? handler = null)
    {
        _handler = handler;
    }

    /// The latest release, or null when the project has none published yet.
    /// <param name="currentVersion">The running version, e.g. "0.1.0".</param>
    public async Task<UpdateInfo?> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        using var http = _handler is null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false);
        http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        // GitHub rejects requests without a User-Agent.
        request.Headers.TryAddWithoutValidation("User-Agent", $"Diktat-Windows/{currentVersion}");
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        string body;
        int status;
        try
        {
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            status = (int)response.StatusCode;
            body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateCheckException(FailedMessage, e);
        }
        catch (HttpRequestException e)
        {
            throw new UpdateCheckException(FailedMessage, e);
        }

        if (status == 404) return null;
        if (status is < 200 or >= 300) throw new UpdateCheckException(FailedMessage);
        try
        {
            return Parse(body, currentVersion);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new UpdateCheckException(FailedMessage, e);
        }
    }

    /// Reads GitHub's release JSON.
    public static UpdateInfo Parse(string json, string currentVersion)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var page = root.TryGetProperty("html_url", out var html) && html.ValueKind == JsonValueKind.String
            ? html.GetString()! : ReleasesPageUrl;
        var notes = root.TryGetProperty("body", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;

        string? setup = null, zip = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (url is null) continue;
                if (setup is null && SetupAsset.IsMatch(name)) setup = url;
                else if (zip is null && ZipAsset.IsMatch(name)) zip = url;
            }
        }
        var version = NormalizedVersion(tag);
        return new UpdateInfo(version, IsNewer(version, currentVersion), setup ?? zip ?? page, page, notes);
    }

    /// "v1.2.0" -> "1.2.0".
    public static string NormalizedVersion(string tag)
    {
        var trimmed = tag.Trim();
        return trimmed.StartsWith('v') || trimmed.StartsWith('V') ? trimmed[1..] : trimmed;
    }

    /// Whether <paramref name="candidate"/> is a later semantic version than <paramref name="current"/>.
    public static bool IsNewer(string candidate, string current) => CompareVersions(candidate, current) > 0;

    /// Semantic versions, "v" prefix allowed: numbers part by part (missing parts are 0), then a
    /// release above its pre-releases ("1.2.0" > "1.2.0-beta.2" > "1.2.0-beta.1"). Build metadata
    /// ("+abc") is ignored.
    public static int CompareVersions(string a, string b)
    {
        var (coreA, preA) = Split(NormalizedVersion(a));
        var (coreB, preB) = Split(NormalizedVersion(b));
        for (var i = 0; i < Math.Max(coreA.Length, coreB.Length); i++)
        {
            var partA = i < coreA.Length ? coreA[i] : 0;
            var partB = i < coreB.Length ? coreB[i] : 0;
            if (partA != partB) return partA.CompareTo(partB);
        }
        if (preA is null) return preB is null ? 0 : 1;
        if (preB is null) return -1;
        var idsA = preA.Split('.');
        var idsB = preB.Split('.');
        for (var i = 0; i < Math.Min(idsA.Length, idsB.Length); i++)
        {
            var numericA = long.TryParse(idsA[i], out var numberA);
            var numericB = long.TryParse(idsB[i], out var numberB);
            var order = (numericA, numericB) switch
            {
                (true, true) => numberA.CompareTo(numberB),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(idsA[i], idsB[i]),
            };
            if (order != 0) return Math.Sign(order);
        }
        return idsA.Length.CompareTo(idsB.Length);
    }

    private static (long[] Core, string? PreRelease) Split(string version)
    {
        var withoutBuild = version.Split('+')[0];
        var dash = withoutBuild.IndexOf('-');
        var core = dash >= 0 ? withoutBuild[..dash] : withoutBuild;
        var pre = dash >= 0 ? withoutBuild[(dash + 1)..] : null;
        var parts = core.Split('.').Select(p => long.TryParse(p, out var n) ? n : 0).ToArray();
        return (parts, string.IsNullOrEmpty(pre) ? null : pre);
    }
}
