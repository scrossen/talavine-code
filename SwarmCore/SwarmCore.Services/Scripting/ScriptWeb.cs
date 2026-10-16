using SwarmCore.Services.Abstractions;

namespace SwarmCore.Services.Scripting;

/// <summary>
/// One HTTP response surfaced to an agent-authored script: the status code, the
/// response body, and whether the status was a success (2xx). Returned by
/// <see cref="ScriptWeb.Send"/> when a script needs more than just the body.
/// </summary>
public sealed record ScriptWebResponse(int Status, string Body, bool Ok);

/// <summary>
/// The single controlled web channel exposed to agent-authored C# tools/scripts
/// (across Vibes, the service catalog, and — over time — Talavine). Authored
/// code can't use raw <c>HttpClient</c>/sockets (the safety policy blocks them);
/// every outbound call goes through here, so the host keeps one place to add
/// logging, auth injection, and egress limits.
///
/// Backed by the host-supplied <see cref="IServiceHttpClient"/>, so the scripting
/// engine stays free of any concrete HTTP package. The convenience verbs return
/// the response body as a string (the common case); <see cref="Send"/> returns the
/// full <see cref="ScriptWebResponse"/> for status/error-aware code.
/// </summary>
public sealed class ScriptWeb
{
    private readonly IServiceHttpClient _http;
    private readonly ICredentialStore? _credentials;
    private readonly string? _defaultCredentialRef;
    private readonly CancellationToken _ct;
    private readonly IReadOnlyCollection<string>? _allowedHosts;
    private readonly Action<string>? _activity;

    public ScriptWeb(
        IServiceHttpClient http,
        CancellationToken ct,
        ICredentialStore? credentials = null,
        string? defaultCredentialRef = null,
        IReadOnlyCollection<string>? allowedHosts = null,
        Action<string>? activity = null)
    {
        _http = http;
        _credentials = credentials;
        _defaultCredentialRef = defaultCredentialRef;
        _ct = ct;
        _activity = activity;
        // Egress allowlist: when the host supplies one (e.g. a vibe imported with "only these
        // sites"), EVERY request through this channel — including credentialed sends — must
        // target an allowed host or its subdomain. Null = unrestricted (locally-authored code).
        _allowedHosts = allowedHosts is { Count: > 0 }
            ? allowedHosts
                .Select(NormalizeHost)
                .Where(h => h.Length > 0)
                .ToArray()
            : null;
    }

    /// <summary>HTTP GET a URL and return the response body (JSON/CSV/text).</summary>
    public Task<string> Get(string url) => Body("GET", url, null, null);

    /// <summary>HTTP GET with custom request headers (e.g. an API key), returning the body.</summary>
    public Task<string> Get(string url, IDictionary<string, string> headers) => Body("GET", url, null, headers);

    /// <summary>HTTP POST a body (usually JSON) and return the response body.</summary>
    public Task<string> Post(string url, string body) => Body("POST", url, body, null);

    /// <summary>HTTP POST a body with custom headers and return the response body.</summary>
    public Task<string> Post(string url, string body, IDictionary<string, string> headers) => Body("POST", url, body, headers);

    /// <summary>HTTP PUT a body and return the response body.</summary>
    public Task<string> Put(string url, string body) => Body("PUT", url, body, null);

    /// <summary>HTTP PATCH a body and return the response body.</summary>
    public Task<string> Patch(string url, string body) => Body("PATCH", url, body, null);

    /// <summary>HTTP DELETE a URL and return the response body.</summary>
    public Task<string> Delete(string url) => Body("DELETE", url, null, null);

    /// <summary>
    /// Make a fully controlled request — any method, optional body, optional headers —
    /// and get the status code and body back so the script can branch on failures.
    /// </summary>
    public async Task<ScriptWebResponse> Send(string method, string url, string? body = null, IDictionary<string, string>? headers = null)
    {
        if (!IsHostAllowed(url, out var blockedReason))
        {
            return new ScriptWebResponse(0, blockedReason, false);
        }

        // With an allowlist active, redirects must come back to the script rather than being
        // auto-followed — an allowed host's redirect could otherwise carry the request (and a
        // query-string credential) to a host the gate never checked. Following manually
        // re-enters this gate with the new URL.
        _activity?.Invoke($"HTTP {method} {SafeHost(url)} started");
        try
        {
            var resp = await _http.SendAsync(
                new ServiceHttpRequest(method, url, AsReadOnly(headers), body, AllowRedirects: _allowedHosts is null),
                _ct).ConfigureAwait(false);
            _activity?.Invoke($"HTTP {method} {SafeHost(url)} returned {resp.Status}");
            return new ScriptWebResponse(resp.Status, resp.Body, resp.Ok);
        }
        catch
        {
            _activity?.Invoke($"HTTP {method} {SafeHost(url)} failed");
            throw;
        }
    }

    /// <summary>
    /// Make a controlled request with an opaque binary body. Bytes move from
    /// the objective-aware Files surface to the host HTTP transport without
    /// being converted to model-visible text.
    /// </summary>
    public async Task<ScriptWebResponse> SendBytes(
        string method,
        string url,
        byte[] body,
        string contentType = "application/octet-stream",
        IDictionary<string, string>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!IsHostAllowed(url, out var blockedReason))
        {
            return new ScriptWebResponse(0, blockedReason, false);
        }

        _activity?.Invoke($"HTTP {method} {SafeHost(url)} started ({body.Length} binary bytes)");
        try
        {
            var resp = await _http.SendAsync(
                new ServiceHttpRequest(
                    method,
                    url,
                    AsReadOnly(headers),
                    Body: null,
                    AllowRedirects: _allowedHosts is null,
                    BodyBytes: body,
                    ContentType: string.IsNullOrWhiteSpace(contentType)
                        ? "application/octet-stream"
                        : contentType),
                _ct).ConfigureAwait(false);
            _activity?.Invoke($"HTTP {method} {SafeHost(url)} returned {resp.Status}");
            return new ScriptWebResponse(resp.Status, resp.Body, resp.Ok);
        }
        catch
        {
            _activity?.Invoke($"HTTP {method} {SafeHost(url)} failed");
            throw;
        }
    }

    /// <summary>True when no allowlist is active, or the URL's host is an allowed host or a
    /// subdomain of one. An unparseable URL is blocked while an allowlist is active — the gate
    /// can't judge what it can't parse.</summary>
    private bool IsHostAllowed(string url, out string blockedReason)
    {
        blockedReason = string.Empty;
        if (_allowedHosts is null)
        {
            return true;
        }

        if (_allowedHosts.Count == 0)
        {
            // An allowlist was supplied but no entry normalized to a usable host. Fail closed
            // with a message that explains itself instead of rendering an empty site list.
            blockedReason = "Blocked: this vibe's allowed-site list has no usable entries, so all web access is off until it's fixed on the app's page.";
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            blockedReason = "Blocked: this vibe is limited to specific sites, and that request's address couldn't be checked.";
            return false;
        }

        var host = uri.Host;
        foreach (var allowed in _allowedHosts)
        {
            if (host.Equals(allowed, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        blockedReason =
            $"Blocked: this vibe's web access is limited to {string.Join(", ", _allowedHosts)}, and '{host}' isn't on that list. " +
            "The vibe's owner can change the allowed sites on its Agents & tools page.";
        return false;
    }

    /// <summary>Normalize an allowlist entry to a bare host: trim, drop scheme/path/port.</summary>
    private static string NormalizeHost(string entry)
    {
        var value = (entry ?? string.Empty).Trim().TrimEnd('.');
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (Uri.TryCreate(value.Contains("://", StringComparison.Ordinal) ? value : "https://" + value, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        return value.ToLowerInvariant();
    }

    /// <summary>
    /// Make a request using a stored credential handle. The script names a credential
    /// reference, but never receives the secret; the host injects it into the request.
    /// <paramref name="authScheme"/> supports bearer, header, or query. For header/query,
    /// <paramref name="authName"/> is the header or query-string key.
    /// </summary>
    public async Task<ScriptWebResponse> SendWithCredential(
        string credentialRef,
        string authScheme,
        string method,
        string url,
        string? body = null,
        IDictionary<string, string>? headers = null,
        string? authName = null)
    {
        if (_credentials is null)
        {
            return new ScriptWebResponse(0, "No credential store is available in this host.", false);
        }

        if (string.IsNullOrWhiteSpace(credentialRef))
        {
            return new ScriptWebResponse(0, "credentialRef is required.", false);
        }

        var secret = await _credentials.GetAsync(credentialRef.Trim(), _ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(secret))
        {
            return new ScriptWebResponse(0, $"Credential '{credentialRef}' was not found.", false);
        }

        return await SendWithSecretAsync(secret, authScheme, method, url, body, headers, authName).ConfigureAwait(false);
    }

    /// <summary>
    /// Credentialed binary request. This overload intentionally mirrors
    /// <see cref="SendWithCredential(string,string,string,string,string?,IDictionary{string,string}?,string?)"/>
    /// so authored services can pass bytes read from <c>Files</c> directly.
    /// </summary>
    public async Task<ScriptWebResponse> SendWithCredential(
        string credentialRef,
        string authScheme,
        string method,
        string url,
        byte[] body,
        IDictionary<string, string>? headers = null,
        string? authName = null)
    {
        if (_credentials is null)
        {
            return new ScriptWebResponse(0, "No credential store is available in this host.", false);
        }

        if (string.IsNullOrWhiteSpace(credentialRef))
        {
            return new ScriptWebResponse(0, "credentialRef is required.", false);
        }

        var secret = await _credentials.GetAsync(credentialRef.Trim(), _ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(secret))
        {
            return new ScriptWebResponse(0, $"Credential '{credentialRef}' was not found.", false);
        }

        return await SendWithSecretBytesAsync(
            secret, authScheme, method, url, body, headers, authName).ConfigureAwait(false);
    }

    /// <summary>
    /// Same as <see cref="SendWithCredential"/>, but uses the service/app tool's
    /// default credential reference when the host provided one.
    /// </summary>
    public Task<ScriptWebResponse> SendWithDefaultCredential(
        string authScheme,
        string method,
        string url,
        string? body = null,
        IDictionary<string, string>? headers = null,
        string? authName = null)
    {
        if (string.IsNullOrWhiteSpace(_defaultCredentialRef))
        {
            return Task.FromResult(new ScriptWebResponse(0, "No default credentialRef is configured for this tool.", false));
        }

        return SendWithCredential(_defaultCredentialRef!, authScheme, method, url, body, headers, authName);
    }

    public Task<ScriptWebResponse> SendWithDefaultCredential(
        string authScheme,
        string method,
        string url,
        byte[] body,
        IDictionary<string, string>? headers = null,
        string? authName = null)
    {
        if (string.IsNullOrWhiteSpace(_defaultCredentialRef))
        {
            return Task.FromResult(new ScriptWebResponse(0, "No default credentialRef is configured for this tool.", false));
        }

        return SendWithCredential(_defaultCredentialRef!, authScheme, method, url, body, headers, authName);
    }

    private async Task<string> Body(string method, string url, string? body, IDictionary<string, string>? headers)
        => (await Send(method, url, body, headers).ConfigureAwait(false)).Body;

    private async Task<ScriptWebResponse> SendWithSecretAsync(
        string secret,
        string authScheme,
        string method,
        string url,
        string? body,
        IDictionary<string, string>? headers,
        string? authName)
    {
        var token = ExtractBearerToken(secret);
        var outgoingHeaders = headers is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);

        switch ((authScheme ?? "bearer").Trim().ToLowerInvariant())
        {
            case "bearer":
                outgoingHeaders["Authorization"] = "Bearer " + token;
                break;
            case "header":
                outgoingHeaders[string.IsNullOrWhiteSpace(authName) ? "Authorization" : authName!] = token;
                break;
            case "query":
                var key = string.IsNullOrWhiteSpace(authName) ? "access_token" : authName!;
                url += (url.Contains('?') ? "&" : "?")
                    + Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(token);
                break;
            default:
                return new ScriptWebResponse(0, $"Unsupported authScheme '{authScheme}'. Use bearer, header, or query.", false);
        }

        var response = await Send(method, url, body, outgoingHeaders).ConfigureAwait(false);
        return response with { Body = Http.CredentialEcho.Scrub(response.Body, token, secret) };
    }

    private async Task<ScriptWebResponse> SendWithSecretBytesAsync(
        string secret,
        string authScheme,
        string method,
        string url,
        byte[] body,
        IDictionary<string, string>? headers,
        string? authName)
    {
        var token = ExtractBearerToken(secret);
        var outgoingHeaders = headers is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);

        switch ((authScheme ?? "bearer").Trim().ToLowerInvariant())
        {
            case "bearer":
                outgoingHeaders["Authorization"] = "Bearer " + token;
                break;
            case "header":
                outgoingHeaders[string.IsNullOrWhiteSpace(authName) ? "Authorization" : authName!] = token;
                break;
            case "query":
                var key = string.IsNullOrWhiteSpace(authName) ? "access_token" : authName!;
                url += (url.Contains('?') ? "&" : "?")
                    + Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(token);
                break;
            default:
                return new ScriptWebResponse(0, $"Unsupported authScheme '{authScheme}'. Use bearer, header, or query.", false);
        }

        var contentType = outgoingHeaders.TryGetValue("Content-Type", out var declared)
            ? declared
            : "application/octet-stream";
        outgoingHeaders.Remove("Content-Type");
        var response = await SendBytes(method, url, body, contentType, outgoingHeaders).ConfigureAwait(false);
        return response with { Body = Http.CredentialEcho.Scrub(response.Body, token, secret) };
    }

    private static string ExtractBearerToken(string secret)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(secret);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("access_token", out var token)
                && token.ValueKind == System.Text.Json.JsonValueKind.String
                && !string.IsNullOrWhiteSpace(token.GetString()))
            {
                return token.GetString()!;
            }
        }
        catch
        {
            // Plain API key or bearer token.
        }

        return secret;
    }

    private static IReadOnlyDictionary<string, string>? AsReadOnly(IDictionary<string, string>? headers)
        => headers is null ? null : new Dictionary<string, string>(headers);

    private static string SafeHost(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host
            : "unparseable-host";
}
