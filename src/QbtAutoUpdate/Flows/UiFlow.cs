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
            MainInstruction = upgrade ? $"Update QbtAutoUpdate to {SafeHttpClient.ProductVersion}" : "Keep qBittorrent up to date",
            Content = DescribeInstall(install) + "\n\n"
                      + "QbtAutoUpdate checks for new versions at sign-in and every 6 hours, verifies the official signature, "
                      + "and keeps your libtorrent version (1.2 or 2.x). It runs only when needed, through the Task Scheduler.",
            Icon = TaskDialogIcon.Information,
            RadioButtons =
            [
                new(RadioAuto, "Update automatically: close qBittorrent cleanly, install, start it again"),
                new(RadioNotify, "Ask me first with a notification"),
            ],
            DefaultRadioButton = configured && config.Mode == UpdateMode.Notify ? RadioNotify : RadioAuto,
            Buttons = [new(InstallButton, upgrade ? "Update" : "Install", RequiresElevation: !Elevation.IsElevated)],
            DefaultButton = InstallButton,
            Cancel = true,
            Footer = "One administrator prompt now, none afterwards.",
        });
        if (choice.Button != InstallButton)
            return ExitCodes.Ok;

        List<string> arguments = ["setup", "--quiet", "--mode", choice.RadioButton == RadioNotify ? "notify" : "auto"];
        if (install.IsSupported && install.DetectedVariant is null && config.Variant == VariantSetting.Auto)
        {
            TaskDialogResult variant = TaskDialog.Show(new TaskDialogPage
            {
                MainInstruction = "Which qBittorrent build do you use?",
                Content = "QbtAutoUpdate could not tell from qbittorrent.exe. It will keep installing the build you choose.",
                RadioButtons =
                [
                    new(RadioLt2, "libtorrent 2.x: the default download since qBittorrent 5.3.0"),
                    new(RadioLt12, "libtorrent 1.2: the \"lt12\" download (default before 5.3.0)"),
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
            ? new TaskDialogPage
            {
                MainInstruction = "QbtAutoUpdate is ready",
                Content = "It works in the background from now on, without administrator prompts.\n\n"
                          + "To change a setting, check now or uninstall, open QbtAutoUpdate from the Start menu.",
                Icon = TaskDialogIcon.Information,
                Ok = true,
            }
            : Failure("Setup failed", code, context.Paths.MachineLogsDir));
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
                    new(CheckNow, "Check for updates now"),
                    new(ToggleEnabled, config.Enabled ? "Turn automatic updates off" : "Turn automatic updates on", RequiresElevation: !Elevation.IsElevated),
                    new(ToggleMode, config.Mode == UpdateMode.Auto ? "Ask before updating\nShow a notification instead of installing by itself"
                                                                   : "Update automatically\nClose qBittorrent cleanly, install, start it again",
                        RequiresElevation: !Elevation.IsElevated),
                    new(OpenLogs, "Open the log folder"),
                    new(Uninstall, "Uninstall QbtAutoUpdate\nqBittorrent itself stays installed", RequiresElevation: !Elevation.IsElevated),
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
        MainInstruction = "Uninstall QbtAutoUpdate?",
        Content = "The scheduled tasks, settings and logs of QbtAutoUpdate are removed. qBittorrent itself stays installed.",
        Icon = TaskDialogIcon.Warning,
        Buttons = [new(TaskDialogPage.IdYes, "Uninstall", RequiresElevation: !Elevation.IsElevated)],
        Cancel = true,
    }).Button == TaskDialogPage.IdYes;

    public static void ShowUninstallResult(int code, AppPaths paths) => Inform(code == 0
        ? new TaskDialogPage { MainInstruction = "QbtAutoUpdate was removed", Content = "qBittorrent itself was not touched.", Icon = TaskDialogIcon.Information, Ok = true }
        : Failure("Uninstall failed", code, paths.MachineLogsDir));

    private static void CheckNowDialog(FlowContext context, MachineConfig config, UserSettings user, UpdateState state, InstallInfo install)
    {
        UpdatePlan? plan = null;
        string? error = null;
        TaskDialog.Show(new TaskDialogPage
        {
            MainInstruction = "Checking for updates...",
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
            Inform(new TaskDialogPage { MainInstruction = "Could not check for updates", Content = error, Icon = TaskDialogIcon.Warning, Ok = true });
            return;
        }
        if (!plan.IsReady)
        {
            Inform(new TaskDialogPage { MainInstruction = plan.Outcome == PlanOutcome.UpToDate ? "qBittorrent is up to date" : "No update to install", Content = plan.Message, Icon = TaskDialogIcon.Information, Ok = true });
            return;
        }

        TaskDialogResult confirm = TaskDialog.Show(new TaskDialogPage
        {
            MainInstruction = $"qBittorrent {plan.Latest!.Version} is available",
            Content = $"Installed: {install.Version} ({plan.Variant!.Value.Describe()}).\n\n"
                      + "QbtAutoUpdate will close qBittorrent cleanly, install the official, verified update and start qBittorrent again.",
            Buttons = [new(TaskDialogPage.IdYes, "Update now")],
            Cancel = true,
            Icon = TaskDialogIcon.Information,
        });
        if (confirm.Button != TaskDialogPage.IdYes)
            return;

        using var messages = new StringWriter(CultureInfo.CurrentCulture);
        using FlowContext work = FlowContext.Create(context.Command, messages);
        int code = ExitCodes.Error;
        TaskDialog.Show(new TaskDialogPage
        {
            MainInstruction = $"Updating qBittorrent to {plan.Latest.Version}...",
            Content = "qBittorrent will be closed and started again. This takes a minute or two.",
            Work = async () =>
            {
                try
                {
                    code = await UpdateFlow.ExecuteAsync(work, plan, config, user, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    work.Log.Error(ex.ToString());
                    messages.WriteLine("Error: " + ex.Message);
                }
            },
        });

        Inform(new TaskDialogPage
        {
            MainInstruction = code == ExitCodes.Ok ? $"qBittorrent {plan.Latest.Version} is installed" : "The update did not complete",
            Content = messages.ToString().Trim(),
            Icon = code == ExitCodes.Ok ? TaskDialogIcon.Information : TaskDialogIcon.Warning,
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
        InstallKind.Installed => $"qBittorrent {install.Version} ({install.DetectedVariant?.Describe() ?? "libtorrent version unknown"}) is installed in {install.InstallDir}.",
        InstallKind.NotInstalled => "qBittorrent is not installed yet. QbtAutoUpdate will keep it up to date once it is.",
        _ => (install.Notes.Count > 0 ? install.Notes[0] : "This qBittorrent installation is not supported.") + " QbtAutoUpdate will not touch it.",
    };

    private static string DescribeSettings(InstallInfo install, MachineConfig config, UpdateState state, ScheduledTaskInfo? check)
    {
        var lines = new List<string>
        {
            DescribeInstall(install),
            "",
            config.Enabled
                ? $"Automatic updates: on. {char.ToUpperInvariant(SetupFlow.Describe(config.Mode)[0])}{SetupFlow.Describe(config.Mode)[1..]}, at sign-in and every {config.CheckIntervalHours} h."
                : "Automatic updates: off.",
        };
        if (check?.LastRun is { } lastRun)
            lines.Add($"Last check: {lastRun.ToString("g", CultureInfo.CurrentCulture)}.");
        if (state.LastAttempt is { } attempt)
            lines.Add($"Last update attempt: {attempt.At.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}, {attempt.Result}: {attempt.Message}");
        return string.Join("\n", lines);
    }

    private static TaskDialogPage Failure(string title, int code, string logs) => new()
    {
        MainInstruction = title,
        Content = $"Exit code {code}. Details are in the log folder:\n{logs}",
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
