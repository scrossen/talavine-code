using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SwarmCore.Agent.Business;

/// <summary>
/// Host-supplied HMAC keys for public business links. The host owns storage, rotation and tenancy;
/// SwarmCore never reads configuration. Returned key arrays are zeroed after each use, so return a copy.
/// </summary>
public interface IBusinessPublicLinkKeyProvider
{
    /// <summary>Current signing key for a tenant; false when the tenant has no key. Keys must be at least 32 bytes.</summary>
    bool TryGetSigningKey(string tenantId, out string keyId, out byte[] key);

    /// <summary>Verification key by id (current or retired); null when unknown or revoked.</summary>
    byte[]? GetVerificationKey(string keyId);
}

/// <summary>Fields bound into a minted token.</summary>
public sealed record BusinessPublicLinkRequest(
    string TenantId,
    string Purpose,
    string SubjectId,
    string PayloadHash,
    string Recipient,
    DateTimeOffset ExpiresUtc);

/// <summary>
/// Fields recovered from a verified token. The recipient is carried as a keyed digest so the
/// link never exposes an address; compare it with <see cref="BusinessPublicLinkToken.IsRecipient"/>.
/// </summary>
public sealed record BusinessPublicLinkClaims(
    string KeyId,
    string TenantId,
    string Purpose,
    string SubjectId,
    string PayloadHash,
    string RecipientDigest,
    DateTimeOffset IssuedUtc,
    DateTimeOffset ExpiresUtc);

/// <summary>Why a token did not verify. Hosts show one neutral message for every failure.</summary>
public enum BusinessPublicLinkFailure { None, Malformed, UnknownKey, BadSignature, Expired }

/// <summary>Verification outcome; <see cref="Claims"/> is set only when <see cref="Ok"/>.</summary>
public sealed record BusinessPublicLinkVerification(bool Ok, BusinessPublicLinkFailure Failure, BusinessPublicLinkClaims? Claims);

/// <summary>
/// Mints and verifies opaque, URL-safe, purpose-bound tokens for public business links such as
/// proposal acceptance or client intake. A token proves only that this host issued it for the bound
/// tenant, purpose, subject, payload hash, recipient and expiry; the host still checks current subject
/// state (newer version, already used, cancelled) and records one-use consumption itself.
/// </summary>
public sealed partial class BusinessPublicLinkToken(IBusinessPublicLinkKeyProvider keys)
{
    /// <summary>Token format version prefix.</summary>
    public const string Version = "bpl1";

    /// <summary>Longest lifetime a token may carry.</summary>
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromDays(90);

    /// <summary>Longest token accepted for verification.</summary>
    public const int MaximumTokenLength = 2048;

    private const string Domain = "business-public-link-v1:";
    private const string RecipientDomain = "business-public-link-recipient-v1:";
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = null };
    private readonly IBusinessPublicLinkKeyProvider _keys = keys ?? throw new ArgumentNullException(nameof(keys));

    /// <summary>Mints a token. Throws when an input is malformed, the expiry is out of range, or the tenant has no usable key.</summary>
    public string Mint(BusinessPublicLinkRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenant = Bounded(request.TenantId, nameof(request.TenantId));
        var subject = Bounded(request.SubjectId, nameof(request.SubjectId));
        if (!IsPurpose(request.Purpose)) throw new ArgumentException("Purpose must be 1-64 lowercase letters, digits or hyphens.", nameof(request));
        if (request.PayloadHash is null || !PayloadHashPattern().IsMatch(request.PayloadHash))
            throw new ArgumentException("Payload hash must be a hex SHA-256 digest.", nameof(request));
        if (!IsNormalizedRecipient(request.Recipient))
            throw new ArgumentException("Recipient must be an already normalized email or E.164 phone.", nameof(request));
        var issued = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds());
        var expires = DateTimeOffset.FromUnixTimeSeconds(request.ExpiresUtc.ToUnixTimeSeconds());
        if (expires <= issued || expires - issued > MaximumLifetime)
            throw new ArgumentException($"Expiry must be in the future and within {MaximumLifetime.TotalDays:0} days.", nameof(request));
        if (!_keys.TryGetSigningKey(tenant, out var keyId, out var key) || key is null)
            throw new InvalidOperationException("No public-link signing key is configured for this tenant.");
        try
        {
            if (key.Length < 32) throw new InvalidOperationException("Public-link signing keys must be at least 32 bytes.");
            if (!KeyIdPattern().IsMatch(keyId ?? "")) throw new InvalidOperationException("Public-link key ids must be 1-32 letters, digits, '_' or '-'.");
            var body = new Body(tenant, request.Purpose, subject, request.PayloadHash.ToLowerInvariant(),
                RecipientDigest(key, tenant, request.Recipient), issued.ToUnixTimeSeconds(), expires.ToUnixTimeSeconds());
            var payload = JsonSerializer.SerializeToUtf8Bytes(body, Json);
            var mac = Mac(key, keyId!, request.Purpose, payload);
            return Version + "." + keyId + "." + Base64Url.EncodeToString(payload) + "." + Base64Url.EncodeToString(mac);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>
    /// Verifies a token for exactly <paramref name="expectedPurpose"/>. The signature check is
    /// constant time and precedes any parsing of the payload; expiry is checked against <paramref name="now"/>.
    /// </summary>
    public BusinessPublicLinkVerification Verify(string? token, string expectedPurpose, DateTimeOffset now)
    {
        if (!IsPurpose(expectedPurpose)) throw new ArgumentException("Purpose must be 1-64 lowercase letters, digits or hyphens.", nameof(expectedPurpose));
        if (string.IsNullOrEmpty(token) || token.Length > MaximumTokenLength) return Fail(BusinessPublicLinkFailure.Malformed);
        var parts = token.Split('.');
        if (parts.Length != 4 || parts[0] != Version || !KeyIdPattern().IsMatch(parts[1])) return Fail(BusinessPublicLinkFailure.Malformed);
        byte[] payload, claimed;
        try { payload = Base64Url.DecodeFromChars(parts[2]); claimed = Base64Url.DecodeFromChars(parts[3]); }
        catch (FormatException) { return Fail(BusinessPublicLinkFailure.Malformed); }
        if (payload.Length == 0 || claimed.Length != 32) return Fail(BusinessPublicLinkFailure.Malformed);
        var key = _keys.GetVerificationKey(parts[1]);
        if (key is null) return Fail(BusinessPublicLinkFailure.UnknownKey);
        try
        {
            if (key.Length < 32) return Fail(BusinessPublicLinkFailure.UnknownKey);
            if (!CryptographicOperations.FixedTimeEquals(Mac(key, parts[1], expectedPurpose, payload), claimed))
                return Fail(BusinessPublicLinkFailure.BadSignature);
        }
        finally { CryptographicOperations.ZeroMemory(key); }

        Body? body;
        try { body = JsonSerializer.Deserialize<Body>(payload, Json); }
        catch (JsonException) { return Fail(BusinessPublicLinkFailure.Malformed); }
        if (body is null || body.P != expectedPurpose || string.IsNullOrEmpty(body.T) || string.IsNullOrEmpty(body.S)
            || string.IsNullOrEmpty(body.H) || string.IsNullOrEmpty(body.R)) return Fail(BusinessPublicLinkFailure.Malformed);
        DateTimeOffset issued, expires;
        try { issued = DateTimeOffset.FromUnixTimeSeconds(body.I); expires = DateTimeOffset.FromUnixTimeSeconds(body.E); }
        catch (ArgumentOutOfRangeException) { return Fail(BusinessPublicLinkFailure.Malformed); }
        if (expires <= issued || expires - issued > MaximumLifetime) return Fail(BusinessPublicLinkFailure.Malformed);
        if (now >= expires) return Fail(BusinessPublicLinkFailure.Expired);
        return new(true, BusinessPublicLinkFailure.None,
            new BusinessPublicLinkClaims(parts[1], body.T, body.P, body.S, body.H, body.R, issued, expires));
    }

    /// <summary>
    /// Constant-time check that verified claims were bound to <paramref name="recipient"/>
    /// (normalized with <see cref="ClientIdentity"/> first). False when the key is no longer available.
    /// </summary>
    public bool IsRecipient(BusinessPublicLinkClaims claims, string? recipient)
    {
        ArgumentNullException.ThrowIfNull(claims);
        var normalized = recipient is null ? null
            : recipient.TrimStart().StartsWith('+') ? ClientIdentity.NormalizePhone(recipient) : ClientIdentity.NormalizeEmail(recipient);
        if (normalized is null) return false;
        var key = _keys.GetVerificationKey(claims.KeyId);
        if (key is null) return false;
        try
        {
            if (key.Length < 32) return false;
            var expected = Encoding.ASCII.GetBytes(RecipientDigest(key, claims.TenantId, normalized));
            var actual = Encoding.ASCII.GetBytes(claims.RecipientDigest);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static BusinessPublicLinkVerification Fail(BusinessPublicLinkFailure failure) => new(false, failure, null);

    private static byte[] Mac(byte[] key, string keyId, string purpose, byte[] payload)
    {
        var prefix = Encoding.UTF8.GetBytes(Domain + keyId + ":" + purpose + ".");
        var signed = new byte[prefix.Length + payload.Length];
        prefix.CopyTo(signed, 0); payload.CopyTo(signed.AsSpan(prefix.Length));
        return HMACSHA256.HashData(key, signed);
    }

    private static string RecipientDigest(byte[] key, string tenant, string recipient)
        => Base64Url.EncodeToString(HMACSHA256.HashData(key,
            Encoding.UTF8.GetBytes(RecipientDomain + tenant.Length + ":" + tenant + ":" + recipient)));

    private static bool IsPurpose(string? purpose) => purpose is not null && PurposePattern().IsMatch(purpose);

    private static bool IsNormalizedRecipient(string? recipient)
        => !string.IsNullOrEmpty(recipient)
           && (recipient.StartsWith('+') ? ClientIdentity.NormalizePhone(recipient) == recipient
               : recipient.Contains('@') && ClientIdentity.NormalizeEmail(recipient) == recipient);

    private static string Bounded(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > 128)
            throw new ArgumentException($"{name} must be 1-128 characters with no surrounding whitespace.", name);
        return value;
    }

    private sealed record Body(
        [property: JsonPropertyName("t")] string T,
        [property: JsonPropertyName("p")] string P,
        [property: JsonPropertyName("s")] string S,
        [property: JsonPropertyName("h")] string H,
        [property: JsonPropertyName("r")] string R,
        [property: JsonPropertyName("i")] long I,
        [property: JsonPropertyName("e")] long E);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex PurposePattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyIdPattern();

    [GeneratedRegex("^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex PayloadHashPattern();
}
