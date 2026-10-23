using System.Runtime.InteropServices;
using QbtAutoUpdate.Core;
using QbtAutoUpdate.Platform;

namespace QbtAutoUpdate.Flows;

/// <summary>
/// User side of the install step (spec §4.3, steps 6): drop the verified installer and its signature
/// in the inbox under a unique request id, start the SYSTEM "Apply" task (allowed for every user by the
/// task's security descriptor: no UAC prompt), then wait for state.json to answer that request.
/// </summary>
internal static class ApplyHandoff
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);
    /// <summary>How long a task that is no longer running may take to show its answer in state.json.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);

    /// <summary>The task service when the Apply task exists, otherwise null (QbtAutoUpdate not set up).</summary>
    public static ScheduledTasks? TryConnect(ILog log)
    {
        try
        {
            ScheduledTasks tasks = ScheduledTasks.Connect();
            return tasks.Exists(ScheduledTasks.ApplyTask) ? tasks : null;
        }
        catch (Exception ex) when (ScheduledTasks.IsFailure(ex))
        {
            log.Warn($"Task Scheduler unavailable: {ex.Message}");
            return null;
        }
    }

    public static async Task<InstallOutcome> InstallAsync(FlowContext context, ScheduledTasks tasks, DownloadedRelease downloaded, CancellationToken cancellationToken)
    {
        string requestId = Inbox.NewRequestId();
        string installer = Path.Combine(context.Paths.InboxDir, Inbox.FileNameFor(requestId, downloaded.InstallerFileName));
        string signature = installer + ".asc";
        try
        {
            File.Copy(downloaded.InstallerPath, installer);
            File.Copy(downloaded.SignaturePath, signature);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(installer);
            TryDelete(signature);
            return new InstallOutcome(InstallStatus.InstallerFailed, T.Format("UpdHandOverFailed", ex.Message));
        }

        context.Say(T.Get("UpdInstallingTask"));
        DateTime started = DateTime.UtcNow;
        tasks.Run(ScheduledTasks.ApplyTask);

        ApplyAttempt? answer = await WaitForAnswerAsync(context, tasks, requestId, started, cancellationToken).ConfigureAwait(false);
        if (answer is null)
        {
            // Not consumed: take our files back so they are not installed later by surprise.
            TryDelete(installer);
            TryDelete(signature);
            int lastResult = tasks.GetInfo(ScheduledTasks.ApplyTask)?.LastResult ?? 0;
            return new InstallOutcome(InstallStatus.InstallerFailed,
                T.Format("UpdNoAnswer", lastResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture), context.Paths.MachineLogsDir));
        }

        InstallStatus status = ApplyService.StatusFromResult(answer.Result) ?? InstallStatus.InstallerFailed;
        return new InstallOutcome(status, answer.Message, answer.ExitCode);
    }

    private static async Task<ApplyAttempt?> WaitForAnswerAsync(FlowContext context, ScheduledTasks tasks, string requestId, DateTime started, CancellationToken cancellationToken)
    {
        DateTime? stoppedAt = null;
        while (DateTime.UtcNow - started < Timeout)
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);

            UpdateState state = UpdateState.Load(context.Paths.StateFile, []);
            if (state.LastAttempt is { } attempt && attempt.RequestId == requestId && attempt.Result != ApplyAttempt.Installing)
                return attempt;

            bool running = tasks.GetInfo(ScheduledTasks.ApplyTask)?.Running ?? false;
            if (running)
            {
                stoppedAt = null;
            }
            else if (DateTime.UtcNow - started > Grace)
            {
                // Not running and no answer for our request: it ended, or never picked our files.
                stoppedAt ??= DateTime.UtcNow;
                if (DateTime.UtcNow - stoppedAt > Grace)
                    return null;
            }
        }
        context.Log.Error($"No answer from the Apply task within {Timeout.TotalMinutes:0} minutes.");
        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
