namespace QbtAutoUpdate.Core;

/// <summary>
/// A build variant is the libtorrent major series, never a file-name suffix: the meaning of the
/// suffixes flipped in 5.3.0 (see docs/FINDINGS.md §3.3).
/// </summary>
public enum Variant
{
    /// <summary>libtorrent 1.x (1.2 series).</summary>
    Lt12,

    /// <summary>libtorrent 2.x (2.0 series up to 5.2.x, 2.1 series from 5.3.0).</summary>
    Lt2,
}

public enum CpuArch
{
    X64,
    Arm64,
}

public static class VariantText
{
    /// <summary>Value used in config.json and state.json.</summary>
    public static string ToConfigValue(this Variant variant) => variant == Variant.Lt12 ? "lt12" : "lt2";

    public static bool TryParseConfigValue(string? text, out Variant variant)
    {
        switch (text)
        {
            case "lt12":
                variant = Variant.Lt12;
                return true;
            case "lt2":
                variant = Variant.Lt2;
                return true;
            default:
                variant = default;
                return false;
        }
    }

    public static string Describe(this Variant variant) => variant == Variant.Lt12 ? "libtorrent 1.2" : "libtorrent 2.x";

    public static string FileToken(this CpuArch arch) => arch == CpuArch.Arm64 ? "arm64" : "x64";
}
