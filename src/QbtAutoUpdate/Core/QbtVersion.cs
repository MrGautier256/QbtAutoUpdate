using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace QbtAutoUpdate.Core;

/// <summary>
/// A qBittorrent release version, normalized to four numeric components so that
/// "5.3.0" equals "5.3.0.0" and sorts below "5.3.0.1". Pre-release suffixes
/// ("5.3.0rc1") are rejected: the tool only ever deals with stable releases.
/// </summary>
public readonly record struct QbtVersion(int Major, int Minor, int Patch, int Build) : IComparable<QbtVersion>
{
    /// <summary>
    /// Parses "5.3.0", "5.1.2.10" or "v5.3.0" (the form found in qbittorrent.exe's FileVersion string).
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out QbtVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        if (span.Length > 0 && (span[0] == 'v' || span[0] == 'V'))
            span = span[1..];

        Span<int> parts = stackalloc int[4];
        int count = 0;
        foreach (Range range in span.Split('.'))
        {
            ReadOnlySpan<char> part = span[range];
            if (count == 4 || part.Length == 0 || part.Length > 9 || !IsAsciiDigits(part))
                return false;
            parts[count++] = int.Parse(part, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        if (count < 2)
            return false;

        version = new QbtVersion(parts[0], parts[1], parts[2], parts[3]);
        return true;
    }

    public static QbtVersion Parse(string text) =>
        TryParse(text, out QbtVersion version) ? version : throw new FormatException($"Invalid qBittorrent version: '{text}'.");

    public int CompareTo(QbtVersion other)
    {
        int result = Major.CompareTo(other.Major);
        if (result == 0) result = Minor.CompareTo(other.Minor);
        if (result == 0) result = Patch.CompareTo(other.Patch);
        if (result == 0) result = Build.CompareTo(other.Build);
        return result;
    }

    public static bool operator <(QbtVersion left, QbtVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(QbtVersion left, QbtVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(QbtVersion left, QbtVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(QbtVersion left, QbtVersion right) => left.CompareTo(right) >= 0;

    /// <summary>
    /// "5.3.0" when the fourth component is zero, "5.1.2.10" otherwise. This matches the
    /// SourceForge folder names (qbittorrent-5.3.0) and the installer file names.
    /// </summary>
    public override string ToString() =>
        Build == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}")
            : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}.{Build}");

    private static bool IsAsciiDigits(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (!char.IsAsciiDigit(c))
                return false;
        }
        return true;
    }
}
