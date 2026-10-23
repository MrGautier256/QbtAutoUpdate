using QbtAutoUpdate.Core;
using QbtAutoUpdate.Platform;

namespace QbtAutoUpdate.Flows;

/// <summary>
/// "setup" (spec §4.2): installs the exe in Program Files, creates the machine data folders with their
/// access rules, writes config.json, registers both scheduled tasks, adds QbtAutoUpdate to Installed
/// apps and the Start menu, and starts a first check. Running it again repairs or upgrades in place.
/// </summary>
internal static class SetupFlow
{
    public static int Run(FlowContext context)
    {
        CommandLine command = context.Command;
        if (!command.Has("--quiet"))
            return UiFlow.Setup(context);

        if (ConfigChanges.Parse(command, out string? error) is not { } changes)
        {
            context.Say(error!);
            return ExitCodes.Usage;
        }
        return Elevation.IsElevated ? Install(context, changes) : RelaunchElevated(context, command.ToArguments());
    }

    /// <summary>Runs the same command again as administrator (one UAC prompt) and reports how it ended.</summary>
    public static int RelaunchElevated(FlowContext context, List<string> arguments)
    {
        int code = Elevation.RelaunchElevated(arguments);
        context.Say(code switch
        {
            0 => "Done.",
            Elevation.ErrorCancelled => "Cancelled at the administrator prompt; nothing was changed.",
            _ => $"Failed (exit code {code}). Details: {context.Paths.MachineLogsDir}",
        });
        return code;
    }

    public static int Install(FlowContext context, ConfigChanges changes)
    {
        AppPaths paths = context.Paths;
        ILog log = context.Log;
        string version = SafeHttpClient.ProductVersion;
        log.Info($"Setting up QbtAutoUpdate {version} from {Environment.ProcessPath}.");

        PrepareMachineData(paths, log);
        InstallBinary(paths, log);

        var warnings = new List<string>();
        bool firstSetup = !File.Exists(paths.ConfigFile);
        MachineConfig config = MachineConfig.Load(paths.ConfigFile, warnings);
        config = changes.ApplyTo(firstSetup ? config with { Enabled = true } : config);
        if (config.Variant == VariantSetting.Auto)
        {
            InstallInfo install = InstallInfoRecovery.Recover(
                new InstallDetector(new SystemInstallEnvironment()).Detect(), UpdateState.Load(paths.StateFile, warnings));
            if (install.IsSupported && install.DetectedVariant is null)
            {
                // Spec §4.5: ask during setup, libtorrent 2.x by default (the dialog asks; --quiet takes the default).
                log.Warn("The libtorrent series of qbittorrent.exe could not be read; using libtorrent 2.x. Change it with 'config --variant'.");
                config = config with { Variant = VariantSetting.Lt2 };
            }
        }
        foreach (string warning in warnings)
            log.Warn(warning);
        config.Save(paths.ConfigFile);

        RegisterTasks(paths, config);
        UninstallEntry.Write(paths, version);
        CreateStartMenuShortcut(paths, log);
        Toasts.RegisterMachine(paths);

        context.Say($"QbtAutoUpdate {version} is installed in {paths.ProgramDir}.");
        context.Say($"Automatic updates {(config.Enabled ? "on" : "off")}: {Describe(config.Mode)}, at sign-in and every {config.CheckIntervalHours} h.");

        if (config.Enabled)
        {
            try
            {
                ScheduledTasks.Connect().Run(ScheduledTasks.CheckTask);
                log.Info("First check started.");
            }
            catch (Exception ex) when (ScheduledTasks.IsFailure(ex))
            {
                log.Warn($"Could not start the first check: {ex.Message}");
            }
        }
        return ExitCodes.Ok;
    }

    public static string Describe(UpdateMode mode) => mode == UpdateMode.Auto ? "install automatically" : "ask first with a notification";

    public static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "QbtAutoUpdate.lnk");

    internal static void RegisterTasks(AppPaths paths, MachineConfig config)
    {
        ScheduledTasks tasks = ScheduledTasks.Connect();
        DateTime first = DateTime.Now.AddHours(config.CheckIntervalHours);
        first = new DateTime(first.Year, first.Month, first.Day, first.Hour, first.Minute, 0, DateTimeKind.Local);
        tasks.Register(ScheduledTasks.CheckTask, TaskDefinitions.Check(paths.InstalledExe, config.CheckIntervalHours, config.Enabled, first),
            TaskDefinitions.UsersSid, isGroup: true, sddl: null);
        tasks.Register(ScheduledTasks.ApplyTask, TaskDefinitions.Apply(paths.InstalledExe),
            TaskDefinitions.SystemSid, isGroup: false, TaskDefinitions.ApplySddl);
    }

    /// <summary>
    /// A data folder that already exists but was not created by an administrator (or is a junction) is
    /// removed first: SYSTEM must never write through something a user prepared.
    /// </summary>
    private static void PrepareMachineData(AppPaths paths, ILog log)
    {
        if (Directory.Exists(paths.MachineDataDir) && !Acl.IsTrustedDirectory(paths.MachineDataDir))
        {
            log.Warn($"{paths.MachineDataDir} exists but was not created by an administrator; recreating it.");
            DeleteDirectory(paths.MachineDataDir);
        }
        Acl.SecureMachineData(paths);
        foreach (string directory in new[] { paths.WorkDir, paths.InboxDir })
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (Directory.Exists(entry))
                    DeleteDirectory(entry);
                else
                    File.Delete(entry);
            }
        }
    }

    /// <summary>Copies this exe to Program Files. A running older copy is renamed aside first.</summary>
    private static void InstallBinary(AppPaths paths, ILog log)
    {
        Directory.CreateDirectory(paths.ProgramDir);
        foreach (string leftover in Directory.EnumerateFiles(paths.ProgramDir, AppPaths.ExeName + ".old-*"))
        {
            try
            {
                File.Delete(leftover);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        string source = Environment.ProcessPath!;
        if (!ApplyFlow.SamePath(source, paths.InstalledExe))
        {
            if (File.Exists(paths.InstalledExe))
            {
                try
                {
                    File.Delete(paths.InstalledExe);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use (a check is running): a running exe can be renamed, not deleted.
                    File.Move(paths.InstalledExe, $"{paths.InstalledExe}.old-{DateTime.UtcNow.Ticks}");
                }
            }
            File.Copy(source, paths.InstalledExe);
            log.Info($"Copied {source} to {paths.InstalledExe}.");
        }

        if (Acl.IsWritableByNonAdmins(paths.ProgramDir))
            throw new InvalidOperationException($"{paths.ProgramDir} is writable by non-administrators; the SYSTEM task would not be safe.");
    }

    private static void CreateStartMenuShortcut(AppPaths paths, ILog log)
    {
        try
        {
            Shortcuts.Create(StartMenuShortcut, new ShortcutInfo(paths.InstalledExe, "", paths.ProgramDir, paths.InstalledExe, 0,
                "Keep qBittorrent up to date: settings, check now, uninstall"));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
        {
            log.Warn($"Could not create the Start menu shortcut: {ex.Message}");
        }
    }

    /// <summary>Deletes a folder; a junction or link is removed itself, never followed.</summary>
    internal static void DeleteDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            info.Delete(recursive: false);
        else
            info.Delete(recursive: true);
    }
}
