using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace QbtAutoUpdate.Platform;

public static class Elevation
{
    public const int ErrorCancelled = 1223;

    public static bool IsElevated => Environment.IsPrivilegedProcess;

    public static bool IsSystem
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.IsSystem;
        }
    }

    /// <summary>
    /// Starts this exe again with the "runas" verb (one UAC prompt) and waits for it.
    /// Returns its exit code, or <see cref="ErrorCancelled"/> when the prompt was refused.
    /// </summary>
    public static int RelaunchElevated(IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!, CommandLineQuoting.Join(arguments))
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
        };
        try
        {
            using Process process = Process.Start(start)!;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return ErrorCancelled;
        }
    }
}
