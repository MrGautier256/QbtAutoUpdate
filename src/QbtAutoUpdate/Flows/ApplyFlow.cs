using QbtAutoUpdate.Core;
using QbtAutoUpdate.Platform;

namespace QbtAutoUpdate.Flows;

/// <summary>
/// "apply": run by the SYSTEM task only (spec §4.3, SYSTEM side). Takes no argument; refuses to run
/// from anywhere but the admin-only Program Files folder, or on data folders it does not trust.
/// </summary>
internal static class ApplyFlow
{
    public static int Run(FlowContext context)
    {
        AppPaths paths = context.Paths;
        ILog log = context.Log;

        if (!Elevation.IsSystem && !Elevation.IsElevated)
            return Refuse(log, "'apply' runs as SYSTEM through the scheduled task only.");
        if (Elevation.IsSystem && !SamePath(Environment.ProcessPath, paths.InstalledExe))
            return Refuse(log, $"'apply' as SYSTEM must run from {paths.InstalledExe}, not {Environment.ProcessPath}.");
        if (Acl.IsWritableByNonAdmins(paths.ProgramDir))
            return Refuse(log, $"{paths.ProgramDir} is writable by non-administrators; refusing to install anything.");
        foreach (string directory in new[] { paths.MachineDataDir, paths.WorkDir, paths.InboxDir })
        {
            if (!Acl.IsTrustedDirectory(directory))
                return Refuse(log, $"{directory} is missing, redirected or not owned by administrators; run setup again.");
        }

        // One apply at a time, machine-wide.
        using var mutex = new Mutex(false, @"Global\QbtAutoUpdate.Apply");
        bool owned;
        try
        {
            owned = mutex.WaitOne(TimeSpan.FromSeconds(1));
        }
        catch (AbandonedMutexException)
        {
            owned = true;
        }
        if (!owned)
            return Refuse(log, "Another 'apply' is already running.");

        try
        {
            ApplyOutcome outcome = new ApplyService(
                    paths, log, new SystemInstallEnvironment(), ProcessTools.FindQbittorrent, new InstallerProcess(),
                    PgpVerifier.CreateOfficial(), SystemInfo.NativeArchitecture(), () => DateTimeOffset.Now,
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory))
                .Run();
            context.Say(outcome.Message);
            return outcome.Status switch
            {
                null or InstallStatus.Installed => ExitCodes.Ok,
                InstallStatus.Busy => ExitCodes.Busy,
                InstallStatus.Refused => ExitCodes.VerificationFailed,
                _ => ExitCodes.InstallFailed,
            };
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    internal static bool SamePath(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static int Refuse(ILog log, string message)
    {
        log.Error("apply refused: " + message);
        return ExitCodes.VerificationFailed;
    }
}
