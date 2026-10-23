using System.Globalization;
using QbtAutoUpdate.Core;
using QbtAutoUpdate.Platform;

namespace QbtAutoUpdate.Flows;

/// <summary>Setting changes given on the command line of "setup" or "config".</summary>
internal sealed record ConfigChanges(
    bool? Enabled, UpdateMode? Mode, int? IntervalHours, VariantSetting? Variant,
    QbtVersion? SkippedVersion = null, bool ClearSkippedVersion = false)
{
    public bool IsEmpty => Enabled is null && Mode is null && IntervalHours is null && Variant is null
                           && SkippedVersion is null && !ClearSkippedVersion;

    public MachineConfig ApplyTo(MachineConfig config) => config with
    {
        Enabled = Enabled ?? config.Enabled,
        Mode = Mode ?? config.Mode,
        CheckIntervalHours = IntervalHours ?? config.CheckIntervalHours,
        Variant = Variant ?? config.Variant,
        SkippedVersion = ClearSkippedVersion ? null : SkippedVersion ?? config.SkippedVersion,
    };

    public static ConfigChanges? Parse(CommandLine command, out string? error)
    {
        error = null;
        bool? enabled = command.Get("--enabled") switch
        {
            null => null,
            "on" or "true" => true,
            "off" or "false" => false,
            var other => Fail<bool>(ref error, $"--enabled must be on or off, not '{other}'."),
        };
        UpdateMode? mode = command.Get("--mode") switch
        {
            null => null,
            "auto" => UpdateMode.Auto,
            "notify" => UpdateMode.Notify,
            var other => Fail<UpdateMode>(ref error, $"--mode must be auto or notify, not '{other}'."),
        };
        VariantSetting? variant = command.Get("--variant") switch
        {
            null => null,
            "auto" => VariantSetting.Auto,
            "lt2" => VariantSetting.Lt2,
            "lt12" => VariantSetting.Lt12,
            var other => Fail<VariantSetting>(ref error, $"--variant must be auto, lt2 or lt12, not '{other}'."),
        };
        int? interval = null;
        if (command.Get("--interval") is { } text)
        {
            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int hours) && hours is >= 1 and <= 168)
                interval = hours;
            else
                error ??= "--interval must be a number of hours between 1 and 168.";
        }
        QbtVersion? skipped = null;
        bool clearSkipped = false;
        switch (command.Get("--skipped-version"))
        {
            case null:
                break;
            case "none":
                clearSkipped = true;
                break;
            case var value when QbtVersion.TryParse(value, out QbtVersion version):
                skipped = version;
                break;
            case var value:
                error ??= $"--skipped-version must be a version such as 5.3.0, or none, not '{value}'.";
                break;
        }
        return error is null ? new ConfigChanges(enabled, mode, interval, variant, skipped, clearSkipped) : null;
    }

    private static T? Fail<T>(ref string? error, string message) where T : struct
    {
        error ??= message;
        return null;
    }
}

/// <summary>"config": changes machine settings after setup and updates the scheduled tasks accordingly.</summary>
internal static class ConfigFlow
{
    public static int Run(FlowContext context)
    {
        if (ConfigChanges.Parse(context.Command, out string? error) is not { } changes)
        {
            context.Say(error!);
            return ExitCodes.Usage;
        }

        if (changes.IsEmpty)
        {
            MachineConfig current = MachineConfig.Load(context.Paths.ConfigFile, []);
            context.Say(File.Exists(context.Paths.ConfigFile) ? context.Paths.ConfigFile : "(defaults: QbtAutoUpdate is not set up)");
            context.Say(current.ToJson());
            return ExitCodes.Ok;
        }

        if (!File.Exists(context.Paths.InstalledExe))
        {
            context.Say("QbtAutoUpdate is not set up on this PC: run setup first.");
            return ExitCodes.Unsupported;
        }

        return Elevation.IsElevated ? Apply(context, changes) : SetupFlow.RelaunchElevated(context, context.Command.ToArguments());
    }

    public static int Apply(FlowContext context, ConfigChanges changes)
    {
        MachineConfig config = changes.ApplyTo(MachineConfig.Load(context.Paths.ConfigFile, []));
        config.Save(context.Paths.ConfigFile);
        SetupFlow.RegisterTasks(context.Paths, config);
        context.Say($"Settings saved: automatic updates {(config.Enabled ? "on" : "off")}, {SetupFlow.Describe(config.Mode)}, every {config.CheckIntervalHours} h.");
        return ExitCodes.Ok;
    }
}
