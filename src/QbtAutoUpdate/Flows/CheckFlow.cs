using QbtAutoUpdate.Core;

namespace QbtAutoUpdate.Flows;

/// <summary>
/// The scheduled check: compare versions, then update (auto mode) or offer the update with a
/// notification (notify mode).
/// </summary>
internal static class CheckFlow
{
    /// <summary>An unanswered "available" notification is shown again after this long.</summary>
    private static readonly TimeSpan RemindAfter = TimeSpan.FromHours(24);

    public static async Task<int> RunAsync(FlowContext context, CancellationToken cancellationToken)
    {
        (MachineConfig config, UserSettings user, UpdateState state) = context.LoadSettings();
        InstallInfo install = context.DetectInstall(state);
        UpdatePlan plan = await context.CreatePlanner(user)
            .PlanAsync(install, config, user, state, new PlanOptions(Manual: false), cancellationToken)
            .ConfigureAwait(false);

        context.Say(plan.Message);

        // A release without our variant: say it once per version, install nothing.
        if (plan.Outcome == PlanOutcome.VariantMissing && !AlreadyNotified(user, plan.Latest!.Version))
        {
            context.NotifyFailure(config, user, plan.Message);
            Remember(context, plan.Latest.Version);
        }

        if (!plan.IsReady)
            return ExitCodeFor(plan.Outcome);

        if (config.Mode == UpdateMode.Auto)
            return await UpdateFlow.ExecuteAsync(context, plan, config, user, cancellationToken).ConfigureAwait(false);

        QbtVersion target = plan.Latest!.Version;
        if (context.Command.DryRun)
        {
            context.Say($"[dry-run] Notify mode: would show \"qBittorrent {target} is available\" with Update now / Later / Skip this version.");
            return ExitCodes.Ok;
        }
        if (AlreadyNotified(user, target))
        {
            context.Log.Info($"qBittorrent {target} was already announced at {user.NotifiedAt:u}; not repeating it yet.");
            return ExitCodes.Ok;
        }

        context.NotifyAvailable(config, user, plan);
        Remember(context, target);
        return ExitCodes.Ok;
    }

    /// <summary>Waiting states are normal outcomes (0); only real problems get a non-zero code.</summary>
    internal static int ExitCodeFor(PlanOutcome outcome) => outcome switch
    {
        PlanOutcome.Unsupported => ExitCodes.Unsupported,
        PlanOutcome.VariantUnknown => ExitCodes.Unsupported,
        _ => ExitCodes.Ok,
    };

    private static bool AlreadyNotified(UserSettings user, QbtVersion version) =>
        user.NotifiedVersion == version && user.NotifiedAt > DateTimeOffset.Now - RemindAfter;

    private static void Remember(FlowContext context, QbtVersion version)
    {
        if (!context.ToastsEnabled)
            return;
        // Reloaded: TokenFor may just have written the file.
        UserSettings current = UserSettings.Load(context.Paths.UserConfigFile, []);
        (current with { NotifiedVersion = version, NotifiedAt = DateTimeOffset.Now }).Save(context.Paths.UserConfigFile);
    }
}
