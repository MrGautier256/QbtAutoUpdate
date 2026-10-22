using System.Diagnostics;
using QbtAutoUpdate.Core;
using QbtAutoUpdate.Platform;

namespace QbtAutoUpdate.Tests;

/// <summary>
/// Talks to the real servers and downloads real installers (about 90 MB). Off by default and in CI:
/// set QBTAU_INTEGRATION=1 to run them. Nothing is ever executed or installed.
/// </summary>
[Trait("Category", "Integration")]
public sealed class IntegrationTests : IDisposable
{
    private static readonly QbtVersion V530 = QbtVersion.Parse("5.3.0");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "QbtAutoUpdate.Integration", Guid.NewGuid().ToString("N"));
    private readonly ListLog _log = new();

    private static void RequireOptIn() =>
        Assert.SkipUnless(Environment.GetEnvironmentVariable("QBTAU_INTEGRATION") == "1", "Set QBTAU_INTEGRATION=1 to run integration tests.");

    [Fact]
    public async Task The_latest_release_resolves_for_both_variants()
    {
        RequireOptIn();
        using var http = new SafeHttpClient(_log);

        LatestVersion latest = await new VersionSource(http, _log).GetLatestAsync(TestContext.Current.CancellationToken);
        FolderListing listing = await new SourceForgeClient(http, _log).ListAsync(latest.Version, TestContext.Current.CancellationToken);

        Assert.True(latest.Version >= V530);
        Assert.Equal(ResolveStatus.Found, ReleaseResolver.Resolve(listing, Variant.Lt2, CpuArch.X64).Status);
        Assert.Equal(ResolveStatus.Found, ReleaseResolver.Resolve(listing, Variant.Lt12, CpuArch.X64).Status);
    }

    /// <summary>Spec §8: the real 5.3.0 installers verify against the real key, and the offline variant heuristic reads them right.</summary>
    [Theory]
    [InlineData(Variant.Lt2)]
    [InlineData(Variant.Lt12)]
    public async Task Real_5_3_0_installers_verify_and_their_variant_is_detected(Variant variant)
    {
        RequireOptIn();
        using var http = new SafeHttpClient(_log);
        FolderListing listing = await new SourceForgeClient(http, _log).ListAsync(V530, TestContext.Current.CancellationToken);
        ResolvedRelease release = ReleaseResolver.Resolve(listing, variant, CpuArch.X64);

        DownloadedRelease downloaded = await new Downloader(http, _log).DownloadAsync(release, _dir, TestContext.Current.CancellationToken);
        PgpResult result = PgpVerifier.CreateOfficial().VerifyFile(downloaded.InstallerPath, downloaded.SignaturePath, DateTimeOffset.UtcNow);

        Assert.Equal(PgpVerdict.Valid, result.Verdict);
        Assert.Equal(new QbtVersion(5, 3, 0, 0), new SystemInstallEnvironment().GetExeMetadata(downloaded.InstallerPath).FileVersion);

        string? sevenZip = FindSevenZip();
        Assert.SkipWhen(sevenZip is null, "7-Zip is needed to unpack the NSIS installer.");
        string extracted = Path.Combine(_dir, "extracted");
        using (Process unpack = Process.Start(new ProcessStartInfo(sevenZip!, ["e", "-y", "-o" + extracted, downloaded.InstallerPath, "qbittorrent.exe"])
               { RedirectStandardOutput = true, UseShellExecute = false })!)
        {
            await unpack.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            await unpack.WaitForExitAsync(TestContext.Current.CancellationToken);
        }

        BinaryScanResult scan = QbtBinaryScanner.Scan(Path.Combine(extracted, "qbittorrent.exe"));
        Assert.Equal(variant, scan.Variant);
        Assert.False(scan.IsPartial);
        Assert.False(scan.HasForkMarkers);
    }

    [Fact]
    public void The_installed_qbittorrent_is_detected_when_present()
    {
        RequireOptIn();
        InstallInfo info = new InstallDetector(new SystemInstallEnvironment()).Detect();
        Assert.SkipWhen(info.Kind == InstallKind.NotInstalled, "qBittorrent is not installed on this machine.");

        Assert.Equal(InstallKind.Installed, info.Kind);
        Assert.NotNull(info.Version);
        Assert.NotNull(info.DetectedVariant);
    }

    private static string? FindSevenZip()
    {
        string candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }
}
