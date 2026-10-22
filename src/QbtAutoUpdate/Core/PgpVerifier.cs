using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace QbtAutoUpdate.Core;

public enum PgpVerdict
{
    Valid,
    /// <summary>The signature does not match the file: altered or corrupted download.</summary>
    BadSignature,
    /// <summary>Signed by another key, e.g. qBittorrent rotated its key: QbtAutoUpdate itself must be updated.</summary>
    UnknownKey,
    KeyRevoked,
    KeyExpired,
    /// <summary>Hash algorithm weaker than SHA-256.</summary>
    WeakAlgorithm,
    /// <summary>Not a signature over a binary document.</summary>
    UnexpectedSignatureType,
    /// <summary>Not a single, parseable OpenPGP signature.</summary>
    Malformed,
}

public sealed record PgpResult(PgpVerdict Verdict, string Detail, DateTimeOffset? SignedAt = null)
{
    public bool IsValid => Verdict == PgpVerdict.Valid;
}

/// <summary>
/// Verifies detached signatures against one pinned key. Only signatures made by the pinned primary
/// key are accepted: the official key signs with its primary key and its only subkey is
/// encryption-only (docs/FINDINGS.md §4). Every unexpected case fails closed.
/// </summary>
public sealed class PgpVerifier
{
    /// <summary>qBittorrent release signing key 4096R/5B7CC9A2 (sledgehammer999).</summary>
    public const string OfficialFingerprint = "D8F3DA77AAC6741053599C136E4A2D025B7CC9A2";

    private const string OfficialKeyResource = "QbtAutoUpdate.qbittorrent-5B7CC9A2.asc";

    private readonly PgpPublicKey _key;

    public PgpVerifier(Stream armoredPublicKey, string pinnedFingerprint)
    {
        using Stream decoder = PgpUtilities.GetDecoderStream(armoredPublicKey);
        var bundle = new PgpPublicKeyRingBundle(decoder);

        PgpPublicKey? match = null;
        foreach (PgpPublicKeyRing ring in bundle.GetKeyRings())
        {
            PgpPublicKey primary = ring.GetPublicKey();
            if (string.Equals(Convert.ToHexString(primary.GetFingerprint()), pinnedFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                match = primary;
                break;
            }
        }

        _key = match ?? throw new InvalidDataException($"The key file does not contain the pinned key {pinnedFingerprint}.");
    }

    public string Fingerprint => Convert.ToHexString(_key.GetFingerprint());

    /// <summary>The verifier for official qBittorrent installers, using the key embedded in this exe.</summary>
    public static PgpVerifier CreateOfficial()
    {
        using Stream stream = typeof(PgpVerifier).Assembly.GetManifestResourceStream(OfficialKeyResource)
            ?? throw new InvalidOperationException($"Embedded resource {OfficialKeyResource} is missing.");
        return new PgpVerifier(stream, OfficialFingerprint);
    }

    public PgpResult VerifyFile(string dataPath, string signaturePath, DateTimeOffset now)
    {
        using var data = new FileStream(dataPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
        using var signature = new FileStream(signaturePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Verify(data, signature, now);
    }

    public PgpResult Verify(Stream data, Stream armoredSignature, DateTimeOffset now)
    {
        if (_key.HasRevocation())
            return new PgpResult(PgpVerdict.KeyRevoked, "the pinned key carries a revocation signature");

        DateTimeOffset keyCreated = new(DateTime.SpecifyKind(_key.CreationTime, DateTimeKind.Utc));
        long validSeconds = _key.GetValidSeconds();
        DateTimeOffset? keyExpires = validSeconds > 0 ? keyCreated.AddSeconds(validSeconds) : null;
        if (now > keyExpires)
            return new PgpResult(PgpVerdict.KeyExpired, $"the pinned key expired on {keyExpires:yyyy-MM-dd}");

        PgpSignature signature;
        try
        {
            PgpSignatureList? signatures = ReadSignatures(armoredSignature);
            if (signatures is null || signatures.Count != 1)
                return new PgpResult(PgpVerdict.Malformed, "expected exactly one OpenPGP signature");
            signature = signatures[0];
        }
        catch (Exception ex) when (ex is IOException or PgpException or InvalidCastException)
        {
            return new PgpResult(PgpVerdict.Malformed, "unreadable signature: " + ex.Message);
        }

        if (signature.KeyId != _key.KeyId)
            return new PgpResult(PgpVerdict.UnknownKey, $"signed by key {signature.KeyId:X16}, expected {_key.KeyId:X16}");

        if (signature.SignatureType != PgpSignature.BinaryDocument)
            return new PgpResult(PgpVerdict.UnexpectedSignatureType, $"signature type 0x{signature.SignatureType:X2} is not a binary document signature");

        if (signature.HashAlgorithm is not (HashAlgorithmTag.Sha256 or HashAlgorithmTag.Sha384 or HashAlgorithmTag.Sha512))
            return new PgpResult(PgpVerdict.WeakAlgorithm, $"hash algorithm {signature.HashAlgorithm} is not accepted");

        bool matches;
        try
        {
            signature.InitVerify(_key);
            byte[] buffer = new byte[81920];
            int read;
            while ((read = data.Read(buffer, 0, buffer.Length)) > 0)
                signature.Update(buffer, 0, read);
            matches = signature.Verify();
        }
        catch (PgpException ex)
        {
            return new PgpResult(PgpVerdict.Malformed, "signature could not be checked: " + ex.Message);
        }

        DateTimeOffset signedAt = new(DateTime.SpecifyKind(signature.CreationTime, DateTimeKind.Utc));
        if (!matches)
            return new PgpResult(PgpVerdict.BadSignature, "the file does not match its signature", signedAt);
        if (signedAt < keyCreated)
            return new PgpResult(PgpVerdict.BadSignature, "the signature predates the key", signedAt);
        if (signedAt > keyExpires)
            return new PgpResult(PgpVerdict.KeyExpired, "the signature was made after the key expired", signedAt);

        return new PgpResult(PgpVerdict.Valid, $"good signature from {Fingerprint}", signedAt);
    }

    private static PgpSignatureList? ReadSignatures(Stream armoredSignature)
    {
        using Stream decoder = PgpUtilities.GetDecoderStream(armoredSignature);
        var factory = new PgpObjectFactory(decoder);
        PgpObject? item = factory.NextPgpObject();
        if (item is PgpCompressedData compressed)
        {
            factory = new PgpObjectFactory(compressed.GetDataStream());
            item = factory.NextPgpObject();
        }
        return item as PgpSignatureList;
    }
}
