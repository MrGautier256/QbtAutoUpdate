using System.Net;
using System.Security.Cryptography;
using System.Text;
using QbtAutoUpdate.Core;

namespace QbtAutoUpdate.Tests;

public sealed class DownloaderTests : IDisposable
{
    private const string Name = "qbittorrent_5.3.0_x64_setup.exe";
    private static readonly QbtVersion Version = QbtVersion.Parse("5.3.0");
    private static readonly byte[] Exe = [.. "MZ"u8, .. Enumerable.Range(0, 4000).Select(i => (byte)i)];
    private static readonly byte[] Asc = Encoding.ASCII.GetBytes("-----BEGIN PGP SIGNATURE-----\n\nabc\n-----END PGP SIGNATURE-----\n");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "QbtAutoUpdate.Tests", Guid.NewGuid().ToString("N"));

    private static string Url(string file) => SourceForgeClient.DownloadUrl(Version, file).AbsoluteUri;

    private static string Mirror(string file, string mirror = "netix") =>
        $"https://{mirror}.dl.sourceforge.net/project/qbittorrent/qbittorrent-win32/qbittorrent-5.3.0/{file}?viasf=1";

    private static ResolvedRelease Release(long? size = null, string? sha256 = null) => new(
        ResolveStatus.Found, Version, Variant.Lt2,
        new RemoteFile(Name, size ?? Exe.Length, sha256, DateTimeOffset.UnixEpoch),
        new RemoteFile(Name + ".asc", Asc.Length, null, DateTimeOffset.UnixEpoch),
        CpuArch.X64);

    private static FakeHttpHandler Serving(byte[]? exe = null, string exeType = "application/octet-stream") => new FakeHttpHandler()
        .OnRedirect(Url(Name + ".asc"), Mirror(Name + ".asc"))
        .OnBytes(Mirror(Name + ".asc"), Asc, "application/pgp-signature")
        .OnRedirect(Url(Name), Mirror(Name))
        .OnBytes(Mirror(Name), exe ?? Exe, exeType);

    private Task<DownloadedRelease> Download(FakeHttpHandler handler, ResolvedRelease? release = null)
    {
        var http = new SafeHttpClient(NullLog.Instance, handler);
        var downloader = new Downloader(http, NullLog.Instance) { RetryDelays = [TimeSpan.Zero] };
        return downloader.DownloadAsync(release ?? Release(), _dir, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Downloads_installer_and_signature_through_a_mirror()
    {
        DownloadedRelease result = await Download(Serving());

        Assert.Equal(Exe, File.ReadAllBytes(result.InstallerPath));
        Assert.Equal(Asc, File.ReadAllBytes(result.SignaturePath));
        Assert.Empty(Directory.GetFiles(_dir, "*.partial"));
    }

    [Fact]
    public async Task Retries_when_a_mirror_serves_an_html_page()
    {
        var handler = new FakeHttpHandler()
            .OnRedirect(Url(Name + ".asc"), Mirror(Name + ".asc"))
            .OnBytes(Mirror(Name + ".asc"), Asc, "application/pgp-signature")
            .OnRedirect(Url(Name), Mirror(Name, "bad"))
            .OnRedirect(Url(Name), Mirror(Name, "good"))
            .OnText(Mirror(Name, "bad"), "<html>mirror maintenance</html>", "text/html")
            .OnBytes(Mirror(Name, "good"), Exe);

        DownloadedRelease result = await Download(handler);

        Assert.Equal(Exe, File.ReadAllBytes(result.InstallerPath));
        Assert.Contains(handler.Requests, r => r.Host == "bad.dl.sourceforge.net");
    }

    [Fact]
    public async Task Rejects_html_even_without_an_html_content_type()
    {
        byte[] html = Encoding.ASCII.GetBytes("<!DOCTYPE html><html>" + new string(' ', Exe.Length - 21));

        await Assert.ThrowsAsync<InvalidDataException>(() => Download(Serving(html)));
        Assert.False(File.Exists(Path.Combine(_dir, Name)));
        Assert.Empty(Directory.GetFiles(_dir, "*.partial"));
    }

    [Fact]
    public async Task Rejects_a_size_that_differs_from_the_listing()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(Serving(), Release(size: Exe.Length + 1)));
    }

    [Fact]
    public async Task Checks_the_sha256_when_the_listing_has_one()
    {
        string wrong = Convert.ToHexStringLower(SHA256.HashData("other"u8));
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(Serving(), Release(sha256: wrong)));

        string right = Convert.ToHexStringLower(SHA256.HashData(Exe));
        DownloadedRelease result = await Download(Serving(), Release(sha256: right));
        Assert.True(File.Exists(result.InstallerPath));
    }

    [Fact]
    public async Task Never_follows_a_mirror_outside_sourceforge()
    {
        var handler = new FakeHttpHandler()
            .OnRedirect(Url(Name + ".asc"), "https://mirror.evil.example/a.asc");

        await Assert.ThrowsAsync<BlockedUrlException>(() => Download(handler));
        Assert.DoesNotContain(handler.Requests, r => r.Host.EndsWith("evil.example", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Gives_up_after_three_attempts()
    {
        var handler = new FakeHttpHandler().OnStatus(Url(Name + ".asc"), HttpStatusCode.ServiceUnavailable);

        await Assert.ThrowsAsync<HttpStatusException>(() => Download(handler));
        Assert.Equal(Downloader.MaxAttempts, handler.Requests.Count);
    }

    [Fact]
    public void Validates_signature_and_executable_headers()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "x");

        File.WriteAllBytes(path, Asc);
        Downloader.ValidateContent(path, isSignature: true, "x", "m");
        Assert.Throws<InvalidDataException>(() => Downloader.ValidateContent(path, isSignature: false, "x", "m"));

        File.WriteAllBytes(path, Exe);
        Downloader.ValidateContent(path, isSignature: false, "x", "m");
        Assert.Throws<InvalidDataException>(() => Downloader.ValidateContent(path, isSignature: true, "x", "m"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
