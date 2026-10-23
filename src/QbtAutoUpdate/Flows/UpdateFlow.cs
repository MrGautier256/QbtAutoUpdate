using System.Globalization;
using QbtAutoUpdate.Core;
using QbtAutoUpdate.Platform;

namespace QbtAutoUpdate.Flows;

/// <summary>
/// User-side update (spec §4.3): download and verify, close qBittorrent cleanly, back up its settings,
/// hand the installer to the SYSTEM task (or, in development mode, install it directly), start
/// qBittorrent again. Runs without a console (scheduled task, notification click) report through
/// notifications.
/// </summary>
internal static class UpdateFlow
{
    /// <summary>The "update" verb: an explicit request, so the automatic-run gates do not apply.</summary>
    public static async Task<int> RunAsync(FlowContext context, CancellationToken cancellationToken)
    {
        (MachineConfig config, UserSettings user, UpdateState state) = context.LoadSettings();
        InstallInfo install = context.DetectInstall(state);
        UpdatePlan plan = await context.CreatePlanner(user)
            .PlanAsync(install, config, user, state, new PlanOptions(Manual: true), cancellationToken)
            .ConfigureAwait(false);

        context.Say(plan.Message);
        if (!plan.IsReady)
            return CheckFlow.ExitCodeFor(plan.Outcome);

        return await ExecuteAsync(context, plan, config, user, cancellationToken).ConfigureAwait(false);
    }

    /// <param name="rollback">
    /// Installs an older release: only from an elevated "rollback", directly, never through the SYSTEM task
    /// (which refuses downgrades by design).
    /// </param>
    public static async Task<int> ExecuteAsync(FlowContext context, UpdatePlan plan, MachineConfig config, UserSettings user,
        CancellationToken cancellationToken, bool rollback = false)
    {
        bool dryRun = context.Command.DryRun;
        QbtVersion target = plan.Latest!.Version;
        if (rollback && !Elevation.IsElevated)
            throw new InvalidOperationException("A rollback runs elevated.");

        // The SYSTEM "Apply" task installs. Without it (QbtAutoUpdate not set up), only an elevated run can
        // install, by itself: the development mode of D-23. Decided before anything is touched.
        ScheduledTasks? tasks = rollback ? null : ApplyHandoff.TryConnect(context.Log);
        bool developmentMode = tasks is null && Elevation.IsElevated;
        if (!dryRun && tasks is null && !developmentMode)
        {
            context.Say(T.Get("UpdNotSetUp"));
            return ExitCodes.NotAvailableYet;
        }

        ResolvedRelease release = plan.Release!;
        string staging = Path.Combine(context.Paths.StagingDir, release.Version.ToString());
        ClearDirectory(staging);
        try
        {
            // 4. Download and verify first: fail before touching qBittorrent.
            context.Say(T.Format("UpdDownloading", release.Installer!.Name, FormatSize(release.Installer.Size)));
            DownloadedRelease downloaded = await context.Downloader.DownloadAsync(release, staging, cancellationToken).ConfigureAwait(false);
            PgpResult verdict = PgpVerifier.CreateOfficial().VerifyFile(downloaded.InstallerPath, downloaded.SignaturePath, DateTimeOffset.UtcNow);
            if (!verdict.IsValid)
            {
                string failure = DescribeFailure(verdict);
                context.Say(failure);
                context.Log.Error($"Signature check failed for {downloaded.InstallerFileName}: {verdict.Verdict} ({verdict.Detail}).");
                context.NotifyFailure(config, user, failure);
                return ExitCodes.VerificationFailed;
            }
            context.Say(T.Format("UpdSignatureOk", PgpVerifier.OfficialFingerprint[^8..],
                verdict.SignedAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)));

            using SessionInstances instances = SessionInstances.Capture(context.Log);
            if (instances.BlockingReason is { } blocking)
            {
                context.Say(T.Format("UpdNothingChanged", blocking));
                context.NotifyCloseFailed(config, user, target, blocking);
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
                context.NotifyCloseFailed(config, user, target, closeMessage);
                return ExitCodes.Busy;
            }

            if (config.BackupConfig)
                BackUp(context, plan, config, closed);

            // 6. Install: hand over to the SYSTEM task, or (development mode) run the installer from here.
            InstallOutcome outcome = tasks is not null
                ? await ApplyHandoff.InstallAsync(context, tasks, downloaded, cancellationToken).ConfigureAwait(false)
                : new InstallerRunner(
                        PgpVerifier.CreateOfficial(), new SystemInstallEnvironment(), context.Log, ProcessTools.FindQbittorrent,
                        new InstallerProcess(), SystemInfo.NativeArchitecture(), () => DateTimeOffset.UtcNow)
                    .Install(new InstallerFile(downloaded.InstallerPath, downloaded.SignaturePath, downloaded.InstallerFileName), plan.Install,
                        plan.Variant!.Value, allowDowngrade: rollback);
            context.Say(outcome.Message);

            // 7. Start again what was running, whatever the outcome.
            if (closed.Count > 0)
            {
                int started = SessionInstances.Relaunch(closed, plan.Install.ExePath, context.Log);
                context.Say(T.Get(started == closed.Count ? "UpdRestarted" : "UpdRestartFailed"));
            }

            // 8. Report.
            switch (outcome.Status)
            {
                case InstallStatus.Installed:
                    context.NotifyUpdated(config, target, plan.Variant!.Value);
                    break;
                case InstallStatus.Busy:
                    context.NotifyCloseFailed(config, user, target, outcome.Message);
                    break;
                default:
                    context.NotifyFailure(config, user, outcome.Message);
                    break;
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
        PgpVerdict.UnknownKey => T.Get("UpdUnknownKey"),
        PgpVerdict.BadSignature => T.Get("UpdBadSignature"),
        _ => T.Format("UpdSignatureRefused", $"{result.Verdict}: {result.Detail}"),
    };

    internal static string FormatSize(long? bytes) =>
        bytes is { } b ? $"{b / (1024.0 * 1024.0):0.0} MB" : "?";

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
            ? $"[dry-run] Would run {downloaded.InstallerFileName} /S /D={plan.Install.InstallDir} (development mode: elevated, no Apply task)."
            : ApplyHandoff.TryConnect(context.Log) is not null
                ? $"[dry-run] Would hand {downloaded.InstallerFileName} to the SYSTEM task and install it into {plan.Install.InstallDir}."
                : "[dry-run] QbtAutoUpdate is not set up on this PC, so nothing could be installed yet.");
    }

    internal static void BackUp(FlowContext context, UpdatePlan plan, MachineConfig config, List<RunningQbt> closed)
    {
        try
        {
            List<QbtProfile> profiles = ProfilesToBackUp(plan, closed).OfType<QbtProfile>().ToList();
            string? folder = ConfigBackup.Create(profiles, plan.Install.Version!.Value, context.Paths.BackupsDir, config.BackupTorrents, DateTimeOffset.Now, context.Log);
            if (folder is not null)
                context.Say(T.Format("UpdBackedUp", folder));
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

    internal static void ClearDirectory(string path, bool deleteItself = false)
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
