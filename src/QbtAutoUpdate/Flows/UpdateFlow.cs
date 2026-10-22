using QbtAutoUpdate.Core;
using QbtAutoUpdate.Platform;

namespace QbtAutoUpdate.Flows;

/// <summary>
/// User-side update (spec §4.3). Phase 1 covers download and signature verification; closing
/// qBittorrent, handing over to the SYSTEM task and restarting arrive in phases 2 and 3, so for
/// now the flow only runs with --dry-run.
/// </summary>
internal static class UpdateFlow
{
    /// <summary>The "update" verb: an explicit request, so the automatic-run gates do not apply.</summary>
    public static async Task<int> RunAsync(FlowContext context, CancellationToken cancellationToken)
    {
        (MachineConfig config, UserSettings user, UpdateState state) = context.LoadSettings();
        InstallInfo install = context.DetectInstall();
        UpdatePlan plan = await context.CreatePlanner()
            .PlanAsync(install, config, user, state, new PlanOptions(Manual: true), cancellationToken)
            .ConfigureAwait(false);

        context.Say(plan.Message);
        if (!plan.IsReady)
            return CheckFlow.ExitCodeFor(plan.Outcome);

        return await ExecuteAsync(context, plan, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Carries out a ready plan: download, verify, then (later phases) close, install, restart.</summary>
    public static async Task<int> ExecuteAsync(FlowContext context, UpdatePlan plan, CancellationToken cancellationToken)
    {
        if (!context.Command.DryRun)
        {
            context.Say("Installing is not available in this build yet; run with --dry-run to download and verify only.");
            return ExitCodes.NotAvailableYet;
        }

        ResolvedRelease release = plan.Release!;
        string staging = Path.Combine(context.Paths.StagingDir, release.Version.ToString());
        ClearDirectory(staging);
        try
        {
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

            ReportRunningInstances(context);
            context.Say($"[dry-run] Would hand {downloaded.InstallerFileName} to the SYSTEM task and install it into {plan.Install.InstallDir}.");
            return ExitCodes.Ok;
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

    private static void ReportRunningInstances(FlowContext context)
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        int session = self.SessionId;
        IReadOnlyList<QbtProcess> running = ProcessTools.FindQbittorrent();
        if (running.Count == 0)
        {
            context.Say("[dry-run] qBittorrent is not running: nothing to close or restart.");
            return;
        }
        foreach (QbtProcess process in running)
        {
            context.Say(process.SessionId == session
                ? $"[dry-run] Would close qBittorrent (PID {process.Id}) cleanly, then restart it after the update."
                : $"[dry-run] qBittorrent also runs in another Windows session (PID {process.Id}, session {process.SessionId}): the update would wait for it to close.");
        }
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

    internal static string FormatSize(long? bytes) =>
        bytes is { } b ? $"{b / (1024.0 * 1024.0):0.0} MB" : "size unknown";
}
