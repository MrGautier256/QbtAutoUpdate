using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using QbtAutoUpdate.Core;

namespace QbtAutoUpdate.Platform;

/// <summary>A running qbittorrent.exe. Path and start time are null when access is denied (other user, elevated).</summary>
public sealed record QbtProcess(int Id, int SessionId, string? Path, DateTime? StartTime);

public static class ProcessTools
{
    public const string QbittorrentProcessName = "qbittorrent";

    public static int CurrentSessionId { get; } = Process.GetCurrentProcess().SessionId;

    /// <summary>Every qbittorrent.exe on the machine, in all sessions.</summary>
    public static IReadOnlyList<QbtProcess> FindQbittorrent()
    {
        var result = new List<QbtProcess>();
        foreach (Process process in Process.GetProcessesByName(QbittorrentProcessName))
        {
            using (process)
            {
                DateTime? started = null;
                try
                {
                    started = process.StartTime;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                }
                result.Add(new QbtProcess(process.Id, process.SessionId, GetImagePath(process.Id), started));
            }
        }
        return result;
    }

    /// <summary>
    /// Opens every qbittorrent.exe of the given session (all sessions when null) and records what is
    /// needed to close it and start it again. The caller disposes the returned instances.
    /// </summary>
    public static List<RunningQbt> Snapshot(int? sessionId, ILog log)
    {
        var result = new List<RunningQbt>();
        foreach (QbtProcess process in FindQbittorrent())
        {
            if (sessionId is { } wanted && process.SessionId != wanted)
                continue;
            result.Add(RunningQbt.Open(process, log));
        }
        return result;
    }

    /// <summary>Full image path, readable with PROCESS_QUERY_LIMITED_INFORMATION (works for most processes of other users too).</summary>
    public static string? GetImagePath(int processId)
    {
        using SafeProcessHandle handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        return handle.IsInvalid ? null : GetImagePath(handle);
    }

    internal static string? GetImagePath(SafeProcessHandle handle)
    {
        char[] buffer = new char[32768];
        uint size = (uint)buffer.Length;
        return QueryImageName(handle, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
    }

    /// <summary>The full command line of another process (Windows 8.1+, PROCESS_QUERY_LIMITED_INFORMATION).</summary>
    internal static unsafe string? GetCommandLine(SafeProcessHandle handle)
    {
        int status = NativeMethods.NtQueryInformationProcess(handle, NativeMethods.ProcessCommandLineInformation, null, 0, out uint needed);
        if (status != NativeMethods.STATUS_INFO_LENGTH_MISMATCH || needed == 0 || needed > 1024 * 1024)
            return null;

        byte[] buffer = new byte[needed];
        fixed (byte* pointer = buffer)
        {
            if (NativeMethods.NtQueryInformationProcess(handle, NativeMethods.ProcessCommandLineInformation, pointer, needed, out _) != 0)
                return null;
            var text = (NativeMethods.UNICODE_STRING*)pointer;
            return text->Buffer == 0 ? null : new string((char*)text->Buffer, 0, text->Length / 2);
        }
    }

    /// <summary>Splits a command line exactly like Windows programs do (CommandLineToArgvW).</summary>
    public static unsafe string[] SplitCommandLine(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return [];
        nint argv = NativeMethods.CommandLineToArgv(commandLine, out int count);
        if (argv == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            var result = new string[count];
            for (int i = 0; i < count; i++)
                result[i] = new string(((char**)argv)[i]);
            return result;
        }
        finally
        {
            NativeMethods.LocalFree(argv);
        }
    }

    private static unsafe bool QueryImageName(SafeProcessHandle handle, char[] buffer, ref uint size)
    {
        fixed (char* pointer = buffer)
            return NativeMethods.QueryFullProcessImageName(handle, 0, pointer, ref size);
    }
}

/// <summary>
/// One running qBittorrent, opened once so that later checks cannot be fooled by PID reuse.
/// Implements the control surface <see cref="QbtCloser"/> needs.
/// </summary>
public sealed class RunningQbt : IQbtInstance, IDisposable
{
    private readonly SafeProcessHandle? _handle;
    private readonly long _startFileTime;
    private readonly ILog _log;

    private RunningQbt(QbtProcess process, SafeProcessHandle? handle, long startFileTime, QbtAccess access, QbtLaunch launch, ILog log)
    {
        Process = process;
        _handle = handle;
        _startFileTime = startFileTime;
        Access = access;
        Launch = launch;
        _log = log;
    }

    public QbtProcess Process { get; }
    public int Id => Process.Id;
    public QbtAccess Access { get; }
    /// <summary>The instance options to start it again with (files and magnet links removed).</summary>
    public QbtLaunch Launch { get; }

    internal static RunningQbt Open(QbtProcess process, ILog log)
    {
        SafeProcessHandle handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | NativeMethods.SYNCHRONIZE, false, process.Id);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return new RunningQbt(process, null, 0, QbtAccess.Unreadable, QbtLaunch.Default, log);
        }

        NativeMethods.GetProcessTimes(handle, out long created, out _, out _, out _);
        QbtAccess access = ReadAccess(handle);

        QbtLaunch launch = QbtLaunch.Default;
        string? commandLine = ProcessTools.GetCommandLine(handle);
        if (commandLine is null)
        {
            log.Warn($"Could not read the command line of qBittorrent (PID {process.Id}); it will be restarted without arguments.");
        }
        else
        {
            launch = QbtLaunch.FromArguments(ProcessTools.SplitCommandLine(commandLine).Skip(1));
            if (launch.DroppedCount > 0)
                log.Info($"qBittorrent (PID {process.Id}): {launch.DroppedCount} argument(s) about torrents to add will not be replayed on restart.");
        }

        return new RunningQbt(process, handle, created, access, launch, log);
    }

    public bool HasExited => _handle is null || NativeMethods.WaitForSingleObject(_handle, 0) == NativeMethods.WAIT_OBJECT_0;

    public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_handle is null)
            return Task.FromResult(true);
        return Task.Run(() =>
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan left = deadline - DateTime.UtcNow;
                uint slice = (uint)Math.Clamp(left.TotalMilliseconds, 0, 500);
                if (NativeMethods.WaitForSingleObject(_handle, slice) == NativeMethods.WAIT_OBJECT_0)
                    return true;
                if (left <= TimeSpan.Zero)
                    return false;
            }
        }, cancellationToken);
    }

    public RestartManagerResult RequestClose(TimeSpan timeout) =>
        RestartManager.RequestClose(Process.Id, _startFileTime, timeout, _log);

    public bool Kill()
    {
        using SafeProcessHandle handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_TERMINATE, false, Process.Id);
        // Same PID but another process? Only kill what was snapshotted.
        return !handle.IsInvalid && !HasExited && NativeMethods.TerminateProcess(handle, 1);
    }

    public void Dispose() => _handle?.Dispose();

    /// <summary>Whether this (non-elevated or elevated) process may send shutdown messages to the target.</summary>
    private static unsafe QbtAccess ReadAccess(SafeProcessHandle handle)
    {
        if (!NativeMethods.OpenProcessToken(handle, NativeMethods.TOKEN_QUERY, out SafeAccessTokenHandle token))
        {
            token.Dispose();
            // A process of the same session whose token we cannot read is almost always running elevated.
            return Environment.IsPrivilegedProcess ? QbtAccess.Unreadable : QbtAccess.Elevated;
        }

        using (token)
        {
            using var owner = new WindowsIdentity(token.DangerousGetHandle());
            using var self = WindowsIdentity.GetCurrent();
            if (owner.User != self.User)
                return QbtAccess.OtherUser;

            int elevated = 0;
            if (NativeMethods.GetTokenInformation(token, NativeMethods.TokenElevation, &elevated, sizeof(int), out _)
                && elevated != 0 && !Environment.IsPrivilegedProcess)
            {
                return QbtAccess.Elevated;
            }
            return QbtAccess.Controllable;
        }
    }
}
