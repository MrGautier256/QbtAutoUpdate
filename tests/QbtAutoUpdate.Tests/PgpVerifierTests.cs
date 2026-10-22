using System.Text;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using QbtAutoUpdate.Core;

namespace QbtAutoUpdate.Tests;

/// <summary>Keys are generated once per test class: RSA generation is slow.</summary>
public sealed class PgpKeys
{
    internal TestKey Signer { get; } = TestKey.Create();
    internal TestKey Other { get; } = TestKey.Create();
    internal TestKey Expired { get; } = TestKey.Create(DateTime.UtcNow.AddDays(-10), expiresAfterSeconds: 86400);
    internal TestKey Revoked { get; } = TestKey.Create(revoked: true);
}

public class PgpVerifierTests(PgpKeys keys) : IClassFixture<PgpKeys>
{
    private static readonly byte[] Data = Encoding.ASCII.GetBytes("MZ pretend this is qbittorrent_5.3.0_x64_setup.exe");
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static PgpResult Verify(PgpVerifier verifier, byte[] data, byte[] signature) =>
        verifier.Verify(new MemoryStream(data), new MemoryStream(signature), Now);

    [Fact]
    public void Accepts_a_good_signature()
    {
        PgpResult result = Verify(keys.Signer.Verifier(), Data, keys.Signer.Sign(Data));

        Assert.Equal(PgpVerdict.Valid, result.Verdict);
        Assert.NotNull(result.SignedAt);
    }

    [Fact]
    public void Rejects_an_altered_file()
    {
        byte[] altered = (byte[])Data.Clone();
        altered[^1] ^= 1;

        Assert.Equal(PgpVerdict.BadSignature, Verify(keys.Signer.Verifier(), altered, keys.Signer.Sign(Data)).Verdict);
    }

    [Fact]
    public void Rejects_a_signature_from_another_key()
    {
        PgpResult result = Verify(keys.Signer.Verifier(), Data, keys.Other.Sign(Data));

        Assert.Equal(PgpVerdict.UnknownKey, result.Verdict);
    }

    [Fact]
    public void Rejects_sha1()
    {
        Assert.Equal(PgpVerdict.WeakAlgorithm, Verify(keys.Signer.Verifier(), Data, keys.Signer.Sign(Data, HashAlgorithmTag.Sha1)).Verdict);
    }

    [Fact]
    public void Rejects_a_text_signature()
    {
        byte[] signature = keys.Signer.Sign(Data, signatureType: PgpSignature.CanonicalTextDocument);

        Assert.Equal(PgpVerdict.UnexpectedSignatureType, Verify(keys.Signer.Verifier(), Data, signature).Verdict);
    }

    [Fact]
    public void Rejects_an_expired_key()
    {
        Assert.Equal(PgpVerdict.KeyExpired, Verify(keys.Expired.Verifier(), Data, keys.Expired.Sign(Data)).Verdict);
    }

    [Fact]
    public void Rejects_a_revoked_key()
    {
        Assert.Equal(PgpVerdict.KeyRevoked, Verify(keys.Revoked.Verifier(), Data, keys.Revoked.Sign(Data)).Verdict);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>Mirror error</html>")]
    [InlineData("-----BEGIN PGP SIGNATURE-----\n\nAAAA\n-----END PGP SIGNATURE-----\n")]
    public void Rejects_garbage(string signature)
    {
        Assert.Equal(PgpVerdict.Malformed, Verify(keys.Signer.Verifier(), Data, Encoding.ASCII.GetBytes(signature)).Verdict);
    }

    [Fact]
    public void Rejects_several_signatures_in_one_block()
    {
        // A valid signature next to a foreign one: exactly one signature is expected, nothing is guessed.
        byte[] both = TestKey.Armor(keys.Signer.SignPacket(Data), keys.Other.SignPacket(Data));

        Assert.Equal(PgpVerdict.Malformed, Verify(keys.Signer.Verifier(), Data, both).Verdict);
    }

    [Fact]
    public void Refuses_a_key_file_without_the_pinned_key()
    {
        Assert.Throws<InvalidDataException>(() => new PgpVerifier(new MemoryStream(keys.Signer.ArmoredPublicKey()), keys.Other.Fingerprint));
    }

    [Fact]
    public void The_embedded_key_is_the_official_one()
    {
        PgpVerifier verifier = PgpVerifier.CreateOfficial();

        Assert.Equal("D8F3DA77AAC6741053599C136E4A2D025B7CC9A2", verifier.Fingerprint);
    }

    /// <summary>
    /// The real 5.3.0 signatures (with their notation subpacket) parse and point at the pinned key;
    /// against other data they must fail as BadSignature, not as UnknownKey or Malformed.
    /// </summary>
    [Theory]
    [InlineData("qbittorrent_5.3.0_x64_setup.exe.asc")]
    [InlineData("qbittorrent_5.3.0_lt12_x64_setup.exe.asc")]
    public void Real_signatures_are_read_and_bound_to_the_official_key(string file)
    {
        PgpVerifier verifier = PgpVerifier.CreateOfficial();
        using FileStream signature = File.OpenRead(Fixtures.PathOf("Pgp", file));

        PgpResult result = verifier.Verify(new MemoryStream(Data), signature, Now);

        Assert.Equal(PgpVerdict.BadSignature, result.Verdict);
        Assert.Equal(2026, result.SignedAt!.Value.Year);
    }
}
