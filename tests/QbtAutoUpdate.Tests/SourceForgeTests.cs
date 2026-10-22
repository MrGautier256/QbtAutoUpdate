using System.Net;
using QbtAutoUpdate.Core;

namespace QbtAutoUpdate.Tests;

public class SourceForgeTests
{
    private static readonly QbtVersion V530 = QbtVersion.Parse("5.3.0");

    [Fact]
    public void Parses_the_real_5_3_0_feed()
    {
        List<RemoteFile> files = SourceForgeClient.ParseRss(Fixtures.Rss("5.3.0"), V530);

        Assert.Equal(5, files.Count);
        RemoteFile installer = Assert.Single(files, f => f.Name == "qbittorrent_5.3.0_x64_setup.exe");
        Assert.Equal(50030660, installer.Size);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 12, 52, 43, TimeSpan.Zero), installer.Published);
        Assert.Contains(files, f => f.Name == "qbittorrent_5.3.0_lt12_x64_setup.exe.asc" && f.Size == 887);
        Assert.Contains(files, f => f.Name == "README");
    }

    [Theory]
    [MemberData(nameof(AllVersions))]
    public void Every_real_feed_parses_with_sizes_and_dates(string version)
    {
        List<RemoteFile> files = SourceForgeClient.ParseRss(Fixtures.Rss(version), QbtVersion.Parse(version));

        Assert.Equal(5, files.Count);
        Assert.All(files, f =>
        {
            Assert.NotNull(f.Size);
            Assert.NotNull(f.Published);
        });
    }

    public static TheoryData<string> AllVersions() => new(Fixtures.RssVersions);

    [Fact]
    public void Ignores_items_from_other_folders()
    {
        // The 5.2.4 feed parsed as if it were 5.3.0: nothing belongs to that folder.
        Assert.Empty(SourceForgeClient.ParseRss(Fixtures.Rss("5.2.4"), V530));
    }

    [Fact]
    public void Refuses_a_dtd()
    {
        const string Xml = """<?xml version="1.0"?><!DOCTYPE rss [<!ENTITY x "y">]><rss><channel/></rss>""";
        Assert.ThrowsAny<System.Xml.XmlException>(() => SourceForgeClient.ParseRss(Xml, V530));
    }

    [Fact]
    public void Parses_the_real_folder_page()
    {
        List<RemoteFile> files = SourceForgeClient.ParseFolderPage(Fixtures.Text("SourceForge", "folder_5.3.0.html"), V530);

        Assert.Equal(5, files.Count);
        RemoteFile installer = Assert.Single(files, f => f.Name == "qbittorrent_5.3.0_lt12_x64_setup.exe");
        Assert.Equal("10876b7113a6d6bd7f5cef401e4bc6d8893adbc0bcd693eb396c6fb9f93a8e58", installer.Sha256);
        Assert.False(installer.Staged);
        Assert.Null(installer.Published);
    }

    [Fact]
    public void A_folder_page_without_the_file_list_is_an_error() =>
        Assert.Throws<InvalidDataException>(() => SourceForgeClient.ParseFolderPage("<html></html>", V530));

    [Theory]
    [InlineData("Tue, 20 Oct 2026 12:52:43 UT", "2026-10-20T12:52:43+00:00")]
    [InlineData("Mon, 28 Sep 2026 18:50:08 GMT", "2026-09-28T18:50:08+00:00")]
    [InlineData("Sat, 5 Sep 2026 23:01:47 +0200", "2026-09-05T23:01:47+02:00")]
    [InlineData("Sat, 05 Sep 2026 23:01:47 -0500", "2026-09-05T23:01:47-05:00")]
    public void Parses_rss_dates(string text, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), SourceForgeClient.ParseRssDate(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("yesterday")]
    [InlineData("Tue, 20 Oct 2026 12:52:43 CEST")]
    public void Unknown_dates_are_null(string? text) => Assert.Null(SourceForgeClient.ParseRssDate(text));

    [Fact]
    public async Task Falls_back_to_the_folder_page_when_the_feed_fails()
    {
        var handler = new FakeHttpHandler()
            .OnStatus(SourceForgeClient.FolderRssUrl(V530).AbsoluteUri, HttpStatusCode.InternalServerError)
            .OnText(SourceForgeClient.FolderPageUrl(V530).AbsoluteUri, Fixtures.Text("SourceForge", "folder_5.3.0.html"), "text/html");
        using var http = new SafeHttpClient(NullLog.Instance, handler);

        FolderListing listing = await new SourceForgeClient(http, NullLog.Instance).ListAsync(V530, TestContext.Current.CancellationToken);

        Assert.Equal(ListingSource.FolderPage, listing.Source);
        Assert.Equal(5, listing.Files.Count);
    }

    [Fact]
    public async Task A_404_feed_means_the_folder_does_not_exist_yet()
    {
        using var http = new SafeHttpClient(NullLog.Instance, new FakeHttpHandler());

        FolderListing listing = await new SourceForgeClient(http, NullLog.Instance).ListAsync(QbtVersion.Parse("9.9.9"), TestContext.Current.CancellationToken);

        Assert.True(listing.FolderMissing);
        Assert.Empty(listing.Files);
    }

    [Fact]
    public void Download_urls_point_to_the_redirector()
    {
        Assert.Equal(
            "https://downloads.sourceforge.net/project/qbittorrent/qbittorrent-win32/qbittorrent-5.3.0/qbittorrent_5.3.0_x64_setup.exe",
            SourceForgeClient.DownloadUrl(V530, "qbittorrent_5.3.0_x64_setup.exe").AbsoluteUri);
    }
}
