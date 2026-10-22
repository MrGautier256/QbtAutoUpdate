using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;

namespace QbtAutoUpdate.Core;

/// <summary>
/// The only hosts the tool talks to. Checked for the first URL and again for every redirect hop.
/// </summary>
public static class HostPolicy
{
    private static readonly string[] ExactHosts =
    [
        "www.qbittorrent.org",
        "qbittorrent.github.io",
        "sourceforge.net",
        "downloads.sourceforge.net",
    ];

    /// <summary>SourceForge mirrors, e.g. netix.dl.sourceforge.net or sf-eu-introserv-1.dl.sourceforge.net.</summary>
    private const string MirrorSuffix = ".dl.sourceforge.net";

    public static bool IsAllowed(Uri uri)
    {
        if (!uri.IsAbsoluteUri
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || uri.UserInfo.Length > 0
            || uri.HostNameType != UriHostNameType.Dns)
        {
            return false;
        }

        string host = uri.IdnHost.ToLowerInvariant();
        if (ExactHosts.Contains(host, StringComparer.Ordinal))
            return true;

        if (!host.EndsWith(MirrorSuffix, StringComparison.Ordinal))
            return false;

        string mirror = host[..^MirrorSuffix.Length];
        return mirror.Length > 0 && mirror.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    }
}

public sealed class BlockedUrlException(Uri uri)
    : Exception($"Refusing to contact '{uri.Scheme}://{uri.Host}': not an allowed HTTPS host.")
{
    public Uri Uri { get; } = uri;
}

public sealed class HttpStatusException(HttpStatusCode status, string host)
    : Exception($"HTTP {(int)status} from {host}.")
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// HttpClient wrapper that follows redirects itself so that every hop is checked against
/// <see cref="HostPolicy"/>. Uses the system proxy settings of the current user.
/// </summary>
public sealed class SafeHttpClient : IDisposable
{
    public const int MaxRedirects = 8;

    private readonly HttpClient _http;
    private readonly ILog _log;

    public SafeHttpClient(ILog log, HttpMessageHandler? handler = null)
    {
        _log = log;
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        // A descriptive user agent: SourceForge's Cloudflare front answers 403 to a bare "Mozilla/5.0".
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("QbtAutoUpdate", ProductVersion));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/MrGautier256/QbtAutoUpdate)"));
    }

    public static string ProductVersion { get; } =
        typeof(SafeHttpClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    /// <summary>
    /// Sends a GET and follows redirects. Returns the final response with its headers read;
    /// the caller owns it and must check the status code (see <see cref="EnsureSuccess"/>).
    /// </summary>
    public async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        Uri current = uri;
        for (int hop = 0; ; hop++)
        {
            if (!HostPolicy.IsAllowed(current))
                throw new BlockedUrlException(current);

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (!IsRedirect(response.StatusCode))
            {
                _log.Debug($"GET {current.Host}{current.AbsolutePath} -> {(int)response.StatusCode}");
                return response;
            }

            Uri? location = response.Headers.Location;
            response.Dispose();
            if (location is null)
                throw new HttpRequestException($"Redirect from {current.Host} without a Location header.");
            if (hop >= MaxRedirects)
                throw new HttpRequestException($"Too many redirects starting at {uri.Host}.");

            Uri next = location.IsAbsoluteUri ? location : new Uri(current, location);
            _log.Debug($"GET {current.Host}{current.AbsolutePath} -> redirect to {next.Host}");
            current = next;
        }
    }

    /// <summary>Downloads a small text document (JSON, RSS, HTML) with a size cap and a timeout.</summary>
    public async Task<string> GetStringAsync(Uri uri, int maxBytes, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        using HttpResponseMessage response = await GetAsync(uri, timeoutSource.Token).ConfigureAwait(false);
        EnsureSuccess(response);

        await using Stream stream = await response.Content.ReadAsStreamAsync(timeoutSource.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await CopyWithLimitAsync(stream, buffer, maxBytes, timeoutSource.Token).ConfigureAwait(false);
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpStatusException(response.StatusCode, response.RequestMessage?.RequestUri?.Host ?? "?");
    }

    /// <summary>Copies at most <paramref name="maxBytes"/> bytes; throws when the source is larger.</summary>
    public static async Task<long> CopyWithLimitAsync(Stream source, Stream destination, long maxBytes, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new InvalidDataException($"Response exceeds the {maxBytes} byte limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return total;
    }

    public void Dispose() => _http.Dispose();

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
