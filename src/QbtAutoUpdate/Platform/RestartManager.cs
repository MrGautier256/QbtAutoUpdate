using QbtAutoUpdate.Core;

namespace QbtAutoUpdate.Platform;

/// <summary>
/// Asks one process to close through the Windows Restart Manager, the mechanism Windows provides
/// for installers: WM_QUERYENDSESSION (ENDSESSION_CLOSEAPP), WM_ENDSESSION, then WM_CLOSE.
/// Never forced (no RmForceShutdown). qBittorrent saves its resume data while handling
/// WM_QUERYENDSESSION, so a late answer can make Restart Manager report a failure while the
/// process is in fact exiting: callers judge success by the process exit, not by this result.
/// </summary>
internal static class RestartManager
{
    public static unsafe RestartManagerResult RequestClose(int processId, long startFileTime, TimeSpan timeout, ILog log)
    {
        char* key = stackalloc char[NativeMethods.CCH_RM_SESSION_KEY + 1];
        int error = NativeMethods.RmStartSession(out uint session, 0, key);
        if (error != NativeMethods.ERROR_SUCCESS)
        {
            log.Warn($"Restart Manager: RmStartSession failed ({error}).");
            return RestartManagerResult.Error;
        }

        try
        {
            var target = new NativeMethods.RM_UNIQUE_PROCESS { dwProcessId = processId, ProcessStartTime = startFileTime };
            error = NativeMethods.RmRegisterResources(session, 0, null, 1, &target, 0, null);
            if (error != NativeMethods.ERROR_SUCCESS)
            {
                log.Warn($"Restart Manager: RmRegisterResources failed ({error}).");
                return RestartManagerResult.Error;
            }

            // RmShutdown blocks until the applications are gone or Restart Manager gives up.
            Task<int> shutdown = Task.Run(() => NativeMethods.RmShutdown(session, 0, 0));
            if (!shutdown.Wait(timeout))
            {
                NativeMethods.RmCancelCurrentTask(session);
                shutdown.Wait(TimeSpan.FromSeconds(10));
                log.Info("Restart Manager: still waiting when the time budget ran out.");
                return RestartManagerResult.TimedOut;
            }

            error = shutdown.Result;
            log.Info(error == NativeMethods.ERROR_SUCCESS
                ? "Restart Manager: shutdown request completed."
                : $"Restart Manager: RmShutdown returned {error} (351 = some applications could not be shut down yet).");
            return error == NativeMethods.ERROR_SUCCESS ? RestartManagerResult.Completed : RestartManagerResult.Reported;
        }
        finally
        {
            NativeMethods.RmEndSession(session);
        }
    }
}
