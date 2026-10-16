using System.Security.Cryptography;
using System.Text;

namespace SwarmCore.Abstractions.Packaging;

/// <summary>
/// Offline publisher signing: a publisher (e.g. swarmmarshal.com) signs the exact bytes it serves
/// with an ECDSA P-256 key, and apps verify with the publisher's public key — embedded in the app,
/// never fetched — before trusting the bytes. The private half lives only with release tooling, so
/// a compromised web host or a MITM cannot mint signed content. Verification fails closed:
/// <see cref="Verify"/> returns false for anything it cannot positively check.
///
/// This is the shared primitive behind vibe-package signing (<c>VibePackageSigning</c>) and app
/// update-manifest signing (<c>AppReleaseSigning</c>). Callers that share a key across purposes
/// MUST domain-separate their payloads (prefix the signed bytes with a purpose tag) so a signature
/// minted for one document type can never be replayed as another.
/// </summary>
public static class PublisherSigning
{
    /// <summary>Generate a new P-256 key pair as (privateKeyPem, publicKeyPem). The private key is
    /// PKCS#8; keep it out of source control. The public key is SPKI, safe to embed in apps.</summary>
    public static (string PrivateKeyPem, string PublicKeyPem) GenerateKeyPair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), key.ExportSubjectPublicKeyInfoPem());
    }

    /// <summary>Sign the exact UTF-8 bytes of a document. Returns a base64 signature.</summary>
    public static string Sign(string document, string privateKeyPem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPem);

        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        var signature = key.SignData(Encoding.UTF8.GetBytes(document), HashAlgorithmName.SHA256);
        return Convert.ToBase64String(signature);
    }

    /// <summary>Verify a base64 signature over the exact UTF-8 bytes of a document.
    /// Returns false (never throws) for malformed signatures/keys so callers can fail closed simply.</summary>
    public static bool Verify(string document, string signatureBase64, string publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(document) ||
            string.IsNullOrWhiteSpace(signatureBase64) ||
            string.IsNullOrWhiteSpace(publicKeyPem))
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            var signature = Convert.FromBase64String(signatureBase64);
            return key.VerifyData(Encoding.UTF8.GetBytes(document), signature, HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Hex-encoded SHA-256 of the exact UTF-8 bytes of a document (integrity check and
    /// stable content id).</summary>
    public static string Sha256Hex(string document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document))).ToLowerInvariant();
    }

    /// <summary>Hex-encoded SHA-256 of raw bytes (downloaded payloads, not just text documents).</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Constant-time-ish comparison of two hex digests, tolerant of case and surrounding
    /// whitespace. Returns false when either side is missing, so an absent expected hash can never
    /// read as "matched".</summary>
    public static bool HashesMatch(string? expectedHex, string? actualHex)
    {
        if (string.IsNullOrWhiteSpace(expectedHex) || string.IsNullOrWhiteSpace(actualHex))
        {
            return false;
        }

        var expected = expectedHex.Trim();
        var actual = actualHex.Trim();
        if (expected.Length != actual.Length)
        {
            return false;
        }

        var difference = 0;
        for (var i = 0; i < expected.Length; i++)
        {
            difference |= char.ToLowerInvariant(expected[i]) ^ char.ToLowerInvariant(actual[i]);
        }

        return difference == 0;
    }
}
