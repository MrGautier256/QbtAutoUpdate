using System.Net;
using System.Text.Json;

namespace QbtAutoUpdate.Core;

/// <summary>
/// WebUI access configured by the user. With an API key (qBittorrent 5.2.0+) no username or
/// password is needed; with neither, the WebUI must allow localhost without authentication.
/// </summary>
public sealed record WebUiSettings(Uri Url, string? ApiKey = null, string? Username = null, string? Password = null)
{
    public string AuthenticationKind => ApiKey is not null ? "API key" : Username is not null ? "username and password" : "none (localhost bypass)";
}

public sealed class WebUiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Minimal client for qBittorrent's WebUI API, used to shut qBittorrent down cleanly and to read
/// its libtorrent version. Only loopback URLs are accepted: the tool only ever talks to the local
/// instance, and credentials must not travel over a network.
/// </summary>
public sealed class WebUiClient : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly WebUiSettings _settings;
    private readonly string _origin;
    private bool _loggedIn;

    public WebUiClient(WebUiSettings settings, HttpMessageHandler? handler = null)
    {
        if (!IsAllowedUrl(settings.Url))
            throw new ArgumentException("The WebUI URL must be http(s) on localhost.", nameof(settings));

        _settings = settings;
        _origin = settings.Url.GetLeftPart(UriPartial.Authority);
        handler ??= new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            ConnectTimeout = TimeSpan.FromSeconds(5),
            // qBittorrent's HTTPS WebUI normally uses a self-signed certificate; loopback only (see constructor check).
            SslOptions = { RemoteCertificateValidationCallback = static (_, _, _, _) => true },
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri(_origin + settings.Url.AbsolutePath.TrimEnd('/') + "/"), Timeout = Timeout };
    }

    public static bool IsAllowedUrl(Uri url) =>
        url.IsAbsoluteUri && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)
        && url.UserInfo.Length == 0 && url.IsLoopback;

    public async Task<string> GetVersionAsync(CancellationToken cancellationToken) =>
        (await SendAsync(HttpMethod.Get, "api/v2/app/version", cancellationToken).ConfigureAwait(false)).Trim();

    /// <summary>The "libtorrent" field of /api/v2/app/buildInfo, e.g. "1.2.20.0".</summary>
    public async Task<string?> GetLibtorrentVersionAsync(CancellationToken cancellationToken)
    {
        string json = await SendAsync(HttpMethod.Get, "api/v2/app/buildInfo", cancellationToken).ConfigureAwait(false);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("libtorrent", out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException ex)
        {
            throw new WebUiException("The WebUI answered buildInfo with invalid JSON.", ex);
        }
    }

    /// <summary>POST /api/v2/app/shutdown: qBittorrent answers, then exits through its normal cleanup.</summary>
    public Task ShutdownAsync(CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "api/v2/app/shutdown", cancellationToken);

    public void Dispose() => _http.Dispose();

    private async Task<string> SendAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        try
        {
            await EnsureLoggedInAsync(cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, path);
            if (method == HttpMethod.Post)
                request.Content = new FormUrlEncodedContent([]);
            AddHeaders(request);

            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new WebUiException($"The WebUI refused the request (HTTP {(int)response.StatusCode}): check the API key or credentials.");
            if (!response.IsSuccessStatusCode)
                throw new WebUiException($"The WebUI answered HTTP {(int)response.StatusCode} to {path}.");
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new WebUiException($"The WebUI at {_origin} is not reachable: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WebUiException($"The WebUI at {_origin} did not answer in time.", ex);
        }
    }

    private async Task EnsureLoggedInAsync(CancellationToken cancellationToken)
    {
        if (_loggedIn || _settings.ApiKey is not null || _settings.Username is null)
            return;

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v2/auth/login")
        {
            Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("username", _settings.Username),
                new KeyValuePair<string, string>("password", _settings.Password ?? ""),
            ]),
        };
        AddHeaders(request);
        using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new WebUiException("The WebUI temporarily banned this address after failed logins.");
        string body = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();
        if (!response.IsSuccessStatusCode || body != "Ok.")
            throw new WebUiException("The WebUI rejected the username or password.");
        _loggedIn = true;
    }

    /// <summary>Same-origin headers satisfy the WebUI's CSRF check; the API key travels as a bearer token.</summary>
    private void AddHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("Origin", _origin);
        request.Headers.Referrer = new Uri(_origin + "/");
        if (_settings.ApiKey is { } key)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
    }
}
