using System.Runtime.InteropServices;
using QbtAutoUpdate.Core;
using QbtAutoUpdate.Platform;

namespace QbtAutoUpdate.Flows;

/// <summary>
/// "uninstall" (spec §4.2): removes both tasks, the Installed apps entry, the Start menu shortcut, the
/// machine data and this user's data (logs kept with --keep-logs), and the exe. qBittorrent is untouched.
/// </summary>
internal static class UninstallFlow
{
    public static int Run(FlowContext context)
    {
        CommandLine command = context.Command;
        bool quiet = command.Has("--quiet");
        bool keepLogs = command.Has("--keep-logs");

        if (!quiet && !UiFlow.ConfirmUninstall())
            return ExitCodes.Ok;

        if (!Elevation.IsElevated)
        {
            List<string> arguments = ["uninstall", "--quiet"];
            if (keepLogs)
                arguments.Add("--keep-logs");
            int code = SetupFlow.RelaunchElevated(context, arguments);
            if (!quiet && code != Elevation.ErrorCancelled)
                UiFlow.ShowUninstallResult(code, context.Paths);
            return code;
        }

        int result = Remove(context, keepLogs);
        if (!quiet)
            UiFlow.ShowUninstallResult(result, context.Paths);
        return result;
    }

    public static int Remove(FlowContext context, bool keepLogs)
    {
        AppPaths paths = context.Paths;
        ILog log = context.Log;
        log.Info($"Uninstalling QbtAutoUpdate{(keepLogs ? " (keeping logs)" : "")}.");

        try
        {
            ScheduledTasks.Connect().DeleteAll();
        }
        catch (Exception ex) when (ScheduledTasks.IsFailure(ex))
        {
            log.Warn($"Could not delete the scheduled tasks: {ex.Message}");
        }
        UninstallEntry.Delete();
        TryDeleteFile(SetupFlow.StartMenuShortcut);
        Toasts.Unregister();

        // The log file lives in the folders deleted below.
        context.StopFileLogging();
        DeleteData(paths.MachineDataDir, paths.MachineLogsDir, keepLogs);
        DeleteData(paths.UserDataDir, paths.UserLogsDir, keepLogs);
        RemoveBinary(paths);

        context.Say("QbtAutoUpdate was removed. qBittorrent itself was not touched.");
        return ExitCodes.Ok;
    }

    private static void DeleteData(string root, string logs, bool keepLogs)
    {
        if (!Directory.Exists(root))
            return;
        if (!keepLogs)
        {
            SetupFlow.DeleteDirectory(root);
            return;
        }
        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
        {
            if (ApplyFlow.SamePath(entry, logs))
                continue;
            if (Directory.Exists(entry))
                SetupFlow.DeleteDirectory(entry);
            else
                File.Delete(entry);
        }
    }

    /// <summary>
    /// The running exe cannot be deleted, but it can be renamed: it is moved to the temporary folder
    /// (same volume) and deleted at the next restart, so Program Files is clean right away.
    /// </summary>
    private static void RemoveBinary(AppPaths paths)
    {
        if (!Directory.Exists(paths.ProgramDir))
            return;
        foreach (string file in Directory.EnumerateFiles(paths.ProgramDir))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                string aside = Path.Combine(Path.GetTempPath(), $"QbtAutoUpdate-{Guid.NewGuid():N}.delete");
                if (NativeMethods.MoveFileEx(file, aside, NativeMethods.MOVEFILE_REPLACE_EXISTING))
                    NativeMethods.MoveFileEx(aside, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT);
                else
                    NativeMethods.MoveFileEx(file, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT);
            }
        }
        try
        {
            Directory.Delete(paths.ProgramDir);
        }
        catch (IOException)
        {
            NativeMethods.MoveFileEx(paths.ProgramDir, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
