using System.Diagnostics;

namespace QbtAutoUpdate.Core;

public enum QbtAccess
{
    /// <summary>Same user, same or lower privileges: shutdown messages can be delivered.</summary>
    Controllable,
    /// <summary>qBittorrent runs as administrator and this process does not.</summary>
    Elevated,
    /// <summary>Another user's qBittorrent.</summary>
    OtherUser,
    /// <summary>The process could not be inspected.</summary>
    Unreadable,
}

/// <summary>What Restart Manager said. Informational only: success is the process exit.</summary>
public enum RestartManagerResult
{
    Completed,
    Reported,
    TimedOut,
    Error,
}

/// <summary>The operations <see cref="QbtCloser"/> needs on one running qBittorrent.</summary>
public interface IQbtInstance
{
    int Id { get; }
    QbtAccess Access { get; }
    bool HasExited { get; }
    Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken);
    RestartManagerResult RequestClose(TimeSpan timeout);
    bool Kill();
}

public enum CloseMethod
{
    AlreadyExited,
    WebUi,
    RestartManager,
    Killed,
}

public sealed record CloseResult(bool Closed, CloseMethod? Method, string Message, TimeSpan Elapsed);

/// <summary>
/// Closes qBittorrent without losing resume data (spec §2.5). Order: the WebUI shutdown API when
/// configured (deterministic, no UI side effects), otherwise or on failure the Restart Manager.
/// Success is judged by the process actually exiting within the time budget. Never kills, unless
/// the machine setting allowForceClose was turned on explicitly.
/// </summary>
public sealed class QbtCloser(ILog log, Func<WebUiClient?> webUiFactory)
{
    public async Task<CloseResult> CloseAsync(IQbtInstance instance, bool useWebUi, TimeSpan timeout, bool allowForceClose, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        TimeSpan Left() => timeout - clock.Elapsed > TimeSpan.Zero ? timeout - clock.Elapsed : TimeSpan.Zero;
        CloseResult Result(bool closed, CloseMethod? method, string message) => new(closed, method, message, clock.Elapsed);

        switch (instance.Access)
        {
            case QbtAccess.Elevated:
                return Result(false, null, T.Format("CloseElevated", instance.Id));
            case QbtAccess.OtherUser:
                return Result(false, null, T.Format("CloseOtherUser", instance.Id));
            case QbtAccess.Unreadable:
                return Result(false, null, T.Format("CloseUnreadable", instance.Id));
        }

        if (instance.HasExited)
            return Result(true, CloseMethod.AlreadyExited, T.Format("CloseAlreadyExited", instance.Id));

        bool webUiAccepted = false;
        if (useWebUi)
        {
            using WebUiClient? client = webUiFactory();
            if (client is not null)
            {
                try
                {
                    await client.ShutdownAsync(cancellationToken).ConfigureAwait(false);
                    webUiAccepted = true;
                    log.Info($"WebUI accepted the shutdown request for qBittorrent (PID {instance.Id}).");
                }
                catch (WebUiException ex)
                {
                    log.Warn($"WebUI shutdown failed, falling back to Restart Manager: {ex.Message}");
                }
            }
        }

        if (webUiAccepted)
        {
            if (await instance.WaitForExitAsync(Left(), cancellationToken).ConfigureAwait(false))
                return Result(true, CloseMethod.WebUi, T.Format("CloseWebUi", Math.Round(clock.Elapsed.TotalSeconds)));
        }
        else
        {
            RestartManagerResult requested = await Task.Run(() => instance.RequestClose(Left()), cancellationToken).ConfigureAwait(false);
            log.Info($"Restart Manager result for PID {instance.Id}: {requested}.");
            if (await instance.WaitForExitAsync(Left(), cancellationToken).ConfigureAwait(false))
                return Result(true, CloseMethod.RestartManager, T.Format("CloseClean", Math.Round(clock.Elapsed.TotalSeconds)));
        }

        if (allowForceClose)
        {
            log.Warn($"qBittorrent (PID {instance.Id}) did not exit within {timeout.TotalSeconds:0} s; allowForceClose is on, terminating it.");
            if (instance.Kill() && await instance.WaitForExitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false))
                return Result(true, CloseMethod.Killed, T.Get("CloseKilled"));
        }

        return Result(false, null, T.Format("CloseTimeout", Math.Round(timeout.TotalSeconds)));
    }
}
