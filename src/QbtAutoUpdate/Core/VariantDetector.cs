using System.Text;

namespace QbtAutoUpdate.Core;

/// <summary>What a scan of qbittorrent.exe found (docs/FINDINGS.md §6).</summary>
public sealed record BinaryScanResult(
    IReadOnlyList<string> LibtorrentVersions,
    IReadOnlyList<string> PeerFingerprints,
    bool HasForkMarkers)
{
    public static readonly BinaryScanResult Empty = new([], [], false);

    /// <summary>
    /// The libtorrent series, or null when the binary gives no answer or contradicts itself.
    /// Both markers are normally present; one alone is accepted, two that disagree are not.
    /// </summary>
    public Variant? Variant
    {
        get
        {
            var majors = new HashSet<char>();
            foreach (string version in LibtorrentVersions)
                majors.Add(version[0]);
            foreach (string fingerprint in PeerFingerprints)
                majors.Add(fingerprint[3]);

            return majors.Count != 1 ? null : majors.Single() switch
            {
                '1' => Core.Variant.Lt12,
                '2' => Core.Variant.Lt2,
                _ => null,
            };
        }
    }

    /// <summary>True when only one of the two markers was found (logged as a weaker detection).</summary>
    public bool IsPartial => LibtorrentVersions.Count == 0 || PeerFingerprints.Count == 0;
}

/// <summary>
/// Scans a qbittorrent.exe for strings compiled in by libtorrent and by the Enhanced Edition fork:
/// <list type="bullet">
/// <item><c>libtorrent/2.1.2.0</c>: libtorrent's default user agent (ASCII, NUL-terminated).</item>
/// <item><c>-LT2120-</c>: libtorrent's default peer-id fingerprint; the first digit is the major version.</item>
/// <item><c>AutoBanUnknownPeer</c>, <c>ShadowBan</c>: settings keys that only exist in the fork (ASCII or UTF-16).</item>
/// </list>
/// The file is streamed in chunks; matches straddling two chunks are found thanks to a carried-over tail.
/// </summary>
public static class QbtBinaryScanner
{
    private const int ChunkSize = 1 << 20;
    private const int Carry = 128;
    private const int MaxTail = 24;

    private static readonly byte[] LibtorrentPrefix = "libtorrent/"u8.ToArray();
    private static readonly byte[] FingerprintPrefix = "-LT"u8.ToArray();
    private static readonly byte[][] ForkMarkers =
    [
        "AutoBanUnknownPeer"u8.ToArray(),
        "ShadowBan"u8.ToArray(),
        Encoding.Unicode.GetBytes("AutoBanUnknownPeer"),
        Encoding.Unicode.GetBytes("ShadowBan"),
    ];

    public static BinaryScanResult Scan(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920);
        return Scan(stream);
    }

    public static BinaryScanResult Scan(Stream stream)
    {
        var versions = new List<string>();
        var fingerprints = new List<string>();
        bool forkMarkers = false;
        var seen = new HashSet<(int Pattern, long Offset)>();

        byte[] buffer = new byte[ChunkSize + Carry];
        int carried = 0;
        long bufferStart = 0;
        while (true)
        {
            int read = stream.ReadAtLeast(buffer.AsSpan(carried, ChunkSize), ChunkSize, throwOnEndOfStream: false);
            int length = carried + read;
            bool atEnd = read < ChunkSize;
            ReadOnlySpan<byte> window = buffer.AsSpan(0, length);

            foreach (int index in IndexesOf(window, LibtorrentPrefix))
            {
                int tailStart = index + LibtorrentPrefix.Length;
                if (!atEnd && length - tailStart < MaxTail)
                    continue; // found again, complete, in the next window
                if (seen.Add((0, bufferStart + index)) && ReadVersion(window[tailStart..]) is { } version && !versions.Contains(version))
                    versions.Add(version);
            }

            foreach (int index in IndexesOf(window, FingerprintPrefix))
            {
                if (!atEnd && length - index < FingerprintPrefix.Length + MaxTail)
                    continue;
                if (seen.Add((1, bufferStart + index)) && ReadFingerprint(window, index) is { } fingerprint && !fingerprints.Contains(fingerprint))
                    fingerprints.Add(fingerprint);
            }

            if (!forkMarkers)
            {
                foreach (byte[] marker in ForkMarkers)
                {
                    if (window.IndexOf(marker) >= 0)
                    {
                        forkMarkers = true;
                        break;
                    }
                }
            }

            if (atEnd)
                break;

            int keep = Math.Min(Carry, length);
            buffer.AsSpan(length - keep, keep).CopyTo(buffer);
            bufferStart += length - keep;
            carried = keep;
        }

        return new BinaryScanResult(versions, fingerprints, forkMarkers);
    }

    /// <summary>"1.2.20.0" followed by a NUL.</summary>
    private static string? ReadVersion(ReadOnlySpan<byte> tail)
    {
        int end = tail.IndexOf((byte)0);
        if (end is < 5 or > 20)
            return null;
        string text = Encoding.ASCII.GetString(tail[..end]);
        string[] parts = text.Split('.');
        return parts.Length is 3 or 4 && parts.All(p => p.Length is > 0 and <= 3 && p.All(char.IsAsciiDigit)) ? text : null;
    }

    /// <summary>A NUL-delimited "-LT" + four alphanumerics + "-", e.g. "-LT12K0-".</summary>
    private static string? ReadFingerprint(ReadOnlySpan<byte> window, int index)
    {
        const int Length = 8;
        if (index + Length >= window.Length || (index > 0 && window[index - 1] != 0) || window[index + Length] != 0)
            return null;
        ReadOnlySpan<byte> candidate = window.Slice(index, Length);
        if (candidate[7] != (byte)'-' || !char.IsAsciiDigit((char)candidate[3]))
            return null;
        for (int i = 4; i < 7; i++)
        {
            if (!char.IsAsciiLetterOrDigit((char)candidate[i]))
                return null;
        }
        return Encoding.ASCII.GetString(candidate);
    }

    private static List<int> IndexesOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        var indexes = new List<int>();
        int offset = 0;
        while (offset <= haystack.Length - needle.Length)
        {
            int found = haystack[offset..].IndexOf(needle);
            if (found < 0)
                break;
            indexes.Add(offset + found);
            offset += found + 1;
        }
        return indexes;
    }
}
