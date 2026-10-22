namespace QbtAutoUpdate.Core;

/// <summary>
/// One known installer name: <c>qbittorrent_{version}_{Infix}{arch}_setup.exe</c> for versions in
/// [<see cref="From"/>, <see cref="Until"/>).
/// </summary>
public sealed record FileRule(QbtVersion From, QbtVersion? Until, string Infix, Variant Variant, CpuArch Arch)
{
    public bool Covers(QbtVersion version) => version >= From && (Until is null || version < Until.Value);

    public string FileName(QbtVersion version) => $"qbittorrent_{version}_{Infix}{Arch.FileToken()}_setup.exe";
}

public sealed record ClassifiedFile(RemoteFile File, Variant Variant, CpuArch Arch);

public enum ResolveStatus
{
    /// <summary>Installer and signature found.</summary>
    Found,

    /// <summary>Folder missing, empty, still staged, or signature not uploaded yet: retry on the next run.</summary>
    NotYetAvailable,

    /// <summary>The version has installers, but none of the requested variant for this machine.</summary>
    VariantMissing,
}

public sealed record ResolvedRelease(
    ResolveStatus Status,
    QbtVersion Version,
    Variant Variant,
    RemoteFile? Installer = null,
    RemoteFile? Signature = null,
    CpuArch? InstallerArch = null,
    IReadOnlyList<string>? UnknownFiles = null,
    string? Reason = null);

/// <summary>
/// Picks the installer of the right variant and architecture from a SourceForge folder listing.
/// All naming knowledge lives in <see cref="Rules"/>, built from the real listings of every release
/// from 4.6.0 to 5.3.0 (see docs/FINDINGS.md §3.3 and the test fixtures).
/// </summary>
public static class ReleaseResolver
{
    private static QbtVersion V(int major, int minor, int patch) => new(major, minor, patch, 0);

    /// <summary>
    /// Known installer names. Rows marked "derived" come from dist/windows/config.nsh
    /// (<c>${QBT_VERSION}_${QBT_CPU_ARCH}</c>) and have never been published so far.
    /// </summary>
    public static readonly IReadOnlyList<FileRule> Rules =
    [
        // 4.6.0 – 5.0.0 (observed): default = libtorrent 1.2, variant "lt20_qt6" = libtorrent 2.0.
        new(V(4, 6, 0), V(5, 0, 1), "", Variant.Lt12, CpuArch.X64),
        new(V(4, 6, 0), V(5, 0, 1), "lt20_qt6_", Variant.Lt2, CpuArch.X64),

        // 5.0.1 – 5.1.4 (observed): variant renamed "qt6_lt20".
        new(V(5, 0, 1), V(5, 2, 0), "", Variant.Lt12, CpuArch.X64),
        new(V(5, 0, 1), V(5, 2, 0), "qt6_lt20_", Variant.Lt2, CpuArch.X64),

        // 5.2.0 – 5.2.4 (observed): variant renamed "lt20". The NSIS script gained arm64 support in 5.2.0 (derived).
        new(V(5, 2, 0), V(5, 3, 0), "", Variant.Lt12, CpuArch.X64),
        new(V(5, 2, 0), V(5, 3, 0), "lt20_", Variant.Lt2, CpuArch.X64),
        new(V(5, 2, 0), V(5, 3, 0), "", Variant.Lt12, CpuArch.Arm64),
        new(V(5, 2, 0), V(5, 3, 0), "lt20_", Variant.Lt2, CpuArch.Arm64),

        // 5.3.0 and later (observed): the default switched to libtorrent 2.1, variant "lt12" = libtorrent 1.2.
        new(V(5, 3, 0), null, "", Variant.Lt2, CpuArch.X64),
        new(V(5, 3, 0), null, "lt12_", Variant.Lt12, CpuArch.X64),
        new(V(5, 3, 0), null, "", Variant.Lt2, CpuArch.Arm64),
        new(V(5, 3, 0), null, "lt12_", Variant.Lt12, CpuArch.Arm64),
    ];

    /// <summary>Identifies an installer name for a version, or returns null for unknown names.</summary>
    public static (Variant Variant, CpuArch Arch)? Classify(QbtVersion version, string fileName)
    {
        foreach (FileRule rule in Rules)
        {
            if (rule.Covers(version) && string.Equals(rule.FileName(version), fileName, StringComparison.OrdinalIgnoreCase))
                return (rule.Variant, rule.Arch);
        }
        return null;
    }

    /// <summary>Architectures able to run on the machine, best first: arm64 machines also run x64 in emulation.</summary>
    public static IReadOnlyList<CpuArch> CompatibleArchs(CpuArch machine) =>
        machine == CpuArch.Arm64 ? [CpuArch.Arm64, CpuArch.X64] : [CpuArch.X64];

    public static ResolvedRelease Resolve(FolderListing listing, Variant variant, CpuArch machine)
    {
        QbtVersion version = listing.Version;
        if (listing.FolderMissing || listing.Files.Count == 0)
            return new ResolvedRelease(ResolveStatus.NotYetAvailable, version, variant, Reason: "the SourceForge folder does not exist or is empty");

        var installers = new List<ClassifiedFile>();
        var unknown = new List<string>();
        foreach (RemoteFile file in listing.Files)
        {
            if (file.Name.EndsWith(".asc", StringComparison.OrdinalIgnoreCase) || file.Name.Equals("README", StringComparison.OrdinalIgnoreCase))
                continue;
            if (Classify(version, file.Name) is { } kind)
                installers.Add(new ClassifiedFile(file, kind.Variant, kind.Arch));
            else
                unknown.Add(file.Name);
        }

        if (installers.Count == 0)
        {
            return new ResolvedRelease(ResolveStatus.NotYetAvailable, version, variant, UnknownFiles: unknown,
                Reason: "no known installer in the folder yet");
        }

        foreach (CpuArch arch in CompatibleArchs(machine))
        {
            ClassifiedFile? match = installers.FirstOrDefault(i => i.Variant == variant && i.Arch == arch);
            if (match is null)
                continue;

            if (match.File.Staged)
            {
                return new ResolvedRelease(ResolveStatus.NotYetAvailable, version, variant, UnknownFiles: unknown,
                    Reason: $"{match.File.Name} is still staged on SourceForge");
            }

            RemoteFile? signature = listing.Find(match.File.Name + ".asc");
            if (signature is null)
            {
                return new ResolvedRelease(ResolveStatus.NotYetAvailable, version, variant, UnknownFiles: unknown,
                    Reason: $"{match.File.Name}.asc is not published yet");
            }

            return new ResolvedRelease(ResolveStatus.Found, version, variant, match.File, signature, arch, unknown);
        }

        return new ResolvedRelease(ResolveStatus.VariantMissing, version, variant, UnknownFiles: unknown,
            Reason: $"{version} has no {variant.Describe()} installer for {machine.FileToken()}");
    }
}
