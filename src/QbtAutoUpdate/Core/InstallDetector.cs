using Microsoft.Win32;
using QbtAutoUpdate.Platform;

namespace QbtAutoUpdate.Core;

public enum InstallKind
{
    NotInstalled,
    /// <summary>Installed by the official installer: the only kind the tool updates.</summary>
    Installed,
    /// <summary>The Enhanced Edition fork, which shares the official registry keys and folder.</summary>
    Fork,
    /// <summary>Another product or a modified build in the official location.</summary>
    NotOfficial,
    /// <summary>Running without registry keys (portable "profile" folder or a plain copy).</summary>
    Portable,
    /// <summary>Old 32-bit 4.x install in Program Files (x86).</summary>
    Legacy32Bit,
}

public sealed record InstallInfo
{
    public required InstallKind Kind { get; init; }
    public string? InstallDir { get; init; }
    public string? ExePath { get; init; }
    public RegistryView? RegistryView { get; init; }
    public QbtVersion? RegistryVersion { get; init; }
    public QbtVersion? FileVersion { get; init; }
    public ExeMetadata? Metadata { get; init; }
    public BinaryScanResult? Scan { get; init; }
    public bool ExeMissing { get; init; }
    /// <summary>A "profile" folder next to the exe: qBittorrent then runs in portable mode.</summary>
    public bool HasPortableProfile { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
    /// <summary>Variant of an install that was interrupted (no binary left to scan), from state.json.</summary>
    public Variant? RecoveredVariant { get; init; }

    public bool IsSupported => Kind == InstallKind.Installed;

    /// <summary>The file version wins over the registry when both are known and differ.</summary>
    public QbtVersion? Version => FileVersion ?? RegistryVersion;

    /// <summary>Registry and binary disagree, or the binary is gone: reinstalling the same version is allowed.</summary>
    public bool IsInconsistent => ExeMissing || (FileVersion is not null && RegistryVersion is not null && FileVersion != RegistryVersion);

    public Variant? DetectedVariant => Scan?.Variant ?? RecoveredVariant;
}

/// <summary>
/// Finds the qBittorrent installation and decides whether the tool may touch it
/// (docs/FINDINGS.md §5 and §6).
/// </summary>
public sealed class InstallDetector(IInstallEnvironment environment)
{
    public const string AppKey = @"Software\qBittorrent";
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\qBittorrent";

    internal const string OfficialCompany = "The qBittorrent Project";
    internal const string OfficialProduct = "qBittorrent";

    /// <summary>The Enhanced Edition always uses a 4th version component of 10 or more (5.2.4.10).</summary>
    internal const int ForkBuildThreshold = 10;

    public InstallInfo Detect()
    {
        // The 32-bit NSIS installer writes to the 32-bit view (WOW6432Node). Read it first.
        foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            string? location = environment.GetMachineString(view, AppKey, "InstallLocation");
            string? displayVersion = environment.GetMachineString(view, UninstallKey, "DisplayVersion");
            string? uninstallString = environment.GetMachineString(view, UninstallKey, "UninstallString");
            if (location is null && displayVersion is null && uninstallString is null)
                continue;

            return DetectRegistered(view, location, displayVersion, uninstallString);
        }

        return DetectUnregistered();
    }

    private InstallInfo DetectRegistered(RegistryView view, string? location, string? displayVersion, string? uninstallString)
    {
        var notes = new List<string>();
        string? installDir = NormalizeDir(location) ?? NormalizeDir(DirectoryOf(Unquote(uninstallString)));
        QbtVersion? registryVersion = QbtVersion.TryParse(displayVersion, out QbtVersion parsed) ? parsed : null;
        if (displayVersion is not null && registryVersion is null)
            notes.Add($"DisplayVersion '{displayVersion}' is not a release version.");

        if (installDir is null)
        {
            notes.Add(T.Get("NoteNoInstallLocation"));
            return new InstallInfo { Kind = InstallKind.NotOfficial, RegistryView = view, RegistryVersion = registryVersion, Notes = notes };
        }

        string exePath = Path.Combine(installDir, "qbittorrent.exe");
        var info = new InstallInfo
        {
            Kind = InstallKind.Installed,
            InstallDir = installDir,
            ExePath = exePath,
            RegistryView = view,
            RegistryVersion = registryVersion,
            HasPortableProfile = environment.DirectoryExists(Path.Combine(installDir, "profile")),
            Notes = notes,
        };

        if (IsUnder(installDir, environment.ProgramFilesX86))
        {
            notes.Add(T.Get("NoteLegacy32"));
            return info with { Kind = InstallKind.Legacy32Bit };
        }

        if (!environment.FileExists(exePath))
        {
            notes.Add(T.Get("NoteExeMissing"));
            return info with { ExeMissing = true };
        }

        ExeMetadata metadata = environment.GetExeMetadata(exePath);
        BinaryScanResult scan = environment.ScanExe(exePath);
        info = info with { Metadata = metadata, FileVersion = metadata.FileVersion, Scan = scan };

        if (metadata.Machine == PeMachine.I386)
        {
            notes.Add(T.Get("NoteLegacy32"));
            return info with { Kind = InstallKind.Legacy32Bit };
        }

        if (metadata.FileVersion?.Build >= ForkBuildThreshold || scan.HasForkMarkers)
        {
            notes.Add(T.Get("NoteFork"));
            return info with { Kind = InstallKind.Fork };
        }

        if (!string.Equals(metadata.CompanyName, OfficialCompany, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(metadata.ProductName, OfficialProduct, StringComparison.OrdinalIgnoreCase))
        {
            notes.Add(T.Format("NoteNotOfficial", metadata.ProductName, metadata.CompanyName));
            return info with { Kind = InstallKind.NotOfficial };
        }

        if (metadata.FileVersion is null)
            notes.Add("qbittorrent.exe has no version resource; the registry version is used.");
        else if (registryVersion is not null && metadata.FileVersion != registryVersion)
            notes.Add($"Registry says {registryVersion}, qbittorrent.exe is {metadata.FileVersion}: the file wins.");

        if (scan.Variant is null)
            notes.Add("The libtorrent series could not be read from qbittorrent.exe.");
        else if (scan.IsPartial)
            notes.Add("Only one libtorrent marker was found in qbittorrent.exe.");

        if (info.HasPortableProfile)
            notes.Add("A 'profile' folder sits next to qbittorrent.exe: qBittorrent runs in portable mode from the install folder.");

        return info;
    }

    private InstallInfo DetectUnregistered()
    {
        foreach (string path in environment.RunningQbittorrentPaths())
        {
            string? dir = Path.GetDirectoryName(path);
            if (dir is null)
                continue;
            bool profile = environment.DirectoryExists(Path.Combine(dir, "profile"));
            return new InstallInfo
            {
                Kind = InstallKind.Portable,
                InstallDir = dir,
                ExePath = path,
                HasPortableProfile = profile,
                Notes = [T.Get(profile ? "NotePortable" : "NoteUnregistered")],
            };
        }

        string legacyExe = Path.Combine(environment.ProgramFilesX86, "qBittorrent", "qbittorrent.exe");
        if (environment.FileExists(legacyExe))
        {
            return new InstallInfo
            {
                Kind = InstallKind.Legacy32Bit,
                InstallDir = Path.GetDirectoryName(legacyExe),
                ExePath = legacyExe,
                Notes = [T.Get("NoteLegacy32")],
            };
        }

        return new InstallInfo { Kind = InstallKind.NotInstalled };
    }

    private static string? Unquote(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        value = value.Trim();
        if (value.StartsWith('"'))
        {
            int end = value.IndexOf('"', 1);
            return end > 1 ? value[1..end] : null;
        }
        return value;
    }

    private static string? DirectoryOf(string? path) => path is null ? null : Path.GetDirectoryName(path);

    private static string? NormalizeDir(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
            return null;
        dir = dir.Trim().Trim('"');
        return Path.IsPathFullyQualified(dir) ? Path.TrimEndingDirectorySeparator(dir) : null;
    }

    private static bool IsUnder(string path, string root) =>
        !string.IsNullOrEmpty(root)
        && (string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
}
