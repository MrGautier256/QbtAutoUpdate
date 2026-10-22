using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace QbtAutoUpdate.Platform;

/// <summary>A running qbittorrent.exe. Path and start time are null when access is denied (other user, elevated).</summary>
public sealed record QbtProcess(int Id, int SessionId, string? Path, DateTime? StartTime);

public static class ProcessTools
{
    public const string QbittorrentProcessName = "qbittorrent";

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

    /// <summary>Full image path, readable with PROCESS_QUERY_LIMITED_INFORMATION (works for most processes of other users too).</summary>
    public static string? GetImagePath(int processId)
    {
        using SafeProcessHandle handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle.IsInvalid)
            return null;

        char[] buffer = new char[32768];
        uint size = (uint)buffer.Length;
        return QueryImageName(handle, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
    }

    private static unsafe bool QueryImageName(SafeProcessHandle handle, char[] buffer, ref uint size)
    {
        fixed (char* pointer = buffer)
            return NativeMethods.QueryFullProcessImageName(handle, 0, pointer, ref size);
    }
}
