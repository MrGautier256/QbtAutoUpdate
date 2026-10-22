using System.Net;
using System.Text;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using QbtAutoUpdate.Core;

namespace QbtAutoUpdate.Tests;

internal static class Fixtures
{
    public static string PathOf(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "Fixtures", .. parts]);

    public static string Text(params string[] parts) => File.ReadAllText(PathOf(parts));

    public static string Rss(string version) => Text("SourceForge", $"rss_{version}.xml");

    public static readonly string[] RssVersions =
        ["4.6.0", "4.6.7", "5.0.0", "5.0.1", "5.0.5", "5.1.0", "5.1.4", "5.2.0", "5.2.3", "5.2.4", "5.3.0"];
}

/// <summary>Captures log lines for assertions.</summary>
internal sealed class ListLog : ILog
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public void Write(LogLevel level, string message) => Entries.Add((level, message));

    public bool Contains(string text) => Entries.Any(e => e.Message.Contains(text, StringComparison.Ordinal));
}

/// <summary>A scripted HTTP server: each URL answers from a queue (the last answer repeats).</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, List<Func<HttpRequestMessage, HttpResponseMessage>>> _routes = new(StringComparer.Ordinal);

    public List<Uri> Requests { get; } = [];

    public FakeHttpHandler On(string url, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        if (!_routes.TryGetValue(url, out var queue))
            _routes[url] = queue = [];
        queue.Add(respond);
        return this;
    }

    public FakeHttpHandler OnText(string url, string body, string mediaType = "application/json", HttpStatusCode status = HttpStatusCode.OK) =>
        On(url, _ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) });

    public FakeHttpHandler OnBytes(string url, byte[] body, string mediaType = "application/octet-stream") =>
        On(url, _ =>
        {
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

    public FakeHttpHandler OnStatus(string url, HttpStatusCode status) =>
        On(url, _ => new HttpResponseMessage(status) { Content = new StringContent("<html>error</html>", Encoding.UTF8, "text/html") });

    public FakeHttpHandler OnRedirect(string url, string location) =>
        On(url, _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
            return response;
        });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Uri uri = request.RequestUri!;
        Requests.Add(uri);
        HttpResponseMessage response;
        if (_routes.TryGetValue(uri.AbsoluteUri, out var queue) && queue.Count > 0)
        {
            int served = Requests.Count(r => r.AbsoluteUri == uri.AbsoluteUri) - 1;
            response = queue[Math.Min(served, queue.Count - 1)](request);
        }
        else
        {
            response = new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("<html>404</html>", Encoding.UTF8, "text/html") };
        }
        response.RequestMessage = request;
        return Task.FromResult(response);
    }
}

/// <summary>An RSA OpenPGP key generated for the tests, able to sign like qBittorrent's release key.</summary>
internal sealed class TestKey
{
    private readonly PgpPrivateKey _privateKey;

    private TestKey(PgpPublicKey publicKey, PgpPrivateKey privateKey)
    {
        PublicKey = publicKey;
        _privateKey = privateKey;
    }

    public PgpPublicKey PublicKey { get; }

    public string Fingerprint => Convert.ToHexString(PublicKey.GetFingerprint());

    public static TestKey Create(DateTime? created = null, long? expiresAfterSeconds = null, bool revoked = false)
    {
        var generator = new RsaKeyPairGenerator();
        generator.Init(new RsaKeyGenerationParameters(BigInteger.ValueOf(0x10001), new SecureRandom(), 2048, 25));
        AsymmetricCipherKeyPair keys = generator.GenerateKeyPair();
        var pair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, keys, created ?? DateTime.UtcNow.AddDays(-30));

        var subpackets = new PgpSignatureSubpacketGenerator();
        subpackets.SetKeyFlags(false, PgpKeyFlags.CanSign | PgpKeyFlags.CanCertify);
        if (expiresAfterSeconds is { } seconds)
            subpackets.SetKeyExpirationTime(false, seconds);

        const string UserId = "QbtAutoUpdate test key <test@example.invalid>";
        var certifier = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
        certifier.InitSign(PgpSignature.PositiveCertification, pair.PrivateKey);
        certifier.SetHashedSubpackets(subpackets.Generate());
        PgpPublicKey publicKey = PgpPublicKey.AddCertification(pair.PublicKey, UserId, certifier.GenerateCertification(UserId, pair.PublicKey));

        if (revoked)
        {
            var revoker = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
            revoker.InitSign(PgpSignature.KeyRevocation, pair.PrivateKey);
            publicKey = PgpPublicKey.AddCertification(publicKey, revoker.GenerateCertification(publicKey));
        }

        return new TestKey(publicKey, pair.PrivateKey);
    }

    public byte[] ArmoredPublicKey()
    {
        using var buffer = new MemoryStream();
        using (var armor = new ArmoredOutputStream(buffer))
            PublicKey.Encode(armor);
        return buffer.ToArray();
    }

    public PgpVerifier Verifier() => new(new MemoryStream(ArmoredPublicKey()), Fingerprint);

    public PgpSignature SignPacket(byte[] data, HashAlgorithmTag hash = HashAlgorithmTag.Sha256, int signatureType = PgpSignature.BinaryDocument)
    {
        var generator = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, hash);
        generator.InitSign(signatureType, _privateKey);
        generator.Update(data, 0, data.Length);
        return generator.Generate();
    }

    /// <summary>A detached, armored signature like the .asc files on SourceForge.</summary>
    public byte[] Sign(byte[] data, HashAlgorithmTag hash = HashAlgorithmTag.Sha256, int signatureType = PgpSignature.BinaryDocument) =>
        Armor(SignPacket(data, hash, signatureType));

    public static byte[] Armor(params PgpSignature[] signatures)
    {
        using var buffer = new MemoryStream();
        using (var armor = new ArmoredOutputStream(buffer))
        {
            foreach (PgpSignature signature in signatures)
                signature.Encode(armor);
        }
        return buffer.ToArray();
    }
}
