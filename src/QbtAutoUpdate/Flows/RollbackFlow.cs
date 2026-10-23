using QbtAutoUpdate.Core;
using QbtAutoUpdate.Platform;

namespace QbtAutoUpdate.Flows;

/// <summary>
/// "rollback" (spec §4.2, administrator): reinstalls the release before the installed one, same libtorrent
/// series, signed, from SourceForge. It is the only path that installs an older version. Afterwards
/// automatic runs skip the version that was rolled back (config.json "skippedVersion") until a newer
/// release appears, or until an administrator clears it with "config --skipped-version none".
/// </summary>
internal static class RollbackFlow
{
    public static async Task<int> RunAsync(FlowContext context, CancellationToken cancellationToken)
    {
        if (!Elevation.IsElevated)
            return SetupFlow.RelaunchElevated(context, context.Command.ToArguments());

        (MachineConfig config, UserSettings user, UpdateState state) = context.LoadSettings();
        InstallInfo install = context.DetectInstall(state);
        if (!install.IsSupported || install.Version is not { } installed)
        {
            context.Say(install.Notes.Count > 0 ? install.Notes[0] : T.Get("PlanNotInstalled"));
            return ExitCodes.Unsupported;
        }

        Variant? variant = config.Variant switch
        {
            VariantSetting.Lt2 => Variant.Lt2,
            VariantSetting.Lt12 => Variant.Lt12,
            _ => install.DetectedVariant ?? state.LastInstall?.Variant,
        };
        if (variant is null)
        {
            context.Say(T.Get("PlanVariantUnknown"));
            return ExitCodes.Unsupported;
        }

        IReadOnlyList<QbtVersion> releases = await context.SourceForge.ListReleasesAsync(cancellationToken).ConfigureAwait(false);
        if (Previous(releases, installed) is not { } previous)
        {
            context.Say(T.Get("RbNoPrevious"));
            return ExitCodes.Unsupported;
        }

        FolderListing listing = await context.SourceForge.ListAsync(previous, cancellationToken).ConfigureAwait(false);
        ResolvedRelease release = ReleaseResolver.Resolve(listing, variant.Value, SystemInfo.NativeArchitecture());
        if (release.Status != ResolveStatus.Found)
        {
            context.Say(T.Format("PlanVariantMissing", previous, variant.Value.Describe()));
            return ExitCodes.Unsupported;
        }

        context.Say(T.Format("RbStarting", installed, previous, variant.Value.Describe()));
        var plan = new UpdatePlan(PlanOutcome.Ready, "", install, new LatestVersion(previous, []), variant, "rollback", release);
        int code = await UpdateFlow.ExecuteAsync(context, plan, config, user, cancellationToken, rollback: true).ConfigureAwait(false);
        if (code != ExitCodes.Ok || context.Command.DryRun)
            return code;

        // Keep automatic runs from putting the rolled-back version straight back.
        MachineConfig current = MachineConfig.Load(context.Paths.ConfigFile, []);
        if (File.Exists(context.Paths.ConfigFile))
            (current with { SkippedVersion = installed }).Save(context.Paths.ConfigFile);
        UpdateState latestState = UpdateState.Load(context.Paths.StateFile, []);
        if (Directory.Exists(context.Paths.MachineDataDir))
        {
            (latestState with
            {
                LastInstall = new InstallRecord(previous, variant, DateTimeOffset.Now, "rollback", 0, $"Rolled back from {installed}."),
            }).Save(context.Paths.StateFile);
        }
        context.Say(T.Format("RbDone", previous, variant.Value.Describe(), installed));
        return ExitCodes.Ok;
    }

    /// <summary>The highest stable release older than the installed one.</summary>
    internal static QbtVersion? Previous(IEnumerable<QbtVersion> releases, QbtVersion installed) =>
        releases.Where(v => v < installed).Select(v => (QbtVersion?)v).DefaultIfEmpty().Max();
}
