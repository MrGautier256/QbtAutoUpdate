using System.Text.Json;

namespace QbtAutoUpdate.Core;

public sealed record InstallRecord(QbtVersion Version, Variant? Variant, DateTimeOffset At, string Result, int? ExitCode, string? Message);

/// <summary>
/// What the SYSTEM side last did, %ProgramData%\QbtAutoUpdate\state.json (written by SYSTEM,
/// readable by everyone). Writing starts with the apply verb in phase 3.
/// </summary>
public sealed record UpdateState
{
    public InstallRecord? LastInstall { get; init; }
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
            Variant? variant = VariantText.TryParseConfigValue(li.String("variant"), out Variant v) ? v : null;
            int exitCode = li.Int("exitCode", int.MinValue, int.MinValue, int.MaxValue);
            last = new InstallRecord(version, variant, at, li.String("result") ?? "unknown",
                exitCode == int.MinValue ? null : exitCode, li.String("message"));
        }

        return new UpdateState { LastInstall = last, BusySince = f.Date("busySince") };
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
        if (BusySince is { } busy)
            w.WriteString("busySince", JsonFiles.ToIso(busy));
        w.WriteEndObject();
    });
}
