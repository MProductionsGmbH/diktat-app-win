using System.Net;
using Diktat.AI.Updates;

namespace Diktat.AI.Tests;

public sealed class UpdateCheckerTests
{
    private const string Release = """
        {
          "tag_name": "v0.3.0",
          "html_url": "https://github.com/MProductionsGmbH/diktat-app-win/releases/tag/v0.3.0",
          "body": "Neu: Meetings.",
          "assets": [
            {"name": "Diktat-0.3.0-win-x64.zip", "browser_download_url": "https://example.test/Diktat-0.3.0-win-x64.zip"},
            {"name": "Diktat-Setup-0.3.0.exe", "browser_download_url": "https://example.test/Diktat-Setup-0.3.0.exe"}
          ]
        }
        """;

    [Theory]
    [InlineData("0.2.0", "0.1.0", 1)]
    [InlineData("v0.10.0", "0.9.9", 1)]
    [InlineData("1.0", "1.0.0", 0)]
    [InlineData("1.0.0", "1.0.0-beta.2", 1)]
    [InlineData("1.0.0-beta.10", "1.0.0-beta.2", 1)]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]
    [InlineData("1.0.0-alpha.beta", "1.0.0-alpha.1", 1)]
    [InlineData("1.2.3+build.7", "1.2.3", 0)]
    [InlineData("0.1.0", "0.1.1", -1)]
    public void ComparesSemanticVersions(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(UpdateChecker.CompareVersions(a, b)));

    [Fact]
    public async Task FindsTheNewerReleaseAndPrefersTheInstaller()
    {
        var handler = FakeHandler.Json(Release);
        var update = await new UpdateChecker(handler).CheckAsync("0.2.1");
        Assert.NotNull(update);
        Assert.Equal("0.3.0", update.Version);
        Assert.True(update.IsNewer);
        Assert.Equal("https://example.test/Diktat-Setup-0.3.0.exe", update.DownloadUrl);
        Assert.Equal("https://github.com/MProductionsGmbH/diktat-app-win/releases/tag/v0.3.0", update.ReleasePageUrl);
        Assert.Equal("Neu: Meetings.", update.ReleaseNotes);
        var sent = handler.Requests[0];
        Assert.Equal(UpdateChecker.LatestReleaseUrl, sent.Uri.ToString());
        Assert.StartsWith("Diktat-Windows/0.2.1", sent.Headers["user-agent"]);
    }

    /// The names .github/workflows/release.yml attaches, fixed so releases/latest/download works.
    [Fact]
    public void FindsTheAssetNamesOfTheReleaseWorkflow()
    {
        const string json = """{"tag_name":"v0.2.0","html_url":"https://x.test/r","assets":[{"name":"Diktat-win-arm64.zip","browser_download_url":"https://x.test/arm.zip"},{"name":"Diktat-win-x64.zip","browser_download_url":"https://x.test/x64.zip"},{"name":"Diktat-Setup.exe","browser_download_url":"https://x.test/setup.exe"}]}""";
        Assert.Equal("https://x.test/setup.exe", UpdateChecker.Parse(json, "0.1.0").DownloadUrl);
        const string zipOnly = """{"tag_name":"v0.2.0","html_url":"https://x.test/r","assets":[{"name":"Diktat-win-arm64.zip","browser_download_url":"https://x.test/arm.zip"},{"name":"Diktat-win-x64.zip","browser_download_url":"https://x.test/x64.zip"}]}""";
        Assert.Equal("https://x.test/x64.zip", UpdateChecker.Parse(zipOnly, "0.1.0").DownloadUrl);
    }

    [Fact]
    public void FallsBackToTheZipAndThenToTheReleasePage()
    {
        var zipOnly = UpdateChecker.Parse("""{"tag_name":"0.3.0","html_url":"https://x.test/r","assets":[{"name":"Diktat-0.3.0-win-x64.zip","browser_download_url":"https://x.test/z.zip"}]}""", "0.3.0");
        Assert.Equal("https://x.test/z.zip", zipOnly.DownloadUrl);
        Assert.False(zipOnly.IsNewer);
        var none = UpdateChecker.Parse("""{"tag_name":"0.3.0","html_url":"https://x.test/r","assets":[{"name":"Diktat-0.3.0-mac.dmg","browser_download_url":"https://x.test/m.dmg"}]}""", "0.2.0");
        Assert.Equal("https://x.test/r", none.DownloadUrl);
    }

    [Fact]
    public async Task NoReleaseYetIsNotAnError() =>
        Assert.Null(await new UpdateChecker(FakeHandler.Json("""{"message":"Not Found"}""", HttpStatusCode.NotFound)).CheckAsync("0.1.0"));

    [Fact]
    public async Task OtherFailuresGiveAGermanMessage()
    {
        var error = await Assert.ThrowsAsync<UpdateCheckException>(() =>
            new UpdateChecker(FakeHandler.Json("{}", HttpStatusCode.Forbidden)).CheckAsync("0.1.0"));
        Assert.Equal("Die Suche nach Updates ist fehlgeschlagen. Bitte später nochmal versuchen.", error.Message);
        await Assert.ThrowsAsync<UpdateCheckException>(() =>
            new UpdateChecker(new FakeHandler(_ => throw new HttpRequestException("offline"))).CheckAsync("0.1.0"));
    }
}

public sealed class FeedbackMailTests
{
    [Fact]
    public void HiddenWithoutAnAddress()
    {
        Assert.False(FeedbackMail.IsAvailable);
        Assert.Null(FeedbackMail.Url("0.1.0", "1"));
    }

    [Fact]
    public void MailNamesVersionAndSetup()
    {
        var url = FeedbackMail.Url("feedback@example.test", "0.1.0", "42", ["Sprachmodell: Parakeet"], "Surface Laptop 7");
        Assert.StartsWith("mailto:feedback@example.test?subject=", url);
        var decoded = Uri.UnescapeDataString(url);
        Assert.Contains("subject=Diktat Feedback (Windows 0.1.0, Build 42)", decoded);
        Assert.Contains("App: Diktat 0.1.0 (Build 42) für Windows", decoded);
        Assert.Contains("Gerät: Surface Laptop 7", decoded);
        Assert.Contains("Sprachmodell: Parakeet", decoded);
        Assert.Contains("Bitte oberhalb der Linie schreiben.", decoded);
        Assert.DoesNotContain(" ", url);
    }
}
