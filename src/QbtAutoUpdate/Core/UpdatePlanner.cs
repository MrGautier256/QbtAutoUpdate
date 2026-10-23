using System.Globalization;

namespace QbtAutoUpdate.Core;

public enum PlanOutcome
{
    Ready,
    Disabled,
    Unsupported,
    Metered,
    UpToDate,
    InstalledNewer,
    Skipped,
    Snoozed,
    VariantUnknown,
    NotYetAvailable,
    VariantMissing,
    TooRecent,
}

public sealed record UpdatePlan(
    PlanOutcome Outcome,
    string Message,
    InstallInfo Install,
    LatestVersion? Latest = null,
    Variant? Variant = null,
    string? VariantSource = null,
    ResolvedRelease? Release = null)
{
    public bool IsReady => Outcome == PlanOutcome.Ready;
}

/// <param name="Manual">
/// The user asked for this run (a click, a terminal command): the on/off switch, metered
/// connections, a skipped version, a snooze and the minimum release age do not apply.
/// </param>
public sealed record PlanOptions(bool Manual);

/// <summary>
/// Steps 1 to 3 of the update sequence (spec §4.3): installation, latest version, gates,
/// variant, installer file. Pure decision logic: nothing is downloaded or changed here.
/// </summary>
public sealed class UpdatePlanner(
    VersionSource versions,
    SourceForgeClient sourceForge,
    ILog log,
    CpuArch machine,
    Func<bool> isMetered,
    Func<DateTimeOffset> clock,
    Func<CancellationToken, Task<Variant?>>? webUiVariant = null)
{
    public async Task<UpdatePlan> PlanAsync(
        InstallInfo install, MachineConfig config, UserSettings user, UpdateState state,
        PlanOptions options, CancellationToken cancellationToken)
    {
        if (!config.Enabled && !options.Manual)
            return new UpdatePlan(PlanOutcome.Disabled, T.Get("PlanDisabled"), install);

        if (!install.IsSupported)
            return new UpdatePlan(PlanOutcome.Unsupported, DescribeUnsupported(install), install);

        if (install.Version is not { } installed)
            return new UpdatePlan(PlanOutcome.Unsupported, T.Get("PlanVersionUnreadable"), install);

        if (config.SkipOnMeteredConnection && !options.Manual && isMetered())
            return new UpdatePlan(PlanOutcome.Metered, T.Get("PlanMetered"), install);

        LatestVersion latest = await versions.GetLatestAsync(cancellationToken).ConfigureAwait(false);
        QbtVersion target = latest.Version;

        if (target < installed)
        {
            return new UpdatePlan(PlanOutcome.InstalledNewer,
                T.Format("PlanInstalledNewer", installed, target), install, latest);
        }

        if (target == installed && !install.IsInconsistent)
            return new UpdatePlan(PlanOutcome.UpToDate, T.Format("PlanUpToDate", installed), install, latest);

        // Skipped by the user (notification) or machine-wide by a rollback: automatic runs leave it alone.
        if (!options.Manual && (user.SkippedVersion == target || config.SkippedVersion == target))
            return new UpdatePlan(PlanOutcome.Skipped, T.Format("PlanSkipped", target), install, latest);

        DateTimeOffset now = clock();
        if (!options.Manual && user.SnoozeUntil > now)
            return new UpdatePlan(PlanOutcome.Snoozed, T.Format("PlanSnoozed", user.SnoozeUntil.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)), install, latest);

        if (await DecideVariantAsync(config, install, state, cancellationToken).ConfigureAwait(false) is not { } decision)
        {
            return new UpdatePlan(PlanOutcome.VariantUnknown, T.Get("PlanVariantUnknown"), install, latest);
        }

        FolderListing listing = await sourceForge.ListAsync(target, cancellationToken).ConfigureAwait(false);
        ResolvedRelease release = ReleaseResolver.Resolve(listing, decision.Variant, machine);
        if (release.UnknownFiles is { Count: > 0 } unknown)
            log.Info($"Ignored unknown files in the {target} folder: {string.Join(", ", unknown)}");

        var plan = new UpdatePlan(PlanOutcome.Ready, "", install, latest, decision.Variant, decision.Source, release);
        switch (release.Status)
        {
            case ResolveStatus.NotYetAvailable:
                return plan with { Outcome = PlanOutcome.NotYetAvailable, Message = T.Format("PlanNotYetAvailable", target, release.Reason) };
            case ResolveStatus.VariantMissing:
                return plan with { Outcome = PlanOutcome.VariantMissing, Message = T.Format("PlanVariantMissing", target, decision.Variant.Describe()) };
        }

        if (config.MinReleaseAgeHours > 0 && !options.Manual)
        {
            DateTimeOffset? published = release.Installer!.Published;
            if (published is null || published.Value.AddHours(config.MinReleaseAgeHours) > now)
            {
                string when = published is null ? T.Get("PlanPublishedUnknown") : published.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
                return plan with { Outcome = PlanOutcome.TooRecent, Message = T.Format("PlanTooRecent", target, when, config.MinReleaseAgeHours) };
            }
        }

        return plan with
        {
            Message = target == installed
                ? T.Format("PlanReinstallAvailable", target, decision.Variant.Describe(), release.Installer!.Name)
                : T.Format("PlanUpdateAvailable", installed, target, decision.Variant.Describe(), release.Installer!.Name),
        };
    }

    /// <summary>
    /// Variant to install, in order: an explicit choice in config.json, the libtorrent series read from
    /// the installed qbittorrent.exe, the WebUI's buildInfo, then the last install recorded in state.json.
    /// The binary comes before state.json because the user may have reinstalled another variant by hand since.
    /// </summary>
    internal async Task<(Variant Variant, string Source)?> DecideVariantAsync(
        MachineConfig config, InstallInfo install, UpdateState state, CancellationToken cancellationToken)
    {
        switch (config.Variant)
        {
            case VariantSetting.Lt2:
                return (Variant.Lt2, "config.json");
            case VariantSetting.Lt12:
                return (Variant.Lt12, "config.json");
        }

        Variant? recorded = state.LastInstall?.Variant;
        if (install.DetectedVariant is { } detected)
        {
            if (recorded is not null && recorded != detected)
                log.Warn($"state.json records {recorded.Value.Describe()} but qbittorrent.exe is {detected.Describe()}; keeping {detected.Describe()}.");
            return (detected, "qbittorrent.exe");
        }

        if (webUiVariant is not null && await webUiVariant(cancellationToken).ConfigureAwait(false) is { } fromWebUi)
            return (fromWebUi, "WebUI buildInfo");

        return recorded is { } fromState ? (fromState, "state.json") : null;
    }

    /// <summary>"1.2.20.0" → libtorrent 1.x, "2.1.2.0" → libtorrent 2.x.</summary>
    public static Variant? VariantFromLibtorrentVersion(string? version) => version switch
    {
        ['1', '.', ..] => Variant.Lt12,
        ['2', '.', ..] => Variant.Lt2,
        _ => null,
    };

    private static string DescribeUnsupported(InstallInfo install) => install.Kind switch
    {
        InstallKind.NotInstalled => T.Get("PlanNotInstalled"),
        _ => install.Notes.Count > 0 ? install.Notes[0] : T.Get("PlanUnsupported"),
    };
}
