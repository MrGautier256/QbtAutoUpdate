using QbtAutoUpdate.Core;
using QbtAutoUpdate.Platform;

namespace QbtAutoUpdate.Flows;

/// <summary>
/// User-side update (spec §4.3): download and verify, close qBittorrent cleanly, back up its
/// settings, install, start it again. Until the SYSTEM task exists (phase 3), installing requires
/// running this command elevated by hand: the "development mode" of the spec.
/// </summary>
internal static class UpdateFlow
{
    /// <summary>The "update" verb: an explicit request, so the automatic-run gates do not apply.</summary>
    public static async Task<int> RunAsync(FlowContext context, CancellationToken cancellationToken)
    {
        (MachineConfig config, UserSettings user, UpdateState state) = context.LoadSettings();
        InstallInfo install = context.DetectInstall();
        UpdatePlan plan = await context.CreatePlanner(user)
            .PlanAsync(install, config, user, state, new PlanOptions(Manual: true), cancellationToken)
            .ConfigureAwait(false);

        context.Say(plan.Message);
        if (!plan.IsReady)
            return CheckFlow.ExitCodeFor(plan.Outcome);

        return await ExecuteAsync(context, plan, config, user, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<int> ExecuteAsync(FlowContext context, UpdatePlan plan, MachineConfig config, UserSettings user, CancellationToken cancellationToken)
    {
        bool dryRun = context.Command.DryRun;
        bool developmentMode = Environment.IsPrivilegedProcess;
        if (!dryRun && !developmentMode)
        {
            context.Say("Installing needs the QbtAutoUpdate scheduled task, which arrives with 'setup'. "
                        + "For development, run this command from an elevated terminal.");
            return ExitCodes.NotAvailableYet;
        }

        ResolvedRelease release = plan.Release!;
        string staging = Path.Combine(context.Paths.StagingDir, release.Version.ToString());
        ClearDirectory(staging);
        try
        {
            // 4. Download and verify first: fail before touching qBittorrent.
            context.Say($"Downloading {release.Installer!.Name} ({FormatSize(release.Installer.Size)}) and its signature...");
            DownloadedRelease downloaded = await context.Downloader.DownloadAsync(release, staging, cancellationToken).ConfigureAwait(false);
            PgpResult verdict = PgpVerifier.CreateOfficial().VerifyFile(downloaded.InstallerPath, downloaded.SignaturePath, DateTimeOffset.UtcNow);
            if (!verdict.IsValid)
            {
                context.Say(DescribeFailure(verdict));
                context.Log.Error($"Signature check failed for {downloaded.InstallerFileName}: {verdict.Verdict} ({verdict.Detail}).");
                return ExitCodes.VerificationFailed;
            }
            context.Say($"Signature OK: {verdict.Detail}, signed {verdict.SignedAt:yyyy-MM-dd HH:mm} UTC.");

            using SessionInstances instances = SessionInstances.Capture(context.Log);
            if (instances.BlockingReason is { } blocking)
            {
                context.Say(blocking + " Nothing was changed.");
                return ExitCodes.Busy;
            }

            if (dryRun)
            {
                DescribeDryRun(context, plan, config, user, instances, downloaded, developmentMode);
                return ExitCodes.Ok;
            }

            // 5. Close qBittorrent cleanly; on failure, put back what was closed and stop.
            (bool closedAll, List<RunningQbt> closed, string closeMessage) = await instances.CloseAllAsync(context, config, user, cancellationToken).ConfigureAwait(false);
            context.Say(closeMessage);
            if (!closedAll)
            {
                SessionInstances.Relaunch(closed, plan.Install.ExePath, context.Log);
                return ExitCodes.Busy;
            }

            if (config.BackupConfig)
                BackUp(context, plan, config, closed);

            // 6. Install. Development mode: this elevated process runs the installer itself.
            InstallOutcome outcome = new InstallerRunner(
                    PgpVerifier.CreateOfficial(), new SystemInstallEnvironment(), context.Log, ProcessTools.FindQbittorrent,
                    new InstallerProcess(), SystemInfo.NativeArchitecture(), () => DateTimeOffset.UtcNow)
                .Install(new InstallerFile(downloaded.InstallerPath, downloaded.SignaturePath, downloaded.InstallerFileName), plan.Install, plan.Variant!.Value);
            context.Say(outcome.Message);

            // 7. Start again what was running, whatever the outcome.
            if (closed.Count > 0)
            {
                int started = SessionInstances.Relaunch(closed, plan.Install.ExePath, context.Log);
                context.Say(started == closed.Count ? "qBittorrent started again." : "qBittorrent could not be started again: start it yourself.");
            }

            return outcome.Status switch
            {
                InstallStatus.Installed => ExitCodes.Ok,
                InstallStatus.Busy => ExitCodes.Busy,
                InstallStatus.Refused => ExitCodes.VerificationFailed,
                _ => ExitCodes.InstallFailed,
            };
        }
        finally
        {
            ClearDirectory(staging, deleteItself: true);
        }
    }

    internal static string DescribeFailure(PgpResult result) => result.Verdict switch
    {
        PgpVerdict.UnknownKey =>
            "The installer is signed with a key QbtAutoUpdate does not know. qBittorrent may have changed its signing key: update QbtAutoUpdate. Nothing was installed.",
        PgpVerdict.BadSignature =>
            "The downloaded installer does not match its signature (altered or corrupted download). Nothing was installed.",
        _ => $"The installer signature was refused ({result.Verdict}: {result.Detail}). Nothing was installed.",
    };

    internal static string FormatSize(long? bytes) =>
        bytes is { } b ? $"{b / (1024.0 * 1024.0):0.0} MB" : "size unknown";

    private static void DescribeDryRun(FlowContext context, UpdatePlan plan, MachineConfig config, UserSettings user,
        SessionInstances instances, DownloadedRelease downloaded, bool developmentMode)
    {
        if (instances.Mine.Count == 0)
            context.Say("[dry-run] qBittorrent is not running: nothing to close or restart.");
        foreach (RunningQbt instance in instances.Mine)
            context.Say("[dry-run] Would " + instances.DescribeClose(instance, user, config) + ".");

        if (config.BackupConfig)
        {
            foreach (QbtProfile? profile in ProfilesToBackUp(plan, instances.Mine))
                context.Say(profile is null
                    ? "[dry-run] A relative --profile cannot be located: its settings would not be backed up."
                    : $"[dry-run] Would back up the settings in {profile.ConfigDir}{(config.BackupTorrents ? " and BT_backup" : "")}.");
        }

        context.Say(developmentMode
            ? $"[dry-run] Would run {downloaded.InstallerFileName} /S /D={plan.Install.InstallDir} (development mode: elevated)."
            : $"[dry-run] Would hand {downloaded.InstallerFileName} to the SYSTEM task and install it into {plan.Install.InstallDir}.");
    }

    private static void BackUp(FlowContext context, UpdatePlan plan, MachineConfig config, List<RunningQbt> closed)
    {
        try
        {
            List<QbtProfile> profiles = ProfilesToBackUp(plan, closed).OfType<QbtProfile>().ToList();
            string? folder = ConfigBackup.Create(profiles, plan.Install.Version!.Value, context.Paths.BackupsDir, config.BackupTorrents, DateTimeOffset.Now, context.Log);
            if (folder is not null)
                context.Say($"Settings backed up to {folder}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The installer does not touch the profile: a failed backup is not a reason to stop.
            context.Log.Warn($"Settings backup failed: {ex.Message}");
        }
    }

    /// <summary>The profiles of the closed instances, or the default profile when none was running.</summary>
    private static IEnumerable<QbtProfile?> ProfilesToBackUp(UpdatePlan plan, IReadOnlyList<RunningQbt> instances)
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        IEnumerable<QbtLaunch> launches = instances.Count == 0 ? [QbtLaunch.Default] : instances.Select(i => i.Launch);
        return launches
            .Select(l => l.ResolveProfile(plan.Install.InstallDir, appData, localAppData, Directory.Exists))
            .DistinctBy(p => p?.ConfigDir, StringComparer.OrdinalIgnoreCase);
    }

    private static void ClearDirectory(string path, bool deleteItself = false)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            if (!deleteItself)
                Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
