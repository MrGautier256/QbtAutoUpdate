using System.Net;
using System.Text;
using QbtAutoUpdate.Core;

namespace QbtAutoUpdate.Tests;

public class WebUiClientTests
{
    private const string Base = "http://localhost:8080/";

    [Theory]
    [InlineData("http://localhost:8080", true)]
    [InlineData("https://127.0.0.1:8443/qbt/", true)]
    [InlineData("http://[::1]:8080", true)]
    [InlineData("http://192.168.1.10:8080", false)]
    [InlineData("http://nas.local:8080", false)]
    [InlineData("ftp://localhost", false)]
    [InlineData("http://user:pw@localhost:8080", false)]
    public void Only_local_urls_are_accepted(string url, bool allowed) => Assert.Equal(allowed, WebUiClient.IsAllowedUrl(new Uri(url)));

    [Fact]
    public async Task Api_key_travels_as_a_bearer_token_without_login()
    {
        string? authorization = null, origin = null;
        var handler = new FakeHttpHandler().On(Base + "api/v2/app/shutdown", request =>
        {
            authorization = request.Headers.Authorization?.ToString();
            origin = request.Headers.GetValues("Origin").Single();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new WebUiClient(new WebUiSettings(new Uri("http://localhost:8080"), ApiKey: "secret-key"), handler);

        await client.ShutdownAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Bearer secret-key", authorization);
        Assert.Equal("http://localhost:8080", origin);
        Assert.DoesNotContain(handler.Requests, r => r.AbsolutePath.Contains("auth/login", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Username_and_password_log_in_first()
    {
        string? form = null;
        var handler = new FakeHttpHandler()
            .On(Base + "api/v2/auth/login", request =>
            {
                form = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Ok.") };
            })
            .OnText(Base + "api/v2/app/version", "v5.2.4", "text/plain");
        using var client = new WebUiClient(new WebUiSettings(new Uri("http://localhost:8080"), Username: "admin", Password: "p@ss"), handler);

        Assert.Equal("v5.2.4", await client.GetVersionAsync(TestContext.Current.CancellationToken));
        Assert.Equal("username=admin&password=p%40ss", form);
    }

    [Fact]
    public async Task A_rejected_login_is_reported()
    {
        var handler = new FakeHttpHandler().OnText(Base + "api/v2/auth/login", "Fails.", "text/plain");
        using var client = new WebUiClient(new WebUiSettings(new Uri("http://localhost:8080"), Username: "admin", Password: "wrong"), handler);

        var error = await Assert.ThrowsAsync<WebUiException>(() => client.ShutdownAsync(TestContext.Current.CancellationToken));
        Assert.Contains("rejected", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reads_the_libtorrent_version()
    {
        const string BuildInfo = """{"bitness":64,"boost":"1.86.0","libtorrent":"1.2.20.0","openssl":"3.5.4","platform":"windows","qt":"6.10.3","zlib":"1.3.1"}""";
        var handler = new FakeHttpHandler().OnText(Base + "api/v2/app/buildInfo", BuildInfo);
        using var client = new WebUiClient(new WebUiSettings(new Uri("http://localhost:8080")), handler);

        string? libtorrent = await client.GetLibtorrentVersionAsync(TestContext.Current.CancellationToken);

        Assert.Equal("1.2.20.0", libtorrent);
        Assert.Equal(Variant.Lt12, UpdatePlanner.VariantFromLibtorrentVersion(libtorrent));
    }

    [Fact]
    public async Task A_url_with_a_path_keeps_it()
    {
        var handler = new FakeHttpHandler().OnText("https://localhost:8443/qbt/api/v2/app/version", "v5.3.0", "text/plain");
        using var client = new WebUiClient(new WebUiSettings(new Uri("https://localhost:8443/qbt"), ApiKey: "k"), handler);

        Assert.Equal("v5.3.0", await client.GetVersionAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Forbidden_is_a_credentials_problem()
    {
        var handler = new FakeHttpHandler().OnStatus(Base + "api/v2/app/version", HttpStatusCode.Forbidden);
        using var client = new WebUiClient(new WebUiSettings(new Uri("http://localhost:8080"), ApiKey: "bad"), handler);

        var error = await Assert.ThrowsAsync<WebUiException>(() => client.GetVersionAsync(TestContext.Current.CancellationToken));
        Assert.Contains("API key", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.2.20.0", Variant.Lt12)]
    [InlineData("2.0.13.0", Variant.Lt2)]
    [InlineData("2.1.2.0", Variant.Lt2)]
    [InlineData("3.0.0.0", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Libtorrent_version_to_series(string? version, Variant? expected) =>
        Assert.Equal(expected, UpdatePlanner.VariantFromLibtorrentVersion(version));

    [Fact]
    public void Webui_secrets_are_stored_encrypted()
    {
        string path = Path.Combine(Path.GetTempPath(), "QbtAutoUpdate.Tests", Guid.NewGuid().ToString("N"), "user.json");
        var settings = new UserSettings { WebUi = new WebUiSettings(new Uri("http://localhost:8080/"), ApiKey: "very-secret-key", Username: null, Password: null) };

        settings.Save(path);
        string json = File.ReadAllText(path, Encoding.UTF8);
        UserSettings loaded = UserSettings.Load(path, []);

        Assert.DoesNotContain("very-secret-key", json, StringComparison.Ordinal);
        Assert.Contains("apiKeyProtected", json, StringComparison.Ordinal);
        Assert.Equal(settings.WebUi, loaded.WebUi);
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public void A_secret_that_cannot_be_decrypted_disables_the_webui()
    {
        string path = Path.Combine(Path.GetTempPath(), "QbtAutoUpdate.Tests", Guid.NewGuid().ToString("N"), "user.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"webUi": {"url": "http://localhost:8080/", "apiKeyProtected": "AAAAbm90IGRwYXBp"}}""");
        var warnings = new List<string>();

        Assert.Null(UserSettings.Load(path, warnings).WebUi);
        Assert.Single(warnings);
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }
}
