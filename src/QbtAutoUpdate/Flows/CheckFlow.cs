using QbtAutoUpdate.Core;

namespace QbtAutoUpdate.Flows;

/// <summary>
/// The scheduled check: compare versions, then update (auto mode) or offer the update (notify mode).
/// </summary>
internal static class CheckFlow
{
    public static async Task<int> RunAsync(FlowContext context, CancellationToken cancellationToken)
    {
        (MachineConfig config, UserSettings user, UpdateState state) = context.LoadSettings();
        InstallInfo install = context.DetectInstall();
        UpdatePlan plan = await context.CreatePlanner()
            .PlanAsync(install, config, user, state, new PlanOptions(Manual: false), cancellationToken)
            .ConfigureAwait(false);

        context.Say(plan.Message);
        if (!plan.IsReady)
            return ExitCodeFor(plan.Outcome);

        if (config.Mode == UpdateMode.Auto)
            return await UpdateFlow.ExecuteAsync(context, plan, cancellationToken).ConfigureAwait(false);

        // Notifications arrive in phase 4.
        context.Say(context.Command.DryRun
            ? $"[dry-run] Notify mode: would show \"qBittorrent {plan.Latest!.Version} is available\" with Update now / Later / Skip this version."
            : "Notify mode: notifications are not available in this build yet.");
        return context.Command.DryRun ? ExitCodes.Ok : ExitCodes.NotAvailableYet;
    }

    /// <summary>Waiting states are normal outcomes (0); only real problems get a non-zero code.</summary>
    internal static int ExitCodeFor(PlanOutcome outcome) => outcome switch
    {
        PlanOutcome.Unsupported => ExitCodes.Unsupported,
        PlanOutcome.VariantUnknown => ExitCodes.Unsupported,
        _ => ExitCodes.Ok,
    };
}
