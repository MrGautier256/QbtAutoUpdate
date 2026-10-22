using System.Diagnostics;
using QbtAutoUpdate.Core;
using QbtAutoUpdate.Platform;

namespace QbtAutoUpdate.Flows;

/// <summary>
/// "restart": closes qBittorrent the way an update would, then starts it again, without installing
/// anything. Lets users (and the Windows Sandbox scenario) check the clean shutdown path on their
/// own settings: tray behaviour, exit confirmation, many torrents.
/// </summary>
internal static class RestartFlow
{
    public static async Task<int> RunAsync(FlowContext context, CancellationToken cancellationToken)
    {
        (MachineConfig config, UserSettings user, _) = context.LoadSettings();
        using SessionInstances instances = SessionInstances.Capture(context.Log);

        if (instances.Mine.Count == 0)
        {
            context.Say("qBittorrent is not running in this session.");
            return ExitCodes.Ok;
        }
        if (instances.Mine.FirstOrDefault(i => i.Access != QbtAccess.Controllable) is not null)
        {
            context.Say(instances.BlockingReason!);
            return ExitCodes.Busy;
        }

        foreach (RunningQbt instance in instances.Mine)
            context.Say((context.Command.DryRun ? "[dry-run] Would " : "Going to ") + instances.DescribeClose(instance, user, config) + ".");
        if (context.Command.DryRun)
            return ExitCodes.Ok;

        var clock = Stopwatch.StartNew();
        (bool closedAll, List<RunningQbt> closed, string message) = await instances.CloseAllAsync(context, config, user, cancellationToken).ConfigureAwait(false);
        context.Say(message);

        int started = SessionInstances.Relaunch(closed, null, context.Log);
        if (closed.Count > 0)
            context.Say(started == closed.Count ? $"qBittorrent started again ({clock.Elapsed.TotalSeconds:0} s in total)." : "qBittorrent could not be started again: start it yourself.");
        return closedAll && started == closed.Count ? ExitCodes.Ok : ExitCodes.Busy;
    }
}
