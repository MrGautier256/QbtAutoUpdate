using System.Globalization;
using Microsoft.Win32;
using QbtAutoUpdate.Core;
using QbtAutoUpdate.Platform;

namespace QbtAutoUpdate.Flows;

/// <summary>Read-only report: installation, latest release, settings, last result, log locations.</summary>
internal static class StatusFlow
{
    public static async Task<int> RunAsync(FlowContext context, CancellationToken cancellationToken)
    {
        TextWriter o = context.Output;
        (MachineConfig config, UserSettings user, UpdateState state) = context.LoadSettings();
        InstallInfo install = context.DetectInstall(state);

        o.WriteLine($"QbtAutoUpdate {SafeHttpClient.ProductVersion}");
        o.WriteLine();
        o.WriteLine("qBittorrent");
        WriteInstall(o, install);
        if (install.Kind != InstallKind.NotInstalled)
            WriteRunning(o, context);

        o.WriteLine();
        o.WriteLine("Latest release");
        int exitCode = ExitCodes.Ok;
        try
        {
            // Manual: report what an explicit update would do, whatever the automatic-run gates say.
            UpdatePlan plan = await context.CreatePlanner(user)
                .PlanAsync(install, config, user, state, new PlanOptions(Manual: true), cancellationToken)
                .ConfigureAwait(false);
            WriteLatest(o, plan);
            context.Log.Info($"status: {plan.Outcome} {plan.Message}");
        }
        catch (VersionSourceException ex)
        {
            Row(o, "Version", "unavailable: " + string.Join("; ", ex.Sources.Select(s => $"{s.Url.Host}: {s.Error}")));
            exitCode = ExitCodes.SourceUnavailable;
        }

        o.WriteLine();
        o.WriteLine("QbtAutoUpdate");
        WriteSettings(o, context, config, user, state);
        return install.Kind == InstallKind.Installed || install.Kind == InstallKind.NotInstalled ? exitCode : ExitCodes.Unsupported;
    }

    private static void WriteInstall(TextWriter o, InstallInfo install)
    {
        string kind = install.Kind switch
        {
            InstallKind.NotInstalled => "not installed",
            InstallKind.Installed => "official installer",
            InstallKind.Fork => "Enhanced Edition fork (not handled)",
            InstallKind.NotOfficial => "not an official build (not handled)",
            InstallKind.Portable => "portable (not handled)",
            InstallKind.Legacy32Bit => "32-bit installation (not handled)",
            _ => install.Kind.ToString(),
        };
        string view = install.RegistryView switch
        {
            RegistryView.Registry32 => ", registry 32-bit view",
            RegistryView.Registry64 => ", registry 64-bit view",
            _ => "",
        };
        Row(o, "Installation", install.InstallDir is null ? kind : $"{install.InstallDir} ({kind}{view})");
        if (install.Kind == InstallKind.NotInstalled)
            return;

        string version = install.Version?.ToString() ?? "unknown";
        if (install.IsInconsistent && install.RegistryVersion is { } registry && install.FileVersion is { } file)
            version += $" (registry says {registry})";
        Row(o, "Version", version);

        if (install.Scan is { } scan)
        {
            string markers = string.Join(", ", scan.LibtorrentVersions.Select(v => "libtorrent/" + v).Concat(scan.PeerFingerprints));
            Row(o, "Variant", scan.Variant is { } variant
                ? $"{variant.Describe()} ({markers})"
                : $"unknown ({(markers.Length > 0 ? markers : "no libtorrent marker found")})");
        }

        foreach (string note in install.Notes)
            Row(o, "Note", note);
    }

    private static void WriteRunning(TextWriter o, FlowContext context)
    {
        using SessionInstances instances = SessionInstances.Capture(context.Log);
        if (instances.Mine.Count == 0 && instances.Elsewhere.Count == 0)
            Row(o, "Running", "no");

        foreach (RunningQbt instance in instances.Mine)
        {
            string access = instance.Access switch
            {
                QbtAccess.Controllable => "can be closed cleanly",
                QbtAccess.Elevated => "runs as administrator: cannot be closed by QbtAutoUpdate",
                QbtAccess.OtherUser => "belongs to another user",
                _ => "cannot be inspected",
            };
            Row(o, "Running", $"PID {instance.Id}, this session ({access})");
            if (instance.Access == QbtAccess.Controllable)
                Row(o, "Restart with", SessionInstances.DescribeArguments(instance.Launch));
        }
        foreach (QbtProcess other in instances.Elsewhere)
            Row(o, "Running", $"PID {other.Id}, session {other.SessionId} (another session: blocks updates)");
    }

    private static void WriteLatest(TextWriter o, UpdatePlan plan)
    {
        if (plan.Latest is { } latest)
        {
            string sources = string.Join(", ", latest.Sources.Select(s => $"{s.Url.Host} {(s.Version?.ToString() ?? "failed")}"));
            Row(o, "Version", $"{latest.Version} ({sources})");
        }

        if (plan.Release?.Installer is { } installer)
        {
            string published = installer.Published is { } date
                ? $", published {date.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC"
                : "";
            Row(o, "Target file", $"{installer.Name} ({UpdateFlow.FormatSize(installer.Size)}{published})");
            Row(o, "Variant from", plan.VariantSource ?? "?");
        }

        Row(o, "Status", plan.Message);
    }

    private static void WriteSettings(TextWriter o, FlowContext context, MachineConfig config, UserSettings user, UpdateState state)
    {
        AppPaths paths = context.Paths;
        bool installed = File.Exists(paths.InstalledExe);
        Row(o, "Setup", installed ? $"installed ({paths.InstalledExe})" : "not installed (double-click QbtAutoUpdate.exe or run setup)");
        if (installed)
        {
            try
            {
                ScheduledTasks tasks = ScheduledTasks.Connect();
                Row(o, "Check task", DescribeTask(tasks.GetInfo(ScheduledTasks.CheckTask)));
                Row(o, "Apply task", DescribeTask(tasks.GetInfo(ScheduledTasks.ApplyTask)));
            }
            catch (Exception ex) when (ScheduledTasks.IsFailure(ex))
            {
                Row(o, "Tasks", "unreadable: " + ex.Message);
            }
        }

        string configSource = File.Exists(paths.ConfigFile) ? paths.ConfigFile : "defaults, no config.json";
        Row(o, "Settings", configSource);
        Row(o, "Automatic", config.Enabled ? $"on, every {config.CheckIntervalHours} h" : "off");
        Row(o, "Mode", config.Mode == UpdateMode.Auto ? "auto" : "notify");
        Row(o, "Variant", config.Variant switch { VariantSetting.Lt2 => "libtorrent 2.x (forced)", VariantSetting.Lt12 => "libtorrent 1.2 (forced)", _ => "keep the installed one" });
        if (config.SkippedVersion is { } rolledBack)
            Row(o, "Held back", $"{rolledBack} (rollback; clear with: config --skipped-version none)");
        Row(o, "Notifications", config.Notifications switch { NotificationLevel.Errors => "errors only", NotificationLevel.None => "off", _ => "all" });
        Row(o, "Close timeout", $"{config.CloseTimeoutSeconds} s{(config.AllowForceClose ? ", then terminate (allowForceClose)" : "")}");
        Row(o, "Backup", config.BackupConfig ? $"settings{(config.BackupTorrents ? " and BT_backup" : "")}, last {ConfigBackup.Keep} kept in {paths.BackupsDir}" : "off");
        Row(o, "WebUI", user.WebUi is { } webUi ? $"{webUi.Url} ({webUi.AuthenticationKind})" : "not configured (Restart Manager only)");
        if (user.SkippedVersion is { } skipped)
            Row(o, "Skipped", skipped.ToString());
        if (user.SnoozeUntil is { } snooze && snooze > DateTimeOffset.Now)
            Row(o, "Snoozed until", snooze.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));

        Row(o, "Last install", state.LastInstall is { } last
            ? $"{last.Version} {last.Variant?.Describe()} on {last.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}: {last.Result}{(last.Message is null ? "" : " - " + last.Message)}"
            : "none");
        if (state.LastAttempt is { } attempt)
            Row(o, "Last attempt", $"{attempt.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}: {attempt.Result} - {attempt.Message}");
        if (state.BusySince is { } busy)
            Row(o, "Waiting since", $"{busy.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} (qBittorrent was running)");
        Row(o, "Logs", paths.UserLogsDir);
        Row(o, "", paths.MachineLogsDir);
    }

    private static string DescribeTask(ScheduledTaskInfo? task) => task is null
        ? "missing (run setup again)"
        : $"{(task.Enabled ? "enabled" : "disabled")}{(task.Running ? ", running" : "")}"
          + (task.LastRun is { } run ? $", last run {run.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} (result 0x{task.LastResult:X})" : ", never run");

    private static void Row(TextWriter o, string label, string value) => o.WriteLine($"  {label,-14} {value}");
}
