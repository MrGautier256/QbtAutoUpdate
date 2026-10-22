using System.Text.Json;

namespace QbtAutoUpdate.Core;

public sealed record VersionSourceResult(Uri Url, QbtVersion? Version, string? Error);

public sealed record LatestVersion(QbtVersion Version, IReadOnlyList<VersionSourceResult> Sources);

public sealed class VersionSourceException(IReadOnlyList<VersionSourceResult> sources)
    : Exception("No version source answered with a valid version: " + string.Join("; ", sources.Select(s => $"{s.Url.Host}: {s.Error}")))
{
    public IReadOnlyList<VersionSourceResult> Sources { get; } = sources;
}

/// <summary>
/// Reads the latest Windows release from the same two files qBittorrent's own updater uses
/// (src/gui/programupdater.cpp): both are queried, the highest valid version wins, and one
/// of them may fail.
/// </summary>
public sealed class VersionSource(SafeHttpClient http, ILog log)
{
    public static readonly Uri MainUrl = new("https://www.qbittorrent.org/versions.json");
    public static readonly Uri BackupUrl = new("https://qbittorrent.github.io/qBittorrent-website/versions.json");

    private const int MaxBytes = 64 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task<LatestVersion> GetLatestAsync(CancellationToken cancellationToken)
    {
        VersionSourceResult[] results = await Task.WhenAll(
            FetchAsync(MainUrl, cancellationToken),
            FetchAsync(BackupUrl, cancellationToken)).ConfigureAwait(false);

        List<QbtVersion> versions = results.Where(r => r.Version.HasValue).Select(r => r.Version!.Value).ToList();
        if (versions.Count == 0)
            throw new VersionSourceException(results);

        return new LatestVersion(versions.Max(), results);
    }

    private async Task<VersionSourceResult> FetchAsync(Uri url, CancellationToken cancellationToken)
    {
        try
        {
            string json = await http.GetStringAsync(url, MaxBytes, Timeout, cancellationToken).ConfigureAwait(false);
            QbtVersion? version = ParseWindowsVersion(json, out string? error);
            if (version is null)
                log.Warn($"{url.Host}: unexpected versions.json content ({error}).");
            return new VersionSourceResult(url, version, error);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsNetworkFailure(ex))
        {
            log.Warn($"{url.Host}: {ex.Message}");
            return new VersionSourceResult(url, null, ex.Message);
        }
    }

    /// <summary>Errors that mean "this source is unavailable right now" (timeouts surface as cancellations).</summary>
    internal static bool IsNetworkFailure(Exception ex) =>
        ex is HttpRequestException or HttpStatusException or BlockedUrlException or InvalidDataException or OperationCanceledException or IOException;

    /// <summary>Extracts <c>win.version</c> from versions.json. Returns null if the format changed.</summary>
    internal static QbtVersion? ParseWindowsVersion(string json, out string? error)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("win", out JsonElement win)
                || win.ValueKind != JsonValueKind.Object
                || !win.TryGetProperty("version", out JsonElement version)
                || version.ValueKind != JsonValueKind.String)
            {
                error = "missing \"win.version\" string";
                return null;
            }

            if (!QbtVersion.TryParse(version.GetString(), out QbtVersion parsed))
            {
                error = $"\"win.version\" is not a release version: '{version.GetString()}'";
                return null;
            }

            error = null;
            return parsed;
        }
        catch (JsonException ex)
        {
            error = "invalid JSON: " + ex.Message;
            return null;
        }
    }
}
