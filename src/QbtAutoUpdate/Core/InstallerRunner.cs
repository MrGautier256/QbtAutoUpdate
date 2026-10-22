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
        0 => "success",
        1 => "the installation was cancelled",
        2 => "the installer script aborted",
        1654 => "the installer refused this system (Windows 10 1809 or later, 64-bit, and a matching architecture are required)",
        1223 => "the administrator prompt was cancelled",
        1314 => "administrator privileges are missing",
        1603 => "the installer could not elevate",
        1062 => "the Windows logon service is not running",
        15618 => "qBittorrent was running",
        _ => $"unknown exit code {code}",
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

    public InstallOutcome Install(InstallerFile file, InstallInfo before, Variant expectedVariant)
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
            if (version < installed)
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
            return new InstallOutcome(InstallStatus.Busy,
                $"qBittorrent is running (PID {string.Join(", ", running.Select(p => p.Id))}); the installation will be retried later.");
        }

        // NSIS silent mode skips check_instance: the check above is the only protection.
        string arguments = $"/S /D={before.InstallDir}";
        log.Info($"Running {file.FileName} {arguments}");
        int? exitCode = installer.Run(file.Path, arguments, InstallerTimeout);
        if (exitCode is null)
            return new InstallOutcome(InstallStatus.InstallerFailed, $"The installer did not finish within {InstallerTimeout.TotalMinutes:0} minutes.");
        if (exitCode != 0)
            return new InstallOutcome(InstallStatus.InstallerFailed, $"The installer failed: {NsisExitCodes.Describe(exitCode.Value)}.", exitCode);

        InstallInfo after = new InstallDetector(environment).Detect();
        if (after.Kind != InstallKind.Installed || after.ExeMissing || after.FileVersion != version || after.RegistryVersion != version)
        {
            return new InstallOutcome(InstallStatus.NotVerified,
                $"The installer reported success, but qBittorrent now reports {after.FileVersion?.ToString() ?? "no version"} (registry {after.RegistryVersion?.ToString() ?? "none"}) instead of {version}.",
                exitCode, after);
        }
        if (after.DetectedVariant is { } detected && detected != expectedVariant)
        {
            return new InstallOutcome(InstallStatus.NotVerified,
                $"qBittorrent {version} is installed but is {detected.Describe()} instead of {expectedVariant.Describe()}.", exitCode, after);
        }

        return new InstallOutcome(InstallStatus.Installed, $"qBittorrent {version} ({expectedVariant.Describe()}) installed.", exitCode, after);

        InstallOutcome Refuse(string message)
        {
            log.Error("Installer refused: " + message);
            return new InstallOutcome(InstallStatus.Refused, message);
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
