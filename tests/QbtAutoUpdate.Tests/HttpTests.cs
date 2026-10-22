using QbtAutoUpdate.Core;

namespace QbtAutoUpdate.Tests;

public class HttpTests
{
    [Theory]
    [InlineData("https://www.qbittorrent.org/versions.json")]
    [InlineData("https://qbittorrent.github.io/qBittorrent-website/versions.json")]
    [InlineData("https://sourceforge.net/projects/qbittorrent/rss?path=/x")]
    [InlineData("https://downloads.sourceforge.net/project/qbittorrent/a.exe")]
    [InlineData("https://netix.dl.sourceforge.net/project/qbittorrent/a.exe?viasf=1")]
    [InlineData("https://sf-eu-introserv-1.dl.sourceforge.net/a.exe")]
    [InlineData("https://WWW.QBITTORRENT.ORG/versions.json")]
    public void Allows_official_hosts(string url) => Assert.True(HostPolicy.IsAllowed(new Uri(url)));

    [Theory]
    [InlineData("http://www.qbittorrent.org/versions.json")]
    [InlineData("https://qbittorrent.org/versions.json")]
    [InlineData("https://www.qbittorrent.org:8443/versions.json")]
    [InlineData("https://user:pass@sourceforge.net/x")]
    [InlineData("https://dl.sourceforge.net/a.exe")]
    [InlineData("https://a.b.dl.sourceforge.net/a.exe")]
    [InlineData("https://netix.dl.sourceforge.net.evil.example/a.exe")]
    [InlineData("https://evilsourceforge.net/a.exe")]
    [InlineData("https://github.com/qbittorrent/qBittorrent/releases")]
    [InlineData("https://203.0.113.5/a.exe")]
    [InlineData("ftp://sourceforge.net/a.exe")]
    public void Blocks_everything_else(string url) => Assert.False(HostPolicy.IsAllowed(new Uri(url)));

    [Fact]
    public async Task Follows_redirects_and_checks_every_hop()
    {
        var handler = new FakeHttpHandler()
            .OnRedirect("https://downloads.sourceforge.net/a", "https://netix.dl.sourceforge.net/a?x=1")
            .OnText("https://netix.dl.sourceforge.net/a?x=1", "ok");
        using var http = new SafeHttpClient(NullLog.Instance, handler);

        string body = await http.GetStringAsync(new Uri("https://downloads.sourceforge.net/a"), 1024, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal("ok", body);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Resolves_relative_redirects()
    {
        var handler = new FakeHttpHandler()
            .OnRedirect("https://sourceforge.net/a", "/b")
            .OnText("https://sourceforge.net/b", "ok");
        using var http = new SafeHttpClient(NullLog.Instance, handler);

        Assert.Equal("ok", await http.GetStringAsync(new Uri("https://sourceforge.net/a"), 1024, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("http://netix.dl.sourceforge.net/a")]
    [InlineData("https://mirror.evil.example/a")]
    public async Task Refuses_a_redirect_to_a_foreign_or_plain_http_host(string target)
    {
        var handler = new FakeHttpHandler().OnRedirect("https://downloads.sourceforge.net/a", target);
        using var http = new SafeHttpClient(NullLog.Instance, handler);

        await Assert.ThrowsAsync<BlockedUrlException>(() =>
            http.GetStringAsync(new Uri("https://downloads.sourceforge.net/a"), 1024, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Stops_redirect_loops()
    {
        var handler = new FakeHttpHandler()
            .OnRedirect("https://sourceforge.net/a", "https://sourceforge.net/b")
            .OnRedirect("https://sourceforge.net/b", "https://sourceforge.net/a");
        using var http = new SafeHttpClient(NullLog.Instance, handler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            http.GetStringAsync(new Uri("https://sourceforge.net/a"), 1024, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Enforces_the_size_limit()
    {
        var handler = new FakeHttpHandler().OnText("https://www.qbittorrent.org/big", new string('x', 5000));
        using var http = new SafeHttpClient(NullLog.Instance, handler);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            http.GetStringAsync(new Uri("https://www.qbittorrent.org/big"), 1000, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Reports_http_errors_with_their_status()
    {
        var handler = new FakeHttpHandler().OnStatus("https://www.qbittorrent.org/x", System.Net.HttpStatusCode.ServiceUnavailable);
        using var http = new SafeHttpClient(NullLog.Instance, handler);

        var error = await Assert.ThrowsAsync<HttpStatusException>(() =>
            http.GetStringAsync(new Uri("https://www.qbittorrent.org/x"), 1000, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, error.Status);
    }
}
