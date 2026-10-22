using System.Text;
using QbtAutoUpdate.Core;

namespace QbtAutoUpdate.Tests;

public class BinaryScannerTests
{
    /// <summary>Builds a fake binary: random-ish filler with NUL-terminated strings at given offsets.</summary>
    private static byte[] Binary(int size, params (int Offset, byte[] Bytes)[] strings)
    {
        byte[] data = new byte[size];
        for (int i = 0; i < size; i++)
            data[i] = (byte)(i * 31 % 251 | 0x80); // high bytes: never ASCII text
        foreach ((int offset, byte[] bytes) in strings)
        {
            data[offset - 1] = 0;
            bytes.CopyTo(data, offset);
            data[offset + bytes.Length] = 0;
        }
        return data;
    }

    private static (int, byte[]) At(int offset, string ascii) => (offset, Encoding.ASCII.GetBytes(ascii));

    private static BinaryScanResult Scan(byte[] data) => QbtBinaryScanner.Scan(new MemoryStream(data));

    [Theory]
    [InlineData("libtorrent/1.2.20.0", "-LT12K0-", Variant.Lt12)] // 5.2.3 / 5.2.4 / 5.3.0 lt12
    [InlineData("libtorrent/2.0.13.0", "-LT20D0-", Variant.Lt2)]  // 5.2.3 lt20
    [InlineData("libtorrent/2.1.2.0", "-LT2120-", Variant.Lt2)]   // 5.3.0 default
    public void Reads_the_series_seen_in_real_builds(string userAgent, string fingerprint, Variant expected)
    {
        BinaryScanResult result = Scan(Binary(4096, At(1000, userAgent), At(2000, fingerprint)));

        Assert.Equal(expected, result.Variant);
        Assert.False(result.IsPartial);
        Assert.False(result.HasForkMarkers);
    }

    [Fact]
    public void Ignores_the_look_alikes_found_in_real_builds()
    {
        // Strings present in the real qbittorrent.exe that must not be mistaken for markers.
        byte[] data = Binary(4096, At(100, "-LT0"), At(200, "-LTY/:]"), At(300, "2.5.4.10"), At(400, "A-LT"), At(500, "libtorrent/"));

        BinaryScanResult result = Scan(data);

        Assert.Empty(result.LibtorrentVersions);
        Assert.Empty(result.PeerFingerprints);
        Assert.Null(result.Variant);
    }

    [Fact]
    public void Contradicting_markers_give_no_answer()
    {
        Assert.Null(Scan(Binary(4096, At(1000, "libtorrent/1.2.20.0"), At(2000, "-LT2120-"))).Variant);
    }

    [Fact]
    public void One_marker_is_enough_but_flagged_partial()
    {
        BinaryScanResult result = Scan(Binary(4096, At(2000, "-LT2120-")));

        Assert.Equal(Variant.Lt2, result.Variant);
        Assert.True(result.IsPartial);
    }

    [Fact]
    public void Finds_markers_across_chunk_boundaries()
    {
        const int Chunk = 1 << 20;
        byte[] data = Binary(3 * Chunk, At(Chunk - 6, "libtorrent/1.2.20.0"), At(2 * Chunk - 3, "-LT12K0-"));

        BinaryScanResult result = Scan(data);

        Assert.Equal(["1.2.20.0"], result.LibtorrentVersions);
        Assert.Equal(["-LT12K0-"], result.PeerFingerprints);
    }

    [Fact]
    public void Finds_markers_at_the_very_end()
    {
        byte[] data = Binary(5000, At(4980, "-LT12K0-"));

        Assert.Equal(["-LT12K0-"], Scan(data).PeerFingerprints);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Detects_the_enhanced_edition(bool utf16)
    {
        byte[] marker = utf16 ? Encoding.Unicode.GetBytes("AutoBanUnknownPeer") : Encoding.ASCII.GetBytes("ShadowBan");
        byte[] data = Binary(1 << 21, ((1 << 20) - 10, marker), At(5000, "libtorrent/1.2.20.0"));

        Assert.True(Scan(data).HasForkMarkers);
    }

    [Fact]
    public void An_empty_file_gives_nothing()
    {
        BinaryScanResult result = Scan([]);

        Assert.Null(result.Variant);
        Assert.False(result.HasForkMarkers);
    }
}
