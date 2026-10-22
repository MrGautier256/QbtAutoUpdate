using System.Net;
using QbtAutoUpdate.Core;

namespace QbtAutoUpdate.Tests;

public class UpdatePlannerTests
{
    private const string Main = "https://www.qbittorrent.org/versions.json";
    private const string Backup = "https://qbittorrent.github.io/qBittorrent-website/versions.json";
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 20, 0, 0, TimeSpan.Zero);

    private static readonly InstallInfo Installed524Lt12 = new()
    {
        Kind = InstallKind.Installed,
        InstallDir = @"C:\Program Files\qBittorrent",
        RegistryVersion = QbtVersion.Parse("5.2.4"),
        FileVersion = QbtVersion.Parse("5.2.4"),
        Scan = new BinaryScanResult(["1.2.20.0"], ["-LT12K0-"], false),
    };

    private static InstallInfo WithVariant(InstallInfo install, Variant? variant) => install with
    {
        Scan = variant switch
        {
            Variant.Lt2 => new BinaryScanResult(["2.0.15.0"], ["-LT20F0-"], false),
            Variant.Lt12 => new BinaryScanResult(["1.2.20.0"], ["-LT12K0-"], false),
            _ => BinaryScanResult.Empty,
        },
    };

    private static FakeHttpHandler Server(string latest = "5.3.0")
    {
        string json = """{"win": {"version": "VERSION"}}""".Replace("VERSION", latest, StringComparison.Ordinal);
        var handler = new FakeHttpHandler().OnText(Main, json).OnText(Backup, json);
        QbtVersion version = QbtVersion.Parse(latest);
        if (Fixtures.RssVersions.Contains(latest))
            handler.OnText(SourceForgeClient.FolderRssUrl(version).AbsoluteUri, Fixtures.Rss(latest), "application/rss+xml");
        return handler;
    }

    private static async Task<UpdatePlan> Plan(
        InstallInfo? install = null, MachineConfig? config = null, UserSettings? user = null, UpdateState? state = null,
        bool manual = false, FakeHttpHandler? server = null, bool metered = false, DateTimeOffset? now = null)
    {
        var log = new ListLog();
        using var http = new SafeHttpClient(log, server ?? Server());
        var planner = new UpdatePlanner(new VersionSource(http, log), new SourceForgeClient(http, log), log, CpuArch.X64, () => metered, () => now ?? Now);
        return await planner.PlanAsync(
            install ?? Installed524Lt12, config ?? MachineConfig.Default, user ?? UserSettings.Default, state ?? UpdateState.Empty,
            new PlanOptions(manual), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_5_2_default_build_moves_to_the_5_3_lt12_build()
    {
        UpdatePlan plan = await Plan();

        Assert.Equal(PlanOutcome.Ready, plan.Outcome);
        Assert.Equal(Variant.Lt12, plan.Variant);
        Assert.Equal("qbittorrent.exe", plan.VariantSource);
        Assert.Equal("qbittorrent_5.3.0_lt12_x64_setup.exe", plan.Release!.Installer!.Name);
    }

    [Fact]
    public async Task A_5_2_lt20_build_moves_to_the_5_3_default_build()
    {
        UpdatePlan plan = await Plan(WithVariant(Installed524Lt12, Variant.Lt2));

        Assert.Equal("qbittorrent_5.3.0_x64_setup.exe", plan.Release!.Installer!.Name);
    }

    [Fact]
    public async Task An_explicit_variant_in_the_config_wins()
    {
        UpdatePlan plan = await Plan(config: new MachineConfig { Variant = VariantSetting.Lt2 });

        Assert.Equal(Variant.Lt2, plan.Variant);
        Assert.Equal("config.json", plan.VariantSource);
    }

    [Fact]
    public async Task The_binary_wins_over_a_stale_state_file()
    {
        var state = new UpdateState { LastInstall = new InstallRecord(QbtVersion.Parse("5.2.4"), Variant.Lt2, Now, "success", 0, null) };

        UpdatePlan plan = await Plan(state: state);

        Assert.Equal(Variant.Lt12, plan.Variant);
    }

    [Fact]
    public async Task The_state_file_is_used_when_the_binary_says_nothing()
    {
        var state = new UpdateState { LastInstall = new InstallRecord(QbtVersion.Parse("5.2.4"), Variant.Lt2, Now, "success", 0, null) };

        UpdatePlan plan = await Plan(WithVariant(Installed524Lt12, null), state: state);

        Assert.Equal(Variant.Lt2, plan.Variant);
        Assert.Equal("state.json", plan.VariantSource);
    }

    [Fact]
    public async Task An_unknown_variant_installs_nothing()
    {
        Assert.Equal(PlanOutcome.VariantUnknown, (await Plan(WithVariant(Installed524Lt12, null))).Outcome);
    }

    [Fact]
    public async Task Up_to_date()
    {
        Assert.Equal(PlanOutcome.UpToDate, (await Plan(server: Server("5.2.4"))).Outcome);
    }

    [Fact]
    public async Task An_inconsistent_install_of_the_same_version_is_repaired()
    {
        InstallInfo broken = Installed524Lt12 with { FileVersion = null, ExeMissing = true, Scan = null };
        var state = new UpdateState { LastInstall = new InstallRecord(QbtVersion.Parse("5.2.4"), Variant.Lt12, Now, "interrupted", null, null) };

        UpdatePlan plan = await Plan(broken, state: state, server: Server("5.2.4"));

        Assert.Equal(PlanOutcome.Ready, plan.Outcome);
        Assert.Equal("qbittorrent_5.2.4_x64_setup.exe", plan.Release!.Installer!.Name);
    }

    [Fact]
    public async Task A_newer_install_is_never_downgraded()
    {
        InstallInfo beta = Installed524Lt12 with { FileVersion = QbtVersion.Parse("5.4.0") };

        Assert.Equal(PlanOutcome.InstalledNewer, (await Plan(beta)).Outcome);
    }

    [Fact]
    public async Task Unsupported_installs_stop_before_any_network_access()
    {
        var server = Server();
        UpdatePlan plan = await Plan(Installed524Lt12 with { Kind = InstallKind.Fork, Notes = ["fork"] }, server: server);

        Assert.Equal(PlanOutcome.Unsupported, plan.Outcome);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task The_off_switch_stops_automatic_runs_only()
    {
        var off = new MachineConfig { Enabled = false };

        Assert.Equal(PlanOutcome.Disabled, (await Plan(config: off)).Outcome);
        Assert.Equal(PlanOutcome.Ready, (await Plan(config: off, manual: true)).Outcome);
    }

    [Fact]
    public async Task Metered_connections_postpone_automatic_runs_only()
    {
        var server = Server();
        Assert.Equal(PlanOutcome.Metered, (await Plan(metered: true, server: server)).Outcome);
        Assert.Empty(server.Requests);

        Assert.Equal(PlanOutcome.Ready, (await Plan(metered: true, manual: true)).Outcome);
        Assert.Equal(PlanOutcome.Ready, (await Plan(metered: true, config: new MachineConfig { SkipOnMeteredConnection = false })).Outcome);
    }

    [Fact]
    public async Task A_skipped_version_is_not_offered_again()
    {
        var user = new UserSettings { SkippedVersion = QbtVersion.Parse("5.3.0") };

        Assert.Equal(PlanOutcome.Skipped, (await Plan(user: user)).Outcome);
        Assert.Equal(PlanOutcome.Ready, (await Plan(user: user, manual: true)).Outcome);
    }

    [Fact]
    public async Task Skipping_an_older_version_does_not_block_a_newer_one()
    {
        var user = new UserSettings { SkippedVersion = QbtVersion.Parse("5.2.9") };

        Assert.Equal(PlanOutcome.Ready, (await Plan(user: user)).Outcome);
    }

    [Fact]
    public async Task Snooze_postpones_until_its_end()
    {
        var user = new UserSettings { SnoozeUntil = Now.AddHours(3) };

        Assert.Equal(PlanOutcome.Snoozed, (await Plan(user: user)).Outcome);
        Assert.Equal(PlanOutcome.Ready, (await Plan(user: user, now: Now.AddHours(4))).Outcome);
    }

    [Fact]
    public async Task Minimum_release_age_uses_the_sourceforge_date()
    {
        // 5.3.0 files were uploaded on 2026-10-20 at 12:51-12:52 UTC.
        var config = new MachineConfig { MinReleaseAgeHours = 72 };

        Assert.Equal(PlanOutcome.TooRecent, (await Plan(config: config)).Outcome);
        Assert.Equal(PlanOutcome.Ready, (await Plan(config: config, now: new DateTimeOffset(2026, 10, 23, 13, 0, 0, TimeSpan.Zero))).Outcome);
    }

    [Fact]
    public async Task An_announced_version_without_files_is_retried_later()
    {
        UpdatePlan plan = await Plan(server: Server("5.3.1"));

        Assert.Equal(PlanOutcome.NotYetAvailable, plan.Outcome);
    }

    [Fact]
    public async Task A_version_without_the_variant_installs_nothing()
    {
        const string Rss = """
            <?xml version="1.0" encoding="utf-8"?>
            <rss version="2.0" xmlns:media="http://video.search.yahoo.com/mrss/"><channel><title>qBittorrent</title>
            <item><title><![CDATA[/qbittorrent-win32/qbittorrent-5.4.0/qbittorrent_5.4.0_x64_setup.exe]]></title><pubDate>Tue, 20 Oct 2026 12:52:43 UT</pubDate><media:content filesize="10"/></item>
            <item><title><![CDATA[/qbittorrent-win32/qbittorrent-5.4.0/qbittorrent_5.4.0_x64_setup.exe.asc]]></title><pubDate>Tue, 20 Oct 2026 12:52:43 UT</pubDate><media:content filesize="10"/></item>
            </channel></rss>
            """;
        FakeHttpHandler server = Server("5.4.0").OnText(SourceForgeClient.FolderRssUrl(QbtVersion.Parse("5.4.0")).AbsoluteUri, Rss, "application/rss+xml");

        UpdatePlan plan = await Plan(server: server);

        Assert.Equal(PlanOutcome.VariantMissing, plan.Outcome);
        Assert.Null(plan.Release!.Installer);
    }

    [Fact]
    public async Task Both_version_sources_down_is_an_error()
    {
        var server = new FakeHttpHandler().OnStatus(Main, HttpStatusCode.BadGateway).OnStatus(Backup, HttpStatusCode.BadGateway);

        await Assert.ThrowsAsync<VersionSourceException>(() => Plan(server: server));
    }
}
