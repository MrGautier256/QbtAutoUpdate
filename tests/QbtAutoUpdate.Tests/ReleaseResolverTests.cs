using QbtAutoUpdate.Core;

namespace QbtAutoUpdate.Tests;

public class ReleaseResolverTests
{
    private static FolderListing RealListing(string version)
    {
        QbtVersion v = QbtVersion.Parse(version);
        return new FolderListing(v, SourceForgeClient.ParseRss(Fixtures.Rss(version), v), ListingSource.Rss, false);
    }

    private static FolderListing Listing(string version, params string[] names) =>
        new(QbtVersion.Parse(version), names.Select(n => new RemoteFile(n, 1000, null, DateTimeOffset.UnixEpoch)).ToList(), ListingSource.Rss, false);

    /// <summary>Expected file per version and libtorrent series, from the real SourceForge listings.</summary>
    [Theory]
    [InlineData("4.6.0", Variant.Lt12, "qbittorrent_4.6.0_x64_setup.exe")]
    [InlineData("4.6.0", Variant.Lt2, "qbittorrent_4.6.0_lt20_qt6_x64_setup.exe")]
    [InlineData("4.6.7", Variant.Lt2, "qbittorrent_4.6.7_lt20_qt6_x64_setup.exe")]
    [InlineData("5.0.0", Variant.Lt12, "qbittorrent_5.0.0_x64_setup.exe")]
    [InlineData("5.0.0", Variant.Lt2, "qbittorrent_5.0.0_lt20_qt6_x64_setup.exe")]
    [InlineData("5.0.1", Variant.Lt2, "qbittorrent_5.0.1_qt6_lt20_x64_setup.exe")]
    [InlineData("5.0.5", Variant.Lt12, "qbittorrent_5.0.5_x64_setup.exe")]
    [InlineData("5.0.5", Variant.Lt2, "qbittorrent_5.0.5_qt6_lt20_x64_setup.exe")]
    [InlineData("5.1.0", Variant.Lt2, "qbittorrent_5.1.0_qt6_lt20_x64_setup.exe")]
    [InlineData("5.1.4", Variant.Lt12, "qbittorrent_5.1.4_x64_setup.exe")]
    [InlineData("5.1.4", Variant.Lt2, "qbittorrent_5.1.4_qt6_lt20_x64_setup.exe")]
    [InlineData("5.2.0", Variant.Lt12, "qbittorrent_5.2.0_x64_setup.exe")]
    [InlineData("5.2.0", Variant.Lt2, "qbittorrent_5.2.0_lt20_x64_setup.exe")]
    [InlineData("5.2.3", Variant.Lt2, "qbittorrent_5.2.3_lt20_x64_setup.exe")]
    [InlineData("5.2.4", Variant.Lt12, "qbittorrent_5.2.4_x64_setup.exe")]
    [InlineData("5.2.4", Variant.Lt2, "qbittorrent_5.2.4_lt20_x64_setup.exe")]
    [InlineData("5.3.0", Variant.Lt12, "qbittorrent_5.3.0_lt12_x64_setup.exe")]
    [InlineData("5.3.0", Variant.Lt2, "qbittorrent_5.3.0_x64_setup.exe")]
    public void Picks_the_file_of_the_requested_series(string version, Variant variant, string expected)
    {
        ResolvedRelease release = ReleaseResolver.Resolve(RealListing(version), variant, CpuArch.X64);

        Assert.Equal(ResolveStatus.Found, release.Status);
        Assert.Equal(expected, release.Installer!.Name);
        Assert.Equal(expected + ".asc", release.Signature!.Name);
        Assert.Equal(CpuArch.X64, release.InstallerArch);
        Assert.Empty(release.UnknownFiles!);
    }

    [Theory]
    [MemberData(nameof(AllVersions))]
    public void Every_real_installer_is_known_and_signed(string version)
    {
        FolderListing listing = RealListing(version);
        string[] installers = listing.Files.Select(f => f.Name).Where(n => n.EndsWith(".exe", StringComparison.Ordinal)).ToArray();

        Assert.Equal(2, installers.Length);
        Assert.Equivalent(new[] { Variant.Lt12, Variant.Lt2 }, installers.Select(n => ReleaseResolver.Classify(listing.Version, n)!.Value.Variant));
        Assert.All(installers, n => Assert.NotNull(listing.Find(n + ".asc")));
    }

    public static TheoryData<string> AllVersions() => new(Fixtures.RssVersions);

    [Fact]
    public void The_5_2_default_is_libtorrent_1_2_and_the_5_3_default_is_libtorrent_2()
    {
        Assert.Equal(Variant.Lt12, ReleaseResolver.Classify(QbtVersion.Parse("5.2.4"), "qbittorrent_5.2.4_x64_setup.exe")!.Value.Variant);
        Assert.Equal(Variant.Lt2, ReleaseResolver.Classify(QbtVersion.Parse("5.3.0"), "qbittorrent_5.3.0_x64_setup.exe")!.Value.Variant);
    }

    [Theory]
    [InlineData("5.3.0", "qbittorrent_5.3.0rc1_x64_setup.exe")]
    [InlineData("5.3.0", "qbittorrent_5.2.4_x64_setup.exe")]
    [InlineData("5.3.0", "qbittorrent_5.3.0_lt20_x64_setup.exe")]
    [InlineData("5.2.4", "qbittorrent_5.2.4_lt12_x64_setup.exe")]
    [InlineData("5.0.5", "qbittorrent_5.0.5_lt20_qt6_x64_setup.exe")]
    [InlineData("5.1.4", "qbittorrent_5.1.4_x64_setup.exe.asc")]
    [InlineData("4.5.0", "qbittorrent_4.5.0_x64_setup.exe")]
    public void Unknown_names_are_not_classified(string version, string name) =>
        Assert.Null(ReleaseResolver.Classify(QbtVersion.Parse(version), name));

    [Fact]
    public void Future_versions_follow_the_5_3_naming()
    {
        Assert.Equal((Variant.Lt12, CpuArch.X64), ReleaseResolver.Classify(QbtVersion.Parse("5.4.1"), "qbittorrent_5.4.1_lt12_x64_setup.exe"));
    }

    [Fact]
    public void Arm64_machines_fall_back_to_x64_when_no_arm64_build_exists()
    {
        ResolvedRelease release = ReleaseResolver.Resolve(RealListing("5.3.0"), Variant.Lt2, CpuArch.Arm64);

        Assert.Equal(ResolveStatus.Found, release.Status);
        Assert.Equal(CpuArch.X64, release.InstallerArch);
        Assert.Equal("qbittorrent_5.3.0_x64_setup.exe", release.Installer!.Name);
    }

    [Fact]
    public void Arm64_machines_prefer_an_arm64_build()
    {
        FolderListing listing = Listing("5.3.0",
            "qbittorrent_5.3.0_x64_setup.exe", "qbittorrent_5.3.0_x64_setup.exe.asc",
            "qbittorrent_5.3.0_arm64_setup.exe", "qbittorrent_5.3.0_arm64_setup.exe.asc");

        ResolvedRelease release = ReleaseResolver.Resolve(listing, Variant.Lt2, CpuArch.Arm64);

        Assert.Equal("qbittorrent_5.3.0_arm64_setup.exe", release.Installer!.Name);
        Assert.Equal(CpuArch.Arm64, release.InstallerArch);
    }

    [Fact]
    public void X64_machines_never_get_an_arm64_build()
    {
        FolderListing listing = Listing("5.3.0", "qbittorrent_5.3.0_arm64_setup.exe", "qbittorrent_5.3.0_arm64_setup.exe.asc");

        Assert.Equal(ResolveStatus.VariantMissing, ReleaseResolver.Resolve(listing, Variant.Lt2, CpuArch.X64).Status);
    }

    [Fact]
    public void A_missing_variant_is_reported_not_substituted()
    {
        FolderListing listing = Listing("5.4.0", "qbittorrent_5.4.0_x64_setup.exe", "qbittorrent_5.4.0_x64_setup.exe.asc", "README");

        ResolvedRelease release = ReleaseResolver.Resolve(listing, Variant.Lt12, CpuArch.X64);

        Assert.Equal(ResolveStatus.VariantMissing, release.Status);
        Assert.Null(release.Installer);
    }

    [Fact]
    public void A_missing_signature_means_not_yet_available()
    {
        FolderListing listing = Listing("5.3.0", "qbittorrent_5.3.0_x64_setup.exe", "qbittorrent_5.3.0_lt12_x64_setup.exe", "qbittorrent_5.3.0_lt12_x64_setup.exe.asc");

        Assert.Equal(ResolveStatus.NotYetAvailable, ReleaseResolver.Resolve(listing, Variant.Lt2, CpuArch.X64).Status);
    }

    [Fact]
    public void A_staged_file_means_not_yet_available()
    {
        var listing = new FolderListing(QbtVersion.Parse("5.3.0"),
        [
            new RemoteFile("qbittorrent_5.3.0_x64_setup.exe", null, null, null, Staged: true),
            new RemoteFile("qbittorrent_5.3.0_x64_setup.exe.asc", null, null, null),
        ], ListingSource.FolderPage, false);

        Assert.Equal(ResolveStatus.NotYetAvailable, ReleaseResolver.Resolve(listing, Variant.Lt2, CpuArch.X64).Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Empty_or_missing_folders_mean_not_yet_available(bool missing)
    {
        var listing = new FolderListing(QbtVersion.Parse("5.4.0"), [], ListingSource.Rss, missing);

        Assert.Equal(ResolveStatus.NotYetAvailable, ReleaseResolver.Resolve(listing, Variant.Lt2, CpuArch.X64).Status);
    }

    [Fact]
    public void Unknown_files_are_reported()
    {
        FolderListing listing = Listing("5.3.0",
            "qbittorrent_5.3.0_x64_setup.exe", "qbittorrent_5.3.0_x64_setup.exe.asc", "qbittorrent_5.3.0_x64_portable.zip");

        ResolvedRelease release = ReleaseResolver.Resolve(listing, Variant.Lt2, CpuArch.X64);

        Assert.Equal(ResolveStatus.Found, release.Status);
        Assert.Equal(["qbittorrent_5.3.0_x64_portable.zip"], release.UnknownFiles!);
    }
}
