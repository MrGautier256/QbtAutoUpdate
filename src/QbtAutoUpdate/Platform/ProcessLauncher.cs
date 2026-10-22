using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using QbtAutoUpdate.Core;

namespace QbtAutoUpdate.Platform;

/// <summary>Starts qBittorrent again after an update, always as the logged-on user, never elevated.</summary>
public static class ProcessLauncher
{
    public static bool StartQbittorrent(string exePath, IReadOnlyList<string> arguments, ILog log)
    {
        string workingDirectory = Path.GetDirectoryName(exePath)!;
        try
        {
            if (!Environment.IsPrivilegedProcess)
            {
                // ShellExecute, like Explorer: qBittorrent must not inherit this process's handles
                // (a redirected console pipe would stay open as long as qBittorrent runs).
                var start = new ProcessStartInfo(exePath, CommandLineQuoting.Join(arguments))
                {
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = true,
                };
                using Process? process = Process.Start(start);
                log.Info($"Started qBittorrent (PID {process?.Id}).");
                return process is not null;
            }

            // An elevated QbtAutoUpdate (development mode, rollback) must not start qBittorrent elevated:
            // borrow the desktop shell's token so qBittorrent runs exactly as if the user had started it.
            int id = StartWithShellToken(exePath, arguments, workingDirectory);
            log.Info($"Started qBittorrent unelevated through the shell's token (PID {id}).");
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            log.Error($"Could not start qBittorrent again: {ex.Message}");
            return false;
        }
    }

    private static unsafe int StartWithShellToken(string exePath, IReadOnlyList<string> arguments, string workingDirectory)
    {
        nint shellWindow = NativeMethods.GetShellWindow();
        if (shellWindow == 0)
            throw new InvalidOperationException("no desktop shell is running in this session");
        NativeMethods.GetWindowThreadProcessId(shellWindow, out int shellProcessId);

        using SafeProcessHandle shell = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, shellProcessId);
        if (shell.IsInvalid)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        if (!NativeMethods.OpenProcessToken(shell, NativeMethods.TOKEN_DUPLICATE, out SafeAccessTokenHandle shellToken))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());

        using (shellToken)
        {
            const uint Access = NativeMethods.TOKEN_QUERY | NativeMethods.TOKEN_ASSIGN_PRIMARY | NativeMethods.TOKEN_DUPLICATE
                                | NativeMethods.TOKEN_ADJUST_DEFAULT | NativeMethods.TOKEN_ADJUST_SESSIONID;
            if (!NativeMethods.DuplicateTokenEx(shellToken, Access, 0, NativeMethods.SecurityImpersonation, NativeMethods.TokenPrimary, out SafeAccessTokenHandle token))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());

            using (token)
            {
                // CreateProcessWithTokenW may modify the command line buffer: give it a private copy.
                char[] commandLine = (CommandLineQuoting.Join([exePath, .. arguments]) + "\0").ToCharArray();
                var startup = new NativeMethods.STARTUPINFOW { cb = (uint)sizeof(NativeMethods.STARTUPINFOW) };
                NativeMethods.PROCESS_INFORMATION info;
                fixed (char* application = exePath + "\0")
                fixed (char* line = commandLine)
                fixed (char* directory = workingDirectory + "\0")
                {
                    if (!NativeMethods.CreateProcessWithTokenW(token, 0, application, line, NativeMethods.CREATE_UNICODE_ENVIRONMENT,
                            0, directory, &startup, &info))
                    {
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
                    }
                }
                NativeMethods.CloseHandle(info.hThread);
                NativeMethods.CloseHandle(info.hProcess);
                return info.dwProcessId;
            }
        }
    }
}

/// <summary>Builds a command line that CommandLineToArgvW splits back into the same arguments.</summary>
public static class CommandLineQuoting
{
    public static string Join(IEnumerable<string> arguments) => string.Join(' ', arguments.Select(Quote));

    public static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
            return argument;

        var builder = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            // Backslashes are literal unless they precede a quote: then each one must be doubled.
            builder.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            builder.Append(c);
            backslashes = 0;
        }
        builder.Append('\\', backslashes * 2).Append('"');
        return builder.ToString();
    }
}
