using System.Net;
using QbtAutoUpdate.Core;

namespace QbtAutoUpdate.Tests;

public class VersionSourceTests
{
    private const string Main = "https://www.qbittorrent.org/versions.json";
    private const string Backup = "https://qbittorrent.github.io/qBittorrent-website/versions.json";

    private static string Json(string win) =>
        """{"win": { "version": "VERSION"}, "macos": { "version": "5.2.3"}}""".Replace("VERSION", win, StringComparison.Ordinal);

    private static async Task<LatestVersion> Latest(FakeHttpHandler handler)
    {
        using var http = new SafeHttpClient(NullLog.Instance, handler);
        return await new VersionSource(http, NullLog.Instance).GetLatestAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Takes_the_highest_of_both_sources()
    {
        LatestVersion latest = await Latest(new FakeHttpHandler().OnText(Main, Json("5.3.0")).OnText(Backup, Json("5.3.1")));

        Assert.Equal(QbtVersion.Parse("5.3.1"), latest.Version);
        Assert.Equal(2, latest.Sources.Count);
    }

    [Fact]
    public async Task Tolerates_one_failed_source()
    {
        LatestVersion latest = await Latest(new FakeHttpHandler().OnStatus(Main, HttpStatusCode.BadGateway).OnText(Backup, Json("5.3.0")));

        Assert.Equal(QbtVersion.Parse("5.3.0"), latest.Version);
        Assert.Contains(latest.Sources, s => s.Url.Host == "www.qbittorrent.org" && s.Version is null && s.Error is not null);
    }

    [Fact]
    public async Task Tolerates_one_source_with_a_changed_format()
    {
        LatestVersion latest = await Latest(new FakeHttpHandler().OnText(Main, """{"windows": "6.0.0"}""").OnText(Backup, Json("5.3.0")));

        Assert.Equal(QbtVersion.Parse("5.3.0"), latest.Version);
    }

    [Fact]
    public async Task Fails_when_both_sources_fail()
    {
        var handler = new FakeHttpHandler().OnStatus(Main, HttpStatusCode.NotFound).OnText(Backup, "<html>maintenance</html>", "text/html");

        var error = await Assert.ThrowsAsync<VersionSourceException>(() => Latest(handler));
        Assert.Equal(2, error.Sources.Count);
    }

    [Fact]
    public async Task A_redirect_to_a_foreign_host_counts_as_a_failure()
    {
        var handler = new FakeHttpHandler()
            .OnRedirect(Main, "https://evil.example/versions.json")
            .OnText(Backup, Json("5.3.0"));

        LatestVersion latest = await Latest(handler);

        Assert.Equal(QbtVersion.Parse("5.3.0"), latest.Version);
        Assert.DoesNotContain(handler.Requests, r => r.Host == "evil.example");
    }

    [Theory]
    [InlineData("""{"win": {"version": "5.3.0"}}""", "5.3.0")]
    [InlineData("""{"win": {"version": "5.1.2.10"}, "extra": [1, 2]}""", "5.1.2.10")]
    public void Parses_valid_documents(string json, string expected) =>
        Assert.Equal(QbtVersion.Parse(expected), VersionSource.ParseWindowsVersion(json, out _));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"macos": {"version": "5.3.0"}}""")]
    [InlineData("""{"win": "5.3.0"}""")]
    [InlineData("""{"win": {"version": 5.3}}""")]
    [InlineData("""{"win": {"version": "5.4.0rc1"}}""")]
    [InlineData("""{"win": {"version": null}}""")]
    public void Rejects_changed_formats(string json)
    {
        Assert.Null(VersionSource.ParseWindowsVersion(json, out string? error));
        Assert.NotNull(error);
    }
}
