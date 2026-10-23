using QbtAutoUpdate.Platform;

namespace QbtAutoUpdate.Core;

public sealed record ApplyOutcome(InstallStatus? Status, string Message, string? RequestId = null);

/// <summary>
/// The SYSTEM side of an update (spec §4.3): take the newest valid pair from the inbox, copy it into the
/// protected work folder, verify and install that copy, keep the Public desktop shortcut, record the result
/// in state.json, then empty work and inbox. Nothing from the inbox is trusted or executed in place.
/// </summary>
public sealed class ApplyService(
    AppPaths paths,
    ILog log,
    IInstallEnvironment environment,
    Func<IReadOnlyList<QbtProcess>> runningQbittorrent,
    IInstallerProcess installer,
    PgpVerifier verifier,
    CpuArch machine,
    Func<DateTimeOffset> clock,
    string publicDesktop)
{
    /// <summary>The shortcut the qBittorrent uninstaller removes from the Public desktop (FINDINGS §5).</summary>
    public const string DesktopShortcutName = "qBittorrent.lnk";

    public ApplyOutcome Run()
    {
        var warnings = new List<string>();
        MachineConfig config = MachineConfig.Load(paths.ConfigFile, warnings);
        UpdateState state = UpdateState.Load(paths.StateFile, warnings);
        foreach (string warning in warnings)
            log.Warn(warning);

        (InboxPair? pair, List<InboxEntry> discard) = Inbox.Select(ReadInbox());
        foreach (InboxEntry entry in discard)
        {
            log.Warn($"Inbox: deleting '{entry.Name}' (not a valid installer and signature pair, or not the newest).");
            TryDelete(entry.Path);
        }
        if (pair is null)
            return new ApplyOutcome(null, "Nothing to install: the inbox holds no valid installer and signature.");

        // Work on a private copy only; the inbox files are deleted right away.
        ClearWork();
        string installerCopy = Path.Combine(paths.WorkDir, pair.InstallerName);
        string signatureCopy = installerCopy + ".asc";
        try
        {
            CopyLimited(pair.Installer.Path, installerCopy, Inbox.MaxInstallerBytes);
            CopyLimited(pair.Signature.Path, signatureCopy, Inbox.MaxSignatureBytes);
        }
        finally
        {
            TryDelete(pair.Installer.Path);
            TryDelete(pair.Signature.Path);
        }

        (QbtVersion version, _, _) = InstallerRunner.ParseInstallerName(pair.InstallerName)!.Value;
        InstallInfo before = InstallInfoRecovery.Recover(new InstallDetector(environment).Detect(), state);
        Variant? expected = config.Variant switch
        {
            VariantSetting.Lt2 => Variant.Lt2,
            VariantSetting.Lt12 => Variant.Lt12,
            _ => before.DetectedVariant ?? state.LastInstall?.Variant,
        };

        if (expected is null)
        {
            return Finish(state, pair, version, null, before,
                new InstallOutcome(InstallStatus.Refused, "The libtorrent series of the installed qBittorrent is unknown; set 'variant' in the settings."));
        }

        string shortcut = Path.Combine(publicDesktop, DesktopShortcutName);
        string shortcutBackup = Path.Combine(paths.WorkDir, "desktop-shortcut.lnk");
        bool hadShortcut = File.Exists(shortcut);
        if (hadShortcut)
            File.Copy(shortcut, shortcutBackup, overwrite: true);

        // Written before the installer runs: found again later, it means the PC stopped mid-install.
        (state with
        {
            LastAttempt = new ApplyAttempt(clock(), ApplyAttempt.Installing, $"Installing {pair.InstallerName}", pair.RequestId, version, expected, before.InstallDir),
        }).Save(paths.StateFile);

        InstallOutcome outcome = new InstallerRunner(verifier, environment, log, runningQbittorrent, installer, machine, clock)
            .Install(new InstallerFile(installerCopy, signatureCopy, pair.InstallerName), before, expected.Value);

        if (hadShortcut && config.RestoreDesktopShortcut && !File.Exists(shortcut))
        {
            // A byte copy keeps target, arguments, icon and working directory exactly as they were.
            File.Copy(shortcutBackup, shortcut);
            log.Info($"Restored the Public desktop shortcut {shortcut}.");
        }

        return Finish(state, pair, version, expected, before, outcome);
    }

    public static string ResultName(InstallStatus status) => status switch
    {
        InstallStatus.Installed => "installed",
        InstallStatus.Refused => "refused",
        InstallStatus.Busy => "busy",
        InstallStatus.InstallerFailed => "failed",
        _ => "not-verified",
    };

    public static InstallStatus? StatusFromResult(string result) => result switch
    {
        "installed" => InstallStatus.Installed,
        "refused" => InstallStatus.Refused,
        "busy" => InstallStatus.Busy,
        "failed" => InstallStatus.InstallerFailed,
        "not-verified" => InstallStatus.NotVerified,
        _ => null,
    };

    private ApplyOutcome Finish(UpdateState state, InboxPair pair, QbtVersion version, Variant? variant, InstallInfo before, InstallOutcome outcome)
    {
        DateTimeOffset now = clock();
        UpdateState next = state with
        {
            LastAttempt = new ApplyAttempt(now, ResultName(outcome.Status), outcome.Message, pair.RequestId, version, variant, before.InstallDir, outcome.ExitCode),
            BusySince = outcome.Status == InstallStatus.Busy ? state.BusySince ?? now : null,
        };
        if (outcome.Succeeded)
            next = next with { LastInstall = new InstallRecord(version, variant, now, "installed", outcome.ExitCode, outcome.Message) };
        next.Save(paths.StateFile);

        log.Info($"apply: {ResultName(outcome.Status)} - {outcome.Message}");
        ClearWork();
        return new ApplyOutcome(outcome.Status, outcome.Message, pair.RequestId);
    }

    private List<InboxEntry> ReadInbox()
    {
        var directory = new DirectoryInfo(paths.InboxDir);
        if (!directory.Exists)
            return [];
        return directory.EnumerateFiles()
            .Select(f => new InboxEntry(f.FullName, f.Name, f.Length, f.LastWriteTimeUtc, f.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            .ToList();
    }

    private void ClearWork()
    {
        Directory.CreateDirectory(paths.WorkDir);
        foreach (string file in Directory.EnumerateFiles(paths.WorkDir))
            TryDelete(file);
    }

    /// <summary>Copies at most <paramref name="maxBytes"/>: the inbox file may grow while it is read.</summary>
    private static void CopyLimited(string source, string destination, long maxBytes)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        SafeHttpClient.CopyWithLimitAsync(input, output, maxBytes, CancellationToken.None).GetAwaiter().GetResult();
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"Could not delete {path}: {ex.Message}");
        }
    }
}
