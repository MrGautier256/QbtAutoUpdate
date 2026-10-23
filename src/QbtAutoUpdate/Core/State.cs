using System.Text.Json;

namespace QbtAutoUpdate.Core;

/// <summary>A successful install by the tool: the variant it installed is remembered.</summary>
public sealed record InstallRecord(QbtVersion Version, Variant? Variant, DateTimeOffset At, string Result, int? ExitCode, string? Message);

/// <summary>
/// The last run of "apply", whatever its outcome. <see cref="RequestId"/> lets the user side recognize
/// the answer to its own request. <see cref="Result"/> is "installing" while the installer runs: found
/// later, it means the PC stopped mid-install (see <see cref="InstallInfoRecovery"/>).
/// </summary>
public sealed record ApplyAttempt(
    DateTimeOffset At,
    string Result,
    string Message,
    string? RequestId = null,
    QbtVersion? Version = null,
    Variant? Variant = null,
    string? InstallDir = null,
    int? ExitCode = null)
{
    public const string Installing = "installing";
}

/// <summary>
/// What the SYSTEM side did, %ProgramData%\QbtAutoUpdate\state.json (written by SYSTEM, readable by everyone).
/// </summary>
public sealed record UpdateState
{
    public InstallRecord? LastInstall { get; init; }
    public ApplyAttempt? LastAttempt { get; init; }
    /// <summary>Set when apply found qBittorrent running in some session and backed off.</summary>
    public DateTimeOffset? BusySince { get; init; }

    public static readonly UpdateState Empty = new();

    public static UpdateState Load(string path, List<string> warnings)
    {
        using JsonDocument? document = JsonFields.TryLoad(path, "state.json", warnings);
        if (document is null)
            return Empty;

        var f = new JsonFields(document.RootElement, "state.json", warnings);
        InstallRecord? last = null;
        if (f.Object("lastInstall") is { } li && li.Version("version") is { } version && li.Date("at") is { } at)
        {
            last = new InstallRecord(version, ReadVariant(li), at, li.String("result") ?? "unknown",
                ReadExitCode(li), li.String("message"));
        }

        ApplyAttempt? attempt = null;
        if (f.Object("lastAttempt") is { } la && la.Date("at") is { } attemptAt)
        {
            attempt = new ApplyAttempt(attemptAt, la.String("result") ?? "unknown", la.String("message") ?? "",
                la.String("requestId"), la.Version("version"), ReadVariant(la), la.String("installDir"), ReadExitCode(la));
        }

        return new UpdateState { LastInstall = last, LastAttempt = attempt, BusySince = f.Date("busySince") };
    }

    public void Save(string path) => JsonFiles.WriteAtomically(path, w =>
    {
        w.WriteStartObject();
        if (LastInstall is { } last)
        {
            w.WriteStartObject("lastInstall");
            w.WriteString("version", last.Version.ToString());
            if (last.Variant is { } variant)
                w.WriteString("variant", variant.ToConfigValue());
            w.WriteString("at", JsonFiles.ToIso(last.At));
            w.WriteString("result", last.Result);
            if (last.ExitCode is { } code)
                w.WriteNumber("exitCode", code);
            if (last.Message is { } message)
                w.WriteString("message", message);
            w.WriteEndObject();
        }
        if (LastAttempt is { } attempt)
        {
            w.WriteStartObject("lastAttempt");
            w.WriteString("at", JsonFiles.ToIso(attempt.At));
            w.WriteString("result", attempt.Result);
            w.WriteString("message", attempt.Message);
            if (attempt.RequestId is { } id)
                w.WriteString("requestId", id);
            if (attempt.Version is { } version)
                w.WriteString("version", version.ToString());
            if (attempt.Variant is { } variant)
                w.WriteString("variant", variant.ToConfigValue());
            if (attempt.InstallDir is { } dir)
                w.WriteString("installDir", dir);
            if (attempt.ExitCode is { } code)
                w.WriteNumber("exitCode", code);
            w.WriteEndObject();
        }
        if (BusySince is { } busy)
            w.WriteString("busySince", JsonFiles.ToIso(busy));
        w.WriteEndObject();
    });

    private static Variant? ReadVariant(JsonFields f) => VariantText.TryParseConfigValue(f.String("variant"), out Variant v) ? v : null;

    private static int? ReadExitCode(JsonFields f)
    {
        int code = f.Int("exitCode", int.MinValue, int.MinValue, int.MaxValue);
        return code == int.MinValue ? null : code;
    }
}

/// <summary>
/// Spec §6: "power cut or restart during the installation: on the next run, the inconsistent state is
/// detected and the installation done again". The NSIS installer first runs the old uninstaller, which
/// removes the registry keys and qbittorrent.exe; an interruption right after leaves no trace of
/// qBittorrent at all. state.json still says "installing" with the folder and variant: enough to repair.
/// </summary>
public static class InstallInfoRecovery
{
    public static InstallInfo Recover(InstallInfo detected, UpdateState state)
    {
        if (detected.Kind != InstallKind.NotInstalled
            || state.LastAttempt is not { Result: ApplyAttempt.Installing, InstallDir: { } dir } attempt)
        {
            return detected;
        }

        return new InstallInfo
        {
            Kind = InstallKind.Installed,
            InstallDir = dir,
            ExePath = Path.Combine(dir, "qbittorrent.exe"),
            ExeMissing = true,
            RecoveredVariant = attempt.Variant,
            Notes = [$"An installation of qBittorrent {attempt.Version} was interrupted on {attempt.At:yyyy-MM-dd HH:mm}; it will be done again."],
        };
    }
}
