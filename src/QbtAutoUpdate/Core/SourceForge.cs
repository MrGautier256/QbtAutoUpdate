using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace QbtAutoUpdate.Core;

/// <summary>One file of a qbittorrent-win32/qbittorrent-{version} folder on SourceForge.</summary>
public sealed record RemoteFile(string Name, long? Size, string? Sha256, DateTimeOffset? Published, bool Staged = false);

public enum ListingSource
{
    Rss,
    FolderPage,
}

/// <summary>Files of a version folder. <see cref="FolderMissing"/> means SourceForge answered 404.</summary>
public sealed record FolderListing(QbtVersion Version, IReadOnlyList<RemoteFile> Files, ListingSource Source, bool FolderMissing)
{
    public RemoteFile? Find(string name) =>
        Files.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Lists the files SourceForge hosts for a version. The folder RSS feed is the primary source
/// (names, sizes, upload dates); the folder HTML page, which embeds the same list as JSON with
/// SHA-256 sums but no dates, is the fallback.
/// </summary>
public sealed partial class SourceForgeClient(SafeHttpClient http, ILog log)
{
    private const int MaxListingBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static Uri FolderRssUrl(QbtVersion version) =>
        new($"https://sourceforge.net/projects/qbittorrent/rss?path=/qbittorrent-win32/qbittorrent-{version}");

    public static Uri FolderPageUrl(QbtVersion version) =>
        new($"https://sourceforge.net/projects/qbittorrent/files/qbittorrent-win32/qbittorrent-{version}/");

    /// <summary>Direct download URL; SourceForge redirects it to a random *.dl.sourceforge.net mirror.</summary>
    public static Uri DownloadUrl(QbtVersion version, string fileName) =>
        new($"https://downloads.sourceforge.net/project/qbittorrent/qbittorrent-win32/qbittorrent-{version}/{Uri.EscapeDataString(fileName)}");

    public static Uri ChangelogUrl(QbtVersion version) =>
        new($"https://sourceforge.net/projects/qbittorrent/files/qbittorrent-win32/qbittorrent-{version}/README/download");

    public async Task<FolderListing> ListAsync(QbtVersion version, CancellationToken cancellationToken)
    {
        try
        {
            string rss = await http.GetStringAsync(FolderRssUrl(version), MaxListingBytes, Timeout, cancellationToken).ConfigureAwait(false);
            return new FolderListing(version, ParseRss(rss, version), ListingSource.Rss, FolderMissing: false);
        }
        catch (HttpStatusException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
        {
            log.Info($"SourceForge has no folder for {version} yet (RSS 404).");
            return new FolderListing(version, [], ListingSource.Rss, FolderMissing: true);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && (VersionSource.IsNetworkFailure(ex) || ex is XmlException))
        {
            log.Warn($"SourceForge RSS for {version} failed ({ex.Message}); trying the folder page.");
        }

        try
        {
            string html = await http.GetStringAsync(FolderPageUrl(version), MaxListingBytes, Timeout, cancellationToken).ConfigureAwait(false);
            return new FolderListing(version, ParseFolderPage(html, version), ListingSource.FolderPage, FolderMissing: false);
        }
        catch (HttpStatusException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
        {
            return new FolderListing(version, [], ListingSource.FolderPage, FolderMissing: true);
        }
    }

    /// <summary>Parses the folder RSS feed. Items outside the version folder are ignored.</summary>
    internal static List<RemoteFile> ParseRss(string xml, QbtVersion version)
    {
        XNamespace media = "http://video.search.yahoo.com/mrss/";
        string prefix = $"/qbittorrent-win32/qbittorrent-{version}/";

        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        XDocument document = XDocument.Load(reader);

        var files = new List<RemoteFile>();
        foreach (XElement item in document.Root?.Element("channel")?.Elements("item") ?? [])
        {
            string? title = item.Element("title")?.Value.Trim();
            if (title is null || !title.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            string name = title[prefix.Length..];
            if (!IsPlainFileName(name))
                continue;

            long? size = long.TryParse(item.Element(media + "content")?.Attribute("filesize")?.Value,
                                       NumberStyles.None, CultureInfo.InvariantCulture, out long parsed)
                ? parsed
                : null;
            files.Add(new RemoteFile(name, size, Sha256: null, ParseRssDate(item.Element("pubDate")?.Value)));
        }
        return files;
    }

    /// <summary>
    /// Parses the <c>net.sf.files = {...};</c> object embedded in the folder page.
    /// </summary>
    internal static List<RemoteFile> ParseFolderPage(string html, QbtVersion version)
    {
        const string Marker = "net.sf.files = ";
        int start = html.IndexOf(Marker, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidDataException("The SourceForge folder page no longer embeds net.sf.files.");

        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(html[(start + Marker.Length)..]), new JsonReaderOptions { MaxDepth = 8 });
        using JsonDocument document = JsonDocument.ParseValue(ref reader);

        string expectedPath = $"/qbittorrent-win32/qbittorrent-{version}";
        var files = new List<RemoteFile>();
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            JsonElement entry = property.Value;
            if (entry.ValueKind != JsonValueKind.Object
                || GetString(entry, "type") != "f"
                || GetString(entry, "path") != expectedPath
                || GetString(entry, "name") is not { } name
                || !IsPlainFileName(name))
            {
                continue;
            }

            string? sha256 = GetString(entry, "sha256");
            if (sha256 is not null && !Sha256Regex().IsMatch(sha256))
                sha256 = null;
            bool staged = entry.TryGetProperty("staged", out JsonElement s) && s.ValueKind == JsonValueKind.True;
            files.Add(new RemoteFile(name, Size: null, sha256?.ToLowerInvariant(), Published: null, staged));
        }
        return files;
    }

    /// <summary>
    /// Parses an RSS date. SourceForge writes "Tue, 20 Oct 2026 12:52:43 UT": "UT" is valid RFC 822
    /// but unknown to .NET, so zone names are mapped to numeric offsets first.
    /// </summary>
    internal static DateTimeOffset? ParseRssDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        string value = text.Trim();
        int lastSpace = value.LastIndexOf(' ');
        if (lastSpace < 0)
            return null;

        string zone = value[(lastSpace + 1)..];
        string? offset = zone switch
        {
            "UT" or "UTC" or "GMT" or "Z" => "+00:00",
            _ when IsNumericOffset(zone) => zone[..3] + ":" + zone[3..],
            _ => null,
        };
        if (offset is null)
            return null;

        string normalized = value[..lastSpace] + " " + offset;
        string[] formats =
        [
            "ddd, d MMM yyyy HH:mm:ss zzz", "ddd, dd MMM yyyy HH:mm:ss zzz",
            "d MMM yyyy HH:mm:ss zzz", "dd MMM yyyy HH:mm:ss zzz",
        ];
        return DateTimeOffset.TryParseExact(normalized, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTimeOffset result)
            ? result
            : null;
    }

    /// <summary>"+0200" or "-0500".</summary>
    private static bool IsNumericOffset(string zone) =>
        zone.Length == 5 && (zone[0] == '+' || zone[0] == '-') && zone.Skip(1).All(char.IsAsciiDigit);

    private static bool IsPlainFileName(string name) =>
        name.Length is > 0 and < 200 && name.IndexOfAny(['/', '\\', ':']) < 0 && name != "." && name != "..";

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Regex();
}
