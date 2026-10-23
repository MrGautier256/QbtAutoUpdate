using System.Diagnostics;
using QbtAutoUpdate.Core;

namespace QbtAutoUpdate.Flows;

/// <summary>
/// A click on a notification button: Windows starts "QbtAutoUpdate.exe qbtautoupdate:&lt;action&gt;?…"
/// through the protocol registered by setup, even long after the check that showed it has ended.
/// Actions without the user's secret token are ignored.
/// </summary>
internal static class ActivationFlow
{
    private static readonly TimeSpan Later = TimeSpan.FromHours(24);

    public static async Task<int> RunAsync(FlowContext context, CancellationToken cancellationToken)
    {
        (MachineConfig config, UserSettings user, UpdateState state) = context.LoadSettings();
        ToastAction? action = ToastAction.Parse(context.Command.Get("--uri"));
        if (action is null || !action.IsAuthorizedBy(user.NotificationToken))
        {
            context.Log.Warn("Ignored a notification action that was malformed or did not carry this user's token.");
            return ExitCodes.Usage;
        }
        context.Log.Info($"Notification action: {action.Action} {action.Version}");

        switch (action.Action)
        {
            case "later":
                (user with { SnoozeUntil = DateTimeOffset.Now + Later }).Save(context.Paths.UserConfigFile);
                return ExitCodes.Ok;

            case "skip" when action.Version is { } version:
                (user with { SkippedVersion = version }).Save(context.Paths.UserConfigFile);
                return ExitCodes.Ok;

            case "logs":
                Directory.CreateDirectory(context.Paths.UserLogsDir);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{context.Paths.UserLogsDir}\"") { UseShellExecute = true })?.Dispose();
                return ExitCodes.Ok;

            case "update":
                InstallInfo install = context.DetectInstall(state);
                UpdatePlan plan = await context.CreatePlanner(user)
                    .PlanAsync(install, config, user, state, new PlanOptions(Manual: true), cancellationToken)
                    .ConfigureAwait(false);
                if (!plan.IsReady)
                {
                    context.NotifyInfo(config, T.Get("ToastNothingToDoTitle"), plan.Message);
                    return CheckFlow.ExitCodeFor(plan.Outcome);
                }
                return await UpdateFlow.ExecuteAsync(context, plan, config, user, cancellationToken).ConfigureAwait(false);

            default:
                return ExitCodes.Usage;
        }
    }
}
