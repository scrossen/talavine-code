using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwarmCore.Abstractions.OAuth;

/// <summary>
/// PKCE (RFC 7636) with the only method an agent client may use here: S256.
/// </summary>
public static class OAuthPkce
{
    /// <summary>RFC 7636 §4.1: 43-128 characters from the unreserved set.</summary>
    public static bool IsValidVerifier(string? verifier) =>
        verifier is { Length: >= 43 and <= 128 } && verifier.All(IsUnreserved);

    /// <summary>An S256 challenge is the base64url SHA-256 of a verifier: always 43 characters.</summary>
    public static bool IsValidS256Challenge(string? challenge) =>
        challenge is { Length: 43 } && challenge.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static string ComputeS256Challenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static bool VerifyS256(string? verifier, string? challenge)
    {
        if (!IsValidVerifier(verifier) || !IsValidS256Challenge(challenge))
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(ComputeS256Challenge(verifier!));
        var actual = Encoding.ASCII.GetBytes(challenge!);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    internal static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool IsUnreserved(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~';
}

/// <summary>
/// An OAuth Client ID Metadata Document (the MCP authorization spec's preferred client
/// registration): the client's <c>client_id</c> is an HTTPS URL that serves this JSON.
/// </summary>
public sealed record OAuthClientIdDocument
{
    [JsonPropertyName("client_id")] public string? ClientId { get; init; }
    [JsonPropertyName("client_name")] public string? ClientName { get; init; }
    [JsonPropertyName("client_uri")] public string? ClientUri { get; init; }
    [JsonPropertyName("logo_uri")] public string? LogoUri { get; init; }
    [JsonPropertyName("redirect_uris")] public string[]? RedirectUris { get; init; }
    [JsonPropertyName("token_endpoint_auth_method")] public string? TokenEndpointAuthMethod { get; init; }
    [JsonPropertyName("token_endpoint_auth_methods_supported")] public string[]? TokenEndpointAuthMethodsSupported { get; init; }
    [JsonPropertyName("grant_types")] public string[]? GrantTypes { get; init; }
    [JsonPropertyName("response_types")] public string[]? ResponseTypes { get; init; }

    public static OAuthClientIdDocument? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<OAuthClientIdDocument>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>What to show a person on a consent screen: the client's own name plus the host that vouches for it.</summary>
    public string DisplayName(Uri clientIdUrl) =>
        string.IsNullOrWhiteSpace(ClientName) ? clientIdUrl.Host : $"{ClientName.Trim()} ({clientIdUrl.Host})";
}

/// <summary>
/// Validation rules for metadata-document clients. Pure, so every refusal is unit-tested; the host
/// owns fetching (with its own SSRF guard) and caching.
/// </summary>
public static class OAuthClientIdPolicy
{
    /// <summary>Token endpoint auth methods this server supports for metadata-document clients.</summary>
    public const string PublicClientAuthMethod = "none";

    /// <summary>
    /// A client id URL must be HTTPS with a path, and carry no fragment, credentials, or dot segments
    /// (draft-ietf-oauth-client-id-metadata-document §3). Loopback HTTP is accepted only when the
    /// host explicitly allows it for local development.
    /// </summary>
    public static string? ValidateClientIdUrl(string? clientId, bool allowLoopbackHttp, out Uri? url)
    {
        url = null;
        if (string.IsNullOrWhiteSpace(clientId) || !Uri.TryCreate(clientId, UriKind.Absolute, out var parsed))
        {
            return "client_id must be an absolute URL.";
        }

        var loopbackHttp = allowLoopbackHttp && parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback;
        if (parsed.Scheme != Uri.UriSchemeHttps && !loopbackHttp)
        {
            return "client_id must be an https URL.";
        }

        if (!string.IsNullOrEmpty(parsed.Fragment) || !string.IsNullOrEmpty(parsed.UserInfo))
        {
            return "client_id must not contain a fragment or credentials.";
        }

        if (parsed.AbsolutePath is "/" or "" || parsed.OriginalString.Contains("/./", StringComparison.Ordinal)
            || parsed.OriginalString.Contains("/../", StringComparison.Ordinal))
        {
            return "client_id must have a path without dot segments.";
        }

        if (!loopbackHttp && (IPAddress.TryParse(parsed.Host.Trim('[', ']'), out _) || parsed.IsLoopback))
        {
            return "client_id must name a public host.";
        }

        url = parsed;
        return null;
    }

    /// <summary>
    /// RFC 8252 section 7.3: a native client's loopback redirect may use any port, so a registered
    /// <c>http://127.0.0.1/callback</c> (IP literal, no port) matches the same path on the port the client opened.
    /// Only http, only a loopback IP literal, and the path and query must match exactly.
    /// </summary>
    public static bool LoopbackRedirectMatches(string registered, string requested)
    {
        if (!Uri.TryCreate(registered, UriKind.Absolute, out var r) || !Uri.TryCreate(requested, UriKind.Absolute, out var q)) return false;
        if (r.Scheme != Uri.UriSchemeHttp || q.Scheme != Uri.UriSchemeHttp) return false;
        if (!IPAddress.TryParse(r.Host.Trim('[', ']'), out var rIp) || !IPAddress.IsLoopback(rIp)) return false;
        if (!r.IsDefaultPort) return false; // a registration that names a port means that exact port
        return string.Equals(r.Host, q.Host, StringComparison.OrdinalIgnoreCase)
               && string.Equals(r.PathAndQuery, q.PathAndQuery, StringComparison.Ordinal)
               && string.IsNullOrEmpty(q.UserInfo) && string.IsNullOrEmpty(q.Fragment);
    }

    /// <summary>
    /// The fetched document must describe the URL it was fetched from, list the exact redirect URI the
    /// request uses, and be a public (PKCE) client.
    /// </summary>
    public static string? ValidateDocument(Uri clientIdUrl, OAuthClientIdDocument? document, string? redirectUri)
    {
        if (document is null)
        {
            return "The client metadata document could not be read.";
        }

        if (!string.Equals(document.ClientId, clientIdUrl.OriginalString, StringComparison.Ordinal))
        {
            return "The client metadata document's client_id does not match its URL.";
        }

        if (string.IsNullOrWhiteSpace(redirectUri)
            || document.RedirectUris is null
            || !(document.RedirectUris.Contains(redirectUri, StringComparer.Ordinal)
                 || document.RedirectUris.Any(registered => LoopbackRedirectMatches(registered, redirectUri))))
        {
            return "redirect_uri is not registered in the client metadata document.";
        }

        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var redirect)
            || !string.IsNullOrEmpty(redirect.Fragment)
            || (redirect.Scheme != Uri.UriSchemeHttps && !(redirect.Scheme == Uri.UriSchemeHttp && redirect.IsLoopback)))
        {
            return "redirect_uri must be https (or http on loopback) without a fragment.";
        }

        var method = string.IsNullOrWhiteSpace(document.TokenEndpointAuthMethod)
            ? PublicClientAuthMethod
            : document.TokenEndpointAuthMethod;
        if (!(document.TokenEndpointAuthMethodsSupported is not null
                ? document.TokenEndpointAuthMethodsSupported.Contains(PublicClientAuthMethod, StringComparer.Ordinal)
                : string.Equals(method, PublicClientAuthMethod, StringComparison.Ordinal)))
        {
            return $"token_endpoint_auth_method '{method}' is not supported; register a public client ('none') with PKCE.";
        }

        return null;
    }
}

/// <summary>
/// A statement by the signup service (the website) that a new workspace's signup finished in the
/// browser that is continuing an authorization request. It lets the workspace's host finish the
/// agent's sign-in for the admin who just created the workspace, without a password they have not
/// chosen yet. HMAC-signed with the secret the two hosts already share for provisioning; single use
/// and bound to the continuation it was issued for.
/// </summary>
public sealed record SignupContinuationGrant
{
    public const string Prefix = "SCG1";

    [JsonPropertyName("rid")] public string RequestId { get; init; } = string.Empty;
    [JsonPropertyName("slug")] public string TenantSlug { get; init; } = string.Empty;
    [JsonPropertyName("email")] public string AdminEmail { get; init; } = string.Empty;

    /// <summary>SHA-256 (base64url) of the opaque continuation the authorization host handed out.</summary>
    [JsonPropertyName("cont")] public string ContinuationHash { get; init; } = string.Empty;

    [JsonPropertyName("iat")] public DateTime IssuedAtUtc { get; init; }
    [JsonPropertyName("exp")] public DateTime ExpiresAtUtc { get; init; }

    public static string HashContinuation(string continuation) =>
        OAuthPkce.Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(continuation)));

    public static string Issue(SignupContinuationGrant grant, string sharedSecret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedSecret);
        var body = OAuthPkce.Base64Url(JsonSerializer.SerializeToUtf8Bytes(grant));
        return $"{Prefix}.{body}.{Sign(body, sharedSecret)}";
    }

    public static bool TryVerify(
        string? token,
        string sharedSecret,
        string continuation,
        DateTime nowUtc,
        out SignupContinuationGrant? grant,
        out string? error)
    {
        grant = null;
        error = null;
        var parts = (token ?? string.Empty).Split('.');
        if (parts.Length != 3 || parts[0] != Prefix || string.IsNullOrWhiteSpace(sharedSecret))
        {
            error = "Malformed signup grant.";
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Sign(parts[1], sharedSecret)),
                Encoding.ASCII.GetBytes(parts[2])))
        {
            error = "Signup grant signature is invalid.";
            return false;
        }

        try
        {
            grant = JsonSerializer.Deserialize<SignupContinuationGrant>(FromBase64Url(parts[1]));
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            grant = null;
        }

        if (grant is null || string.IsNullOrWhiteSpace(grant.RequestId) || string.IsNullOrWhiteSpace(grant.TenantSlug))
        {
            error = "Signup grant is incomplete.";
            grant = null;
            return false;
        }

        if (grant.ExpiresAtUtc <= nowUtc || grant.IssuedAtUtc > nowUtc.AddMinutes(5))
        {
            error = "Signup grant has expired.";
            grant = null;
            return false;
        }

        if (!string.Equals(grant.ContinuationHash, HashContinuation(continuation), StringComparison.Ordinal))
        {
            error = "Signup grant was issued for a different sign-in.";
            grant = null;
            return false;
        }

        return true;
    }

    private static string Sign(string body, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return OAuthPkce.Base64Url(hmac.ComputeHash(Encoding.ASCII.GetBytes($"{Prefix}.{body}")));
    }

    private static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
