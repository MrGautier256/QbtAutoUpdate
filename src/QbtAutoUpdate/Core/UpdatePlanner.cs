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
    Func<DateTimeOffset> clock)
{
    public async Task<UpdatePlan> PlanAsync(
        InstallInfo install, MachineConfig config, UserSettings user, UpdateState state,
        PlanOptions options, CancellationToken cancellationToken)
    {
        if (!config.Enabled && !options.Manual)
            return new UpdatePlan(PlanOutcome.Disabled, "Automatic updates are turned off.", install);

        if (!install.IsSupported)
            return new UpdatePlan(PlanOutcome.Unsupported, DescribeUnsupported(install), install);

        if (install.Version is not { } installed)
            return new UpdatePlan(PlanOutcome.Unsupported, "The installed qBittorrent version could not be read.", install);

        if (config.SkipOnMeteredConnection && !options.Manual && isMetered())
            return new UpdatePlan(PlanOutcome.Metered, "The connection is metered; the check is postponed.", install);

        LatestVersion latest = await versions.GetLatestAsync(cancellationToken).ConfigureAwait(false);
        QbtVersion target = latest.Version;

        if (target < installed)
        {
            return new UpdatePlan(PlanOutcome.InstalledNewer,
                $"The installed version {installed} is newer than the latest release {target} (beta or development build?).", install, latest);
        }

        if (target == installed && !install.IsInconsistent)
            return new UpdatePlan(PlanOutcome.UpToDate, $"qBittorrent {installed} is up to date.", install, latest);

        if (!options.Manual && user.SkippedVersion == target)
            return new UpdatePlan(PlanOutcome.Skipped, $"Version {target} was skipped by the user.", install, latest);

        DateTimeOffset now = clock();
        if (!options.Manual && user.SnoozeUntil > now)
            return new UpdatePlan(PlanOutcome.Snoozed, $"Reminder postponed until {user.SnoozeUntil:yyyy-MM-dd HH:mm}.", install, latest);

        if (DecideVariant(config, install, state) is not { } decision)
        {
            return new UpdatePlan(PlanOutcome.VariantUnknown,
                "The libtorrent series of the installed qBittorrent is unknown; choose it in the settings (lt2 or lt12).", install, latest);
        }

        FolderListing listing = await sourceForge.ListAsync(target, cancellationToken).ConfigureAwait(false);
        ResolvedRelease release = ReleaseResolver.Resolve(listing, decision.Variant, machine);
        if (release.UnknownFiles is { Count: > 0 } unknown)
            log.Info($"Ignored unknown files in the {target} folder: {string.Join(", ", unknown)}");

        var plan = new UpdatePlan(PlanOutcome.Ready, "", install, latest, decision.Variant, decision.Source, release);
        switch (release.Status)
        {
            case ResolveStatus.NotYetAvailable:
                return plan with { Outcome = PlanOutcome.NotYetAvailable, Message = $"qBittorrent {target} is announced but not downloadable yet: {release.Reason}." };
            case ResolveStatus.VariantMissing:
                return plan with { Outcome = PlanOutcome.VariantMissing, Message = $"qBittorrent {target} is out, but {release.Reason}. Nothing will be installed." };
        }

        if (config.MinReleaseAgeHours > 0 && !options.Manual)
        {
            DateTimeOffset? published = release.Installer!.Published;
            if (published is null || published.Value.AddHours(config.MinReleaseAgeHours) > now)
            {
                string when = published is null ? "unknown" : $"{published.Value.ToUniversalTime():yyyy-MM-dd HH:mm} UTC";
                return plan with
                {
                    Outcome = PlanOutcome.TooRecent,
                    Message = $"qBittorrent {target} was published {when}; waiting {config.MinReleaseAgeHours} h before installing it.",
                };
            }
        }

        string action = target == installed ? "Reinstall" : "Update";
        return plan with { Message = $"{action} available: {installed} -> {target} ({decision.Variant.Describe()}, {release.Installer!.Name})." };
    }

    /// <summary>
    /// Variant to install, in order: an explicit choice in config.json, the libtorrent series read from
    /// the installed qbittorrent.exe, then the last install recorded in state.json. The binary comes
    /// before state.json because the user may have reinstalled another variant by hand since.
    /// </summary>
    internal (Variant Variant, string Source)? DecideVariant(MachineConfig config, InstallInfo install, UpdateState state)
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

        return recorded is { } fromState ? (fromState, "state.json") : null;
    }

    private static string DescribeUnsupported(InstallInfo install) => install.Kind switch
    {
        InstallKind.NotInstalled => "qBittorrent is not installed.",
        _ => install.Notes.Count > 0 ? install.Notes[0] : $"Unsupported installation ({install.Kind}).",
    };
}
