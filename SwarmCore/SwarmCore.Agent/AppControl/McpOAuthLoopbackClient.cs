using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCore.Abstractions.OAuth;

namespace SwarmCore.Agent.AppControl;

/// <summary>Result of signing an agent in to an MCP server.</summary>
public sealed record McpOAuthResult(string AccessToken, string? Scope, Uri Resource);

/// <summary>
/// Signs a local agent in to an MCP server that follows the MCP authorization spec, as a public client:
/// protected-resource metadata (from the 401 <c>WWW-Authenticate</c> or the well-known path), authorization-server
/// metadata, a Client ID Metadata Document, PKCE S256, and an RFC 8252 loopback redirect. The person approves in
/// their browser; nothing here sees a password. When the client has no public https metadata URL, it serves its
/// document from the same loopback listener, which only a server that allows loopback clients (a local/self-hosted
/// deployment) accepts.
/// </summary>
public sealed class McpOAuthLoopbackClient(HttpClient http)
{
    public const string CallbackPath = "/callback";
    public const string ClientDocumentPath = "/oauth-client.json";

    /// <summary>
    /// Runs the whole sign-in. <paramref name="openBrowser"/> receives the authorization URL (the host opens it and
    /// also shows it). Returns once the browser redirects back, or throws on refusal, mismatch or timeout.
    /// </summary>
    public async Task<McpOAuthResult> SignInAsync(Uri mcpEndpoint, string clientName, Uri? publicClientIdUrl, string? scope,
        Func<Uri, Task> openBrowser, TimeSpan timeout, CancellationToken ct)
    {
        var resourceMetadata = await DiscoverResourceMetadataAsync(mcpEndpoint, ct).ConfigureAwait(false);
        var resource = new Uri(resourceMetadata["resource"]?.GetValue<string>() ?? mcpEndpoint.ToString());
        var issuer = resourceMetadata["authorization_servers"] is JsonArray servers && servers.Count > 0
            ? WebEndpoint(servers[0]?.GetValue<string>(), mcpEndpoint, "authorization server")
            : new Uri(mcpEndpoint.GetLeftPart(UriPartial.Authority));
        JsonObject? server = null;
        foreach (var candidate in AuthorizationServerMetadataUrls(issuer).Where(url => SameOriginOrWeb(url, mcpEndpoint)))
            if ((server = await GetJsonAsync(candidate, ct).ConfigureAwait(false)) is not null) break;
        if (server is null) throw new InvalidOperationException("The server did not publish OAuth authorization-server metadata.");
        var authorize = WebEndpoint(server["authorization_endpoint"]?.GetValue<string>(), mcpEndpoint, "authorization_endpoint");
        var token = WebEndpoint(server["token_endpoint"]?.GetValue<string>(), mcpEndpoint, "token_endpoint");
        if (server["code_challenge_methods_supported"] is JsonArray methods && !methods.Any(m => m?.GetValue<string>() == "S256"))
            throw new InvalidOperationException("The server does not support PKCE S256.");

        using var listener = new CallbackListener();
        var redirect = $"http://127.0.0.1:{listener.Port}{CallbackPath}";
        var clientId = publicClientIdUrl?.ToString() ?? $"http://127.0.0.1:{listener.Port}{ClientDocumentPath}";
        listener.ClientDocument = JsonSerializer.Serialize(new
        {
            client_id = clientId,
            client_name = clientName,
            redirect_uris = new[] { redirect, $"http://127.0.0.1{CallbackPath}" },
            grant_types = new[] { "authorization_code" },
            response_types = new[] { "code" },
            token_endpoint_auth_method = "none",
        });

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));
        var query = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirect,
            ["code_challenge"] = OAuthPkce.ComputeS256Challenge(verifier),
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["resource"] = resource.ToString().TrimEnd('/'),
            ["scope"] = string.IsNullOrWhiteSpace(scope) ? null : scope,
        };
        var url = new UriBuilder(authorize)
        {
            Query = string.Join('&', query.Where(kv => kv.Value is not null).Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value!))),
        }.Uri;

        using var waitLimit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        waitLimit.CancelAfter(timeout);
        var callback = listener.WaitForCallbackAsync(waitLimit.Token);
        await openBrowser(url).ConfigureAwait(false);
        Dictionary<string, string> result;
        try { result = await callback.ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Sign-in was not completed in the browser in time.");
        }
        if (result.TryGetValue("error", out var error))
            throw new InvalidOperationException("Sign-in was refused: " + (result.TryGetValue("error_description", out var d) ? d : error));
        if (!result.TryGetValue("state", out var returned) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(returned), Encoding.UTF8.GetBytes(state)))
            throw new InvalidOperationException("Sign-in returned an unexpected state; nothing was saved.");
        if (!result.TryGetValue("code", out var code)) throw new InvalidOperationException("Sign-in returned no authorization code.");

        using var exchange = new HttpRequestMessage(HttpMethod.Post, token)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirect,
                ["client_id"] = clientId,
                ["code_verifier"] = verifier,
                ["resource"] = resource.ToString().TrimEnd('/'),
            }),
        };
        exchange.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(exchange, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var json = JsonNode.Parse(body) as JsonObject;
        if (!response.IsSuccessStatusCode || json?["access_token"]?.GetValue<string>() is not { Length: > 0 } accessToken)
            throw new InvalidOperationException("The token exchange failed: " + (json?["error_description"] ?? json?["error"])?.ToString());
        return new McpOAuthResult(accessToken, json["scope"]?.GetValue<string>(), resource);
    }

    /// <summary>
    /// The protected-resource metadata for an MCP endpoint: the URL named in the 401 challenge, else the
    /// path-suffixed then root well-known locations.
    /// </summary>
    public async Task<JsonObject> DiscoverResourceMetadataAsync(Uri mcpEndpoint, CancellationToken ct)
    {
        using (var probe = new HttpRequestMessage(HttpMethod.Post, mcpEndpoint)
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", Encoding.UTF8, "application/json"),
        })
        {
            probe.Headers.Accept.ParseAdd("application/json, text/event-stream");
            using var response = await http.SendAsync(probe, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                foreach (var challenge in response.Headers.WwwAuthenticate)
                    if (ResourceMetadataUrl(challenge.Parameter) is { } metadataUrl
                        && SameOriginOrWeb(metadataUrl, mcpEndpoint)
                        && await GetJsonAsync(metadataUrl, ct).ConfigureAwait(false) is { } fromChallenge)
                        return fromChallenge;
        }
        var origin = new Uri(mcpEndpoint.GetLeftPart(UriPartial.Authority));
        var path = mcpEndpoint.AbsolutePath.TrimEnd('/');
        return await GetJsonAsync(new Uri(origin, "/.well-known/oauth-protected-resource" + path), ct).ConfigureAwait(false)
            ?? await GetJsonAsync(new Uri(origin, "/.well-known/oauth-protected-resource"), ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The server does not publish OAuth protected-resource metadata; it may use API keys instead.");
    }

    /// <summary>
    /// Where an issuer's metadata may live, in the order the MCP authorization spec asks clients to try: for an
    /// issuer with a path, RFC 8414 path insertion, then OpenID Connect discovery with path insertion and with
    /// path appending; then the root forms (the only ones for an issuer without a path).
    /// </summary>
    internal static IReadOnlyList<Uri> AuthorizationServerMetadataUrls(Uri issuer)
    {
        var origin = new Uri(issuer.GetLeftPart(UriPartial.Authority));
        var path = issuer.AbsolutePath.TrimEnd('/');
        var urls = new List<Uri>();
        if (path.Length > 0)
        {
            urls.Add(new Uri(origin, "/.well-known/oauth-authorization-server" + path));
            urls.Add(new Uri(origin, "/.well-known/openid-configuration" + path));
            urls.Add(new Uri(origin, path + "/.well-known/openid-configuration"));
        }
        urls.Add(new Uri(origin, "/.well-known/oauth-authorization-server"));
        urls.Add(new Uri(origin, "/.well-known/openid-configuration"));
        return urls;
    }

    /// <summary>
    /// Metadata comes from the server being connected, so its URLs are untrusted: only https, or plain http when
    /// the MCP server itself is on this computer, and never a loopback/private address for a remote server
    /// (the person's browser is about to open it, and this process fetches it).
    /// </summary>
    internal static Uri WebEndpoint(string? value, Uri mcpEndpoint, string what)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || !string.IsNullOrEmpty(url.UserInfo))
            throw new InvalidOperationException($"The server's {what} is not a usable web address.");
        var localServer = mcpEndpoint.IsLoopback;
        var ok = url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && localServer && url.IsLoopback);
        if (!ok) throw new InvalidOperationException($"The server's {what} must be an https address.");
        if (!localServer && (url.IsLoopback || IsPrivateHost(url.Host)))
            throw new InvalidOperationException($"The server's {what} points at a local or private address.");
        return url;
    }

    private static bool IsPrivateHost(string host)
    {
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var ip)) return host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || !host.Contains('.');
        var b = ip.MapToIPv4().GetAddressBytes();
        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31)
               || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || b[0] == 127;
    }

    internal static Uri? ResourceMetadataUrl(string? parameter)
    {
        if (string.IsNullOrEmpty(parameter)) return null;
        const string key = "resource_metadata=";
        var i = parameter.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var value = parameter[(i + key.Length)..].TrimStart();
        value = value.StartsWith('"') ? value[1..value.IndexOf('"', 1)] : value.Split(',')[0].Trim();
        return Uri.TryCreate(value, UriKind.Absolute, out var url) ? url : null;
    }

    private static bool SameOriginOrWeb(Uri url, Uri mcpEndpoint)
    {
        try { WebEndpoint(url.ToString(), mcpEndpoint, "metadata"); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private async Task<JsonObject?> GetJsonAsync(Uri url, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject;
        }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Loopback-only listener for the browser redirect and the client metadata document.</summary>
    private sealed class CallbackListener : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public CallbackListener()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        public int Port { get; }
        public string ClientDocument { get; set; } = "{}";

        public async Task<Dictionary<string, string>> WaitForCallbackAsync(CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
            while (true)
            {
                using var client = await _listener.AcceptTcpClientAsync(linked.Token).ConfigureAwait(false);
                var stream = client.GetStream();
                var target = await ReadRequestTargetAsync(stream, linked.Token).ConfigureAwait(false);
                if (target is null) continue;
                var path = target.Split('?')[0];
                if (path == ClientDocumentPath)
                {
                    await WriteAsync(stream, "200 OK", "application/json", ClientDocument, linked.Token).ConfigureAwait(false);
                    continue;
                }
                if (path != CallbackPath)
                {
                    await WriteAsync(stream, "404 Not Found", "text/plain", "Not found.", linked.Token).ConfigureAwait(false);
                    continue;
                }
                var values = ParseQuery(target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "");
                var ok = !values.ContainsKey("error");
                await WriteAsync(stream, "200 OK", "text/html; charset=utf-8",
                    "<!doctype html><meta charset=utf-8><title>Connected</title><body style=\"font-family:system-ui;margin:3rem\"><h1>"
                    + (ok ? "Connected. You can close this tab." : "Sign-in was not completed.") + "</h1></body>", linked.Token).ConfigureAwait(false);
                return values;
            }
        }

        private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new byte[16 * 1024];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
                if (Encoding.ASCII.GetString(buffer, 0, total).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
            }
            var line = Encoding.ASCII.GetString(buffer, 0, total).Split("\r\n")[0].Split(' ');
            return line.Length >= 2 && line[0] == "GET" ? line[1] : null;
        }

        private static async Task WriteAsync(NetworkStream stream, string status, string type, string body, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var head = $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        }

        private static Dictionary<string, string> ParseQuery(string query)
            => query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .GroupBy(p => Uri.UnescapeDataString(p[0]))
                .ToDictionary(g => g.Key, g => Uri.UnescapeDataString((g.First().Length > 1 ? g.First()[1] : "").Replace('+', ' ')));

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
