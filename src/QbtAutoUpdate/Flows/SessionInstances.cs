using QbtAutoUpdate.Core;
using QbtAutoUpdate.Platform;

namespace QbtAutoUpdate.Flows;

/// <summary>
/// The qBittorrent instances that matter for an update: the ones in this Windows session, which this
/// process closes and restarts, and the ones elsewhere, which block the update.
/// </summary>
internal sealed class SessionInstances : IDisposable
{
    private SessionInstances(List<RunningQbt> mine, List<QbtProcess> elsewhere)
    {
        Mine = mine;
        Elsewhere = elsewhere;
    }

    public List<RunningQbt> Mine { get; }
    public List<QbtProcess> Elsewhere { get; }

    public static SessionInstances Capture(ILog log)
    {
        int session = ProcessTools.CurrentSessionId;
        var elsewhere = ProcessTools.FindQbittorrent().Where(p => p.SessionId != session).ToList();
        return new SessionInstances(ProcessTools.Snapshot(session, log), elsewhere);
    }

    /// <summary>Why the update cannot go ahead without touching anything, or null.</summary>
    public string? BlockingReason
    {
        get
        {
            if (Elsewhere.Count > 0)
            {
                return T.Format("BlockOtherSession", string.Join(", ", Elsewhere.Select(p => p.Id)));
            }
            return Mine.FirstOrDefault(i => i.Access != QbtAccess.Controllable) is { } blocked
                ? blocked.Access == QbtAccess.Elevated
                    ? T.Format("CloseElevated", blocked.Id)
                    : T.Format("BlockOtherUser", blocked.Id)
                : null;
        }
    }

    /// <summary>The WebUI only identifies one instance: with several, Restart Manager is used for all.</summary>
    public bool UseWebUi(UserSettings user) => user.WebUi is not null && Mine.Count == 1;

    public string DescribeClose(RunningQbt instance, UserSettings user, MachineConfig config) =>
        $"close qBittorrent (PID {instance.Id}) through {(UseWebUi(user) ? "its WebUI" : "the Windows Restart Manager")} "
        + $"(up to {config.CloseTimeoutSeconds} s), then start it again with {DescribeArguments(instance.Launch)}";

    public static string DescribeArguments(QbtLaunch launch) =>
        (launch.Arguments.Count == 0 ? "no arguments" : string.Join(' ', launch.Arguments))
        + (launch.DroppedCount > 0 ? $" ({launch.DroppedCount} argument(s) about torrents to add left out)" : "");

    /// <summary>
    /// Closes every instance of this session, one after the other. Stops at the first failure and
    /// returns the instances already closed, so the caller can start them again.
    /// </summary>
    public async Task<(bool Success, List<RunningQbt> Closed, string Message)> CloseAllAsync(
        FlowContext context, MachineConfig config, UserSettings user, CancellationToken cancellationToken)
    {
        var closer = new QbtCloser(context.Log, () => context.CreateWebUi(user));
        var closed = new List<RunningQbt>();
        var messages = new List<string>();
        foreach (RunningQbt instance in Mine)
        {
            CloseResult result = await closer.CloseAsync(instance, UseWebUi(user), TimeSpan.FromSeconds(config.CloseTimeoutSeconds),
                config.AllowForceClose, cancellationToken).ConfigureAwait(false);
            context.Log.Info($"Close PID {instance.Id}: {result.Method?.ToString() ?? "failed"} - {result.Message}");
            if (!result.Closed)
                return (false, closed, result.Message);
            closed.Add(instance);
            messages.Add(result.Message);
        }
        return (true, closed, messages.Count == 0 ? T.Get("CloseNotRunning") : string.Join(" ", messages));
    }

    /// <summary>Starts again, unelevated and with their instance options, the instances that were closed.</summary>
    public static int Relaunch(IEnumerable<RunningQbt> closed, string? fallbackExe, ILog log)
    {
        int started = 0;
        foreach (RunningQbt instance in closed)
        {
            string? exe = instance.Process.Path ?? fallbackExe;
            if (exe is not null && File.Exists(exe) && ProcessLauncher.StartQbittorrent(exe, instance.Launch.Arguments, log))
                started++;
        }
        return started;
    }

    public void Dispose()
    {
        foreach (RunningQbt instance in Mine)
            instance.Dispose();
    }
}
