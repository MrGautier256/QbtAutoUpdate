using System.Diagnostics;
using System.Globalization;
using QbtAutoUpdate.Core;
using QbtAutoUpdate.Platform;

namespace QbtAutoUpdate.Flows;

/// <summary>
/// The windows behind a double-click (DECISIONS D-14): a setup window when QbtAutoUpdate is not
/// installed, a settings window once it is. Administrator actions relaunch the exe with "runas".
/// </summary>
internal static class UiFlow
{
    private const int InstallButton = 200;
    private const int RadioAuto = 101;
    private const int RadioNotify = 102;
    private const int RadioLt2 = 111;
    private const int RadioLt12 = 112;
    private const int CheckNow = 301;
    private const int ToggleEnabled = 302;
    private const int ToggleMode = 303;
    private const int OpenLogs = 304;
    private const int Uninstall = 305;
    private const int Rollback = 306;

    /// <summary>Double-click without arguments.</summary>
    public static int Run(FlowContext context)
    {
        AppPaths paths = context.Paths;
        bool installed = File.Exists(paths.InstalledExe) && File.Exists(paths.ConfigFile);
        if (!installed)
            return Setup(context);
        if (!ApplyFlow.SamePath(Environment.ProcessPath, paths.InstalledExe) && IsNewerThan(paths.InstalledExe))
            return Setup(context, upgrade: true);
        return Settings(context);
    }

    public static int Setup(FlowContext context, bool upgrade = false)
    {
        (MachineConfig config, _, UpdateState state) = context.LoadSettings();
        InstallInfo install = context.DetectInstall(state);
        bool configured = File.Exists(context.Paths.ConfigFile);

        TaskDialogResult choice = TaskDialog.Show(new TaskDialogPage
        {
            MainInstruction = upgrade ? T.Format("UiSetupUpgradeTitle", SafeHttpClient.ProductVersion) : T.Get("UiSetupTitle"),
            Content = DescribeInstall(install) + "\n\n" + T.Get("UiSetupBody"),
            Icon = TaskDialogIcon.Information,
            RadioButtons =
            [
                new(RadioAuto, T.Get("UiModeAuto")),
                new(RadioNotify, T.Get("UiModeNotify")),
            ],
            DefaultRadioButton = configured && config.Mode == UpdateMode.Notify ? RadioNotify : RadioAuto,
            Buttons = [new(InstallButton, T.Get(upgrade ? "UiSetupUpgradeButton" : "UiSetupInstallButton"), RequiresElevation: !Elevation.IsElevated)],
            DefaultButton = InstallButton,
            Cancel = true,
            Footer = T.Get("UiSetupFooter"),
        });
        if (choice.Button != InstallButton)
            return ExitCodes.Ok;

        List<string> arguments = ["setup", "--quiet", "--mode", choice.RadioButton == RadioNotify ? "notify" : "auto"];
        if (install.IsSupported && install.DetectedVariant is null && config.Variant == VariantSetting.Auto)
        {
            TaskDialogResult variant = TaskDialog.Show(new TaskDialogPage
            {
                MainInstruction = T.Get("UiVariantTitle"),
                Content = T.Get("UiVariantBody"),
                RadioButtons =
                [
                    new(RadioLt2, T.Get("UiVariantLt2")),
                    new(RadioLt12, T.Get("UiVariantLt12")),
                ],
                DefaultRadioButton = RadioLt2,
                Ok = true,
                Cancel = true,
            });
            if (variant.Button != TaskDialogPage.IdOk)
                return ExitCodes.Ok;
            arguments.AddRange(["--variant", variant.RadioButton == RadioLt12 ? "lt12" : "lt2"]);
        }

        int code = RunAsAdministrator(context, arguments);
        if (code == Elevation.ErrorCancelled)
            return code;
        Inform(code == 0
            ? new TaskDialogPage { MainInstruction = T.Get("UiSetupDoneTitle"), Content = T.Get("UiSetupDoneBody"), Icon = TaskDialogIcon.Information, Ok = true }
            : Failure(T.Get("UiSetupFailed"), code, context.Paths.MachineLogsDir));
        return code;
    }

    public static int Settings(FlowContext context)
    {
        while (true)
        {
            (MachineConfig config, UserSettings user, UpdateState state) = context.LoadSettings();
            InstallInfo install = context.DetectInstall(state);
            ScheduledTaskInfo? check = null;
            try
            {
                check = ScheduledTasks.Connect().GetInfo(ScheduledTasks.CheckTask);
            }
            catch (Exception ex) when (ScheduledTasks.IsFailure(ex))
            {
            }

            TaskDialogResult result = TaskDialog.Show(new TaskDialogPage
            {
                MainInstruction = "QbtAutoUpdate",
                Content = DescribeSettings(install, config, state, check),
                CommandLinks = true,
                Buttons =
                [
                    new(CheckNow, T.Get("UiCheckNow")),
                    new(ToggleEnabled, T.Get(config.Enabled ? "UiTurnOff" : "UiTurnOn"), RequiresElevation: !Elevation.IsElevated),
                    new(ToggleMode, T.Get(config.Mode == UpdateMode.Auto ? "UiSwitchToNotify" : "UiSwitchToAuto"), RequiresElevation: !Elevation.IsElevated),
                    new(Rollback, T.Get("UiRollback"), RequiresElevation: !Elevation.IsElevated),
                    new(OpenLogs, T.Get("UiOpenLogs")),
                    new(Uninstall, T.Get("UiUninstall"), RequiresElevation: !Elevation.IsElevated),
                ],
                Close = true,
            });

            switch (result.Button)
            {
                case CheckNow:
                    CheckNowDialog(context, config, user, state, install);
                    break;
                case ToggleEnabled:
                    RunAsAdministrator(context, ["config", "--enabled", config.Enabled ? "off" : "on"]);
                    break;
                case ToggleMode:
                    RunAsAdministrator(context, ["config", "--mode", config.Mode == UpdateMode.Auto ? "notify" : "auto"]);
                    break;
                case Rollback:
                    RollbackDialog(context, install);
                    break;
                case OpenLogs:
                    Directory.CreateDirectory(context.Paths.UserLogsDir);
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{context.Paths.UserLogsDir}\"") { UseShellExecute = true })?.Dispose();
                    break;
                case Uninstall:
                    if (ConfirmUninstall())
                    {
                        int code = RunAsAdministrator(context, ["uninstall", "--quiet"]);
                        if (code != Elevation.ErrorCancelled)
                        {
                            ShowUninstallResult(code, context.Paths);
                            return code;
                        }
                    }
                    break;
                default:
                    return ExitCodes.Ok;
            }
        }
    }

    public static bool ConfirmUninstall() => TaskDialog.Show(new TaskDialogPage
    {
        MainInstruction = T.Get("UiUninstallConfirmTitle"),
        Content = T.Get("UiUninstallConfirmBody"),
        Icon = TaskDialogIcon.Warning,
        Buttons = [new(TaskDialogPage.IdYes, T.Get("UiUninstallButton"), RequiresElevation: !Elevation.IsElevated)],
        Cancel = true,
    }).Button == TaskDialogPage.IdYes;

    public static void ShowUninstallResult(int code, AppPaths paths) => Inform(code == 0
        ? new TaskDialogPage { MainInstruction = T.Get("UiUninstallDoneTitle"), Content = T.Get("UiUninstallDoneBody"), Icon = TaskDialogIcon.Information, Ok = true }
        : Failure(T.Get("UiUninstallFailed"), code, paths.MachineLogsDir));

    private static void CheckNowDialog(FlowContext context, MachineConfig config, UserSettings user, UpdateState state, InstallInfo install)
    {
        UpdatePlan? plan = null;
        string? error = null;
        TaskDialog.Show(new TaskDialogPage
        {
            MainInstruction = T.Get("UiChecking"),
            Work = async () =>
            {
                try
                {
                    plan = await context.CreatePlanner(user).PlanAsync(install, config, user, state, new PlanOptions(Manual: true), CancellationToken.None);
                }
                catch (Exception ex) when (ex is VersionSourceException || VersionSource.IsNetworkFailure(ex))
                {
                    error = ex.Message;
                }
            },
        });

        if (plan is null)
        {
            Inform(new TaskDialogPage { MainInstruction = T.Get("UiCheckFailed"), Content = error, Icon = TaskDialogIcon.Warning, Ok = true });
            return;
        }
        if (!plan.IsReady)
        {
            Inform(new TaskDialogPage
            {
                MainInstruction = T.Get(plan.Outcome == PlanOutcome.UpToDate ? "UiUpToDate" : "UiNothingToInstall"),
                Content = plan.Message,
                Icon = TaskDialogIcon.Information,
                Ok = true,
            });
            return;
        }

        TaskDialogResult confirm = TaskDialog.Show(new TaskDialogPage
        {
            MainInstruction = T.Format("ToastAvailableTitle", plan.Latest!.Version),
            Content = T.Format("ToastAvailableBody", install.Version?.ToString() ?? "?", plan.Variant!.Value.Describe()),
            Buttons = [new(TaskDialogPage.IdYes, T.Get("ToastUpdateNow"))],
            Cancel = true,
            Icon = TaskDialogIcon.Information,
        });
        if (confirm.Button != TaskDialogPage.IdYes)
            return;

        RunWithProgress(context, T.Format("UiUpdating", plan.Latest.Version),
            work => UpdateFlow.ExecuteAsync(work, plan, config, user, CancellationToken.None),
            ok => T.Format(ok ? "UiUpdated" : "UiUpdateIncomplete", plan.Latest.Version));
    }

    private static void RollbackDialog(FlowContext context, InstallInfo install)
    {
        TaskDialogResult confirm = TaskDialog.Show(new TaskDialogPage
        {
            MainInstruction = T.Get("UiRollbackConfirmTitle"),
            Content = T.Format("UiRollbackConfirmBody", install.Version?.ToString() ?? "?"),
            Icon = TaskDialogIcon.Warning,
            Buttons = [new(TaskDialogPage.IdYes, T.Get("UiRollbackButton"), RequiresElevation: !Elevation.IsElevated)],
            Cancel = true,
        });
        if (confirm.Button != TaskDialogPage.IdYes)
            return;

        if (Elevation.IsElevated)
        {
            RunWithProgress(context, T.Get("UiRollingBack"),
                work => RollbackFlow.RunAsync(work, CancellationToken.None),
                ok => T.Get(ok ? "UiRollbackDone" : "UiRollbackFailed"));
            return;
        }

        int code = 0;
        TaskDialog.Show(new TaskDialogPage
        {
            MainInstruction = T.Get("UiRollingBack"),
            Work = () => Task.Run(() => code = Elevation.RelaunchElevated(["rollback"])),
        });
        if (code == Elevation.ErrorCancelled)
            return;
        UpdateState state = UpdateState.Load(context.Paths.StateFile, []);
        Inform(new TaskDialogPage
        {
            MainInstruction = T.Get(code == 0 ? "UiRollbackDone" : "UiRollbackFailed"),
            Content = code == 0 && state.LastInstall is { } last ? $"qBittorrent {last.Version} ({last.Variant?.Describe()})" : T.Format("UiExitCode", code, context.Paths.UserLogsDir),
            Icon = code == 0 ? TaskDialogIcon.Information : TaskDialogIcon.Warning,
            Ok = true,
        });
    }

    /// <summary>Runs a flow behind a progress window, then shows everything it said.</summary>
    private static void RunWithProgress(FlowContext context, string title, Func<FlowContext, Task<int>> flow, Func<bool, string> resultTitle)
    {
        using var messages = new StringWriter(CultureInfo.CurrentCulture);
        using FlowContext work = FlowContext.Create(context.Command, messages);
        int code = ExitCodes.Error;
        TaskDialog.Show(new TaskDialogPage
        {
            MainInstruction = title,
            Content = T.Get("UiProgressBody"),
            Work = async () =>
            {
                try
                {
                    code = await flow(work);
                }
                catch (Exception ex)
                {
                    work.Log.Error(ex.ToString());
                    messages.WriteLine(ex.Message);
                }
            },
        });

        bool ok = code == ExitCodes.Ok;
        Inform(new TaskDialogPage
        {
            MainInstruction = resultTitle(ok),
            Content = messages.ToString().Trim(),
            Icon = ok ? TaskDialogIcon.Information : TaskDialogIcon.Warning,
            Ok = true,
        });
    }

    /// <summary>Runs a command as administrator: in this process when already elevated, else through one UAC prompt.</summary>
    private static int RunAsAdministrator(FlowContext context, List<string> arguments)
    {
        if (!Elevation.IsElevated)
            return Elevation.RelaunchElevated(arguments);

        CommandLine command = CommandLine.Parse(arguments);
        using FlowContext elevated = FlowContext.Create(command, TextWriter.Null);
        return command.Verb switch
        {
            "setup" => SetupFlow.Install(elevated, ConfigChanges.Parse(command, out _)!),
            "config" => ConfigFlow.Apply(elevated, ConfigChanges.Parse(command, out _)!),
            "uninstall" => UninstallFlow.Remove(elevated, command.Has("--keep-logs")),
            _ => ExitCodes.Usage,
        };
    }

    private static string DescribeInstall(InstallInfo install) => install.Kind switch
    {
        InstallKind.Installed => T.Format("UiInstalled", install.Version, install.DetectedVariant?.Describe() ?? T.Get("UiVariantUnknownShort"), install.InstallDir),
        InstallKind.NotInstalled => T.Get("UiNotInstalled"),
        _ => (install.Notes.Count > 0 ? install.Notes[0] : T.Get("PlanUnsupported")) + " " + T.Get("UiNotTouched"),
    };

    private static string DescribeSettings(InstallInfo install, MachineConfig config, UpdateState state, ScheduledTaskInfo? check)
    {
        var lines = new List<string>
        {
            DescribeInstall(install),
            "",
            config.Enabled
                ? T.Format(config.Mode == UpdateMode.Auto ? "UiStatusOnAuto" : "UiStatusOnNotify", config.CheckIntervalHours)
                : T.Get("UiStatusOff"),
        };
        if (config.SkippedVersion is { } held)
            lines.Add(T.Format("UiHeldBack", held));
        if (check?.LastRun is { } lastRun)
            lines.Add(T.Format("UiLastCheck", lastRun.ToString("g", CultureInfo.CurrentCulture)));
        if (state.LastAttempt is { } attempt)
            lines.Add(T.Format("UiLastAttempt", attempt.At.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), attempt.Message));
        return string.Join("\n", lines);
    }

    private static TaskDialogPage Failure(string title, int code, string logs) => new()
    {
        MainInstruction = title,
        Content = T.Format("UiExitCode", code, logs),
        Icon = TaskDialogIcon.Error,
        Ok = true,
    };

    private static void Inform(TaskDialogPage page) => TaskDialog.Show(page);

    private static bool IsNewerThan(string installedExe)
    {
        string? installed = FileVersionInfo.GetVersionInfo(installedExe).ProductVersion?.Split('+')[0];
        return Version.TryParse(SafeHttpClient.ProductVersion, out Version? mine)
               && (!Version.TryParse(installed, out Version? theirs) || mine > theirs);
    }
}
