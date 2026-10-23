using System.Diagnostics;
using System.Text.RegularExpressions;
using QbtAutoUpdate.Platform;

namespace QbtAutoUpdate.Core;

public enum InstallStatus
{
    Installed,
    /// <summary>A check failed before anything was run.</summary>
    Refused,
    /// <summary>qBittorrent is running somewhere: try again later.</summary>
    Busy,
    /// <summary>The installer ran and failed, or did not finish in time.</summary>
    InstallerFailed,
    /// <summary>The installer reported success but the result does not match the target.</summary>
    NotVerified,
}

public sealed record InstallOutcome(InstallStatus Status, string Message, int? ExitCode = null, InstallInfo? After = null)
{
    public bool Succeeded => Status == InstallStatus.Installed;
}

/// <summary>An installer and its detached signature, already copied somewhere the caller trusts.</summary>
public sealed record InstallerFile(string Path, string SignaturePath, string FileName);

/// <summary>Runs an installer and returns its exit code, or null if it did not finish in time.</summary>
public interface IInstallerProcess
{
    int? Run(string path, string arguments, TimeSpan timeout);
}

public sealed class InstallerProcess : IInstallerProcess
{
    public int? Run(string path, string arguments, TimeSpan timeout)
    {
        // Raw argument string on purpose: NSIS wants "/D=C:\Program Files\qBittorrent" last and unquoted.
        var start = new ProcessStartInfo(path, arguments) { UseShellExecute = false, WorkingDirectory = System.IO.Path.GetDirectoryName(path)! };
        using Process process = Process.Start(start)!;
        return process.WaitForExit(timeout) ? process.ExitCode : null;
    }
}

/// <summary>Meaning of the exit codes of qBittorrent's NSIS installer (docs/FINDINGS.md §5).</summary>
public static class NsisExitCodes
{
    public static string Describe(int code) => code switch
    {
        0 or 1 or 2 or 1654 or 1223 or 1314 or 1603 or 1062 or 15618 => T.Get("Nsis" + code.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        _ => T.Format("NsisUnknown", code),
    };
}

/// <summary>
/// Verifies an installer one last time and runs it silently into the current installation folder,
/// then checks the result (spec §4.3, SYSTEM side, steps 4 to 7). Shared by "apply" (SYSTEM task)
/// and by an elevated "update" run (development mode).
/// </summary>
public sealed partial class InstallerRunner(
    PgpVerifier verifier,
    IInstallEnvironment environment,
    ILog log,
    Func<IReadOnlyList<QbtProcess>> runningQbittorrent,
    IInstallerProcess installer,
    CpuArch machine,
    Func<DateTimeOffset> clock)
{
    public static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(15);

    /// <param name="allowDowngrade">Only for "rollback", run by an administrator: an older version is accepted.</param>
    public InstallOutcome Install(InstallerFile file, InstallInfo before, Variant expectedVariant, bool allowDowngrade = false)
    {
        if (ParseInstallerName(file.FileName) is not { } parsed)
            return Refuse($"'{file.FileName}' is not a known qBittorrent installer name.");
        (QbtVersion version, Variant variant, CpuArch arch) = parsed;

        PgpResult signature = verifier.VerifyFile(file.Path, file.SignaturePath, clock());
        if (!signature.IsValid)
            return Refuse($"Signature refused ({signature.Verdict}): {signature.Detail}.");

        QbtVersion? fileVersion = environment.GetExeMetadata(file.Path).FileVersion;
        if (fileVersion != version)
            return Refuse($"The installer's version resource ({fileVersion?.ToString() ?? "none"}) does not match its name ({version}).");

        if (before.Version is { } installed)
        {
            if (version < installed && !allowDowngrade)
                return Refuse($"{version} is older than the installed {installed}; downgrades only happen through 'rollback'.");
            if (version == installed && !before.IsInconsistent)
                return Refuse($"{version} is already installed.");
        }
        else if (!before.IsInconsistent)
        {
            return Refuse("The installed version is unknown.");
        }

        if (variant != expectedVariant)
            return Refuse($"The installer is {variant.Describe()} but this machine uses {expectedVariant.Describe()}.");
        if (!ReleaseResolver.CompatibleArchs(machine).Contains(arch))
            return Refuse($"The installer is for {arch.FileToken()}, this machine is {machine.FileToken()}.");
        if (!before.IsSupported || before.InstallDir is null)
            return Refuse("No supported qBittorrent installation to update.");

        IReadOnlyList<QbtProcess> running = runningQbittorrent();
        if (running.Count > 0)
        {
            return new InstallOutcome(InstallStatus.Busy, T.Format("InstBusy", string.Join(", ", running.Select(p => p.Id))));
        }

        // NSIS silent mode skips check_instance: the check above is the only protection.
        string arguments = $"/S /D={before.InstallDir}";
        log.Info($"Running {file.FileName} {arguments}");
        int? exitCode = installer.Run(file.Path, arguments, InstallerTimeout);
        if (exitCode is null)
            return new InstallOutcome(InstallStatus.InstallerFailed, T.Format("InstTimeout", InstallerTimeout.TotalMinutes));
        if (exitCode != 0)
            return new InstallOutcome(InstallStatus.InstallerFailed, T.Format("InstFailed", NsisExitCodes.Describe(exitCode.Value)), exitCode);

        InstallInfo after = new InstallDetector(environment).Detect();
        if (after.Kind != InstallKind.Installed || after.ExeMissing || after.FileVersion != version || after.RegistryVersion != version)
        {
            return new InstallOutcome(InstallStatus.NotVerified,
                T.Format("InstNotVerified", after.FileVersion?.ToString() ?? "?", after.RegistryVersion?.ToString() ?? "?", version),
                exitCode, after);
        }
        if (after.DetectedVariant is { } detected && detected != expectedVariant)
        {
            return new InstallOutcome(InstallStatus.NotVerified,
                T.Format("InstWrongVariant", version, detected.Describe(), expectedVariant.Describe()), exitCode, after);
        }

        return new InstallOutcome(InstallStatus.Installed, T.Format("InstInstalled", version, expectedVariant.Describe()), exitCode, after);

        InstallOutcome Refuse(string message)
        {
            log.Error("Installer refused: " + message);
            return new InstallOutcome(InstallStatus.Refused, T.Format("InstRefused", message));
        }
    }

    /// <summary>"qbittorrent_5.3.0_lt12_x64_setup.exe" → (5.3.0, Lt12, X64), through the tested name table.</summary>
    public static (QbtVersion Version, Variant Variant, CpuArch Arch)? ParseInstallerName(string fileName)
    {
        Match match = InstallerNameRegex().Match(fileName);
        if (!match.Success || !QbtVersion.TryParse(match.Groups["version"].Value, out QbtVersion version))
            return null;
        return ReleaseResolver.Classify(version, fileName) is { } kind ? (version, kind.Variant, kind.Arch) : null;
    }

    [GeneratedRegex(@"^qbittorrent_(?<version>\d+(\.\d+){1,3})_[a-z0-9_]+_setup\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex InstallerNameRegex();
}
