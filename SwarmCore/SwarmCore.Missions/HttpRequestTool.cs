using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmCore.Agent;
using SwarmCore.Agent.Catalog;
using SwarmCore.Services.Abstractions;
using SwarmCore.Services.Http;

namespace SwarmCore.Missions;

/// <summary>
/// Options for the <see cref="HttpRequestTool"/>.
/// </summary>
public sealed class HttpRequestToolOptions
{
    /// <summary>
    /// Hostnames the agent may call. Empty list = allow any host (default for
    /// personal-use installs). Set to a concrete list to restrict.
    /// </summary>
    public List<string> AllowedHosts { get; set; } = [];

    /// <summary>Hard upper bound on response body size returned to the model. Default 128 KB.</summary>
    public int MaxResponseBytes { get; set; } = 128 * 1024;

}

/// <summary>
/// Primitive: issue an arbitrary HTTP request. Supports configurable host
/// allowlist; when the allowlist is empty every host is permitted.
/// </summary>
public sealed class HttpRequestTool
{
    public const string ToolId = "http.request";

    /// <summary>
    /// Named <see cref="IHttpClientFactory"/> client with auto-redirect disabled. The default
    /// client follows 3xx itself, which would walk straight past the address guard below — a
    /// public URL that redirects to http://169.254.169.254/ would be fetched anyway.
    /// </summary>
    public const string NoRedirectClientName = "primitive-http-request";

    public sealed class Input
    {
        [Description("Full URL including scheme.")]
        public required string Url { get; set; }

        [Description("HTTP method. Default GET.")]
        public string? Method { get; set; }

        [Description("Optional request headers.")]
        public Dictionary<string, string>? Headers { get; set; }

        [Description("Optional request body. Must be a string (JSON / text). Binary bodies are not supported. " +
                     "A JSON body is sent as application/json unless a Content-Type header says otherwise.")]
        public string? Body { get; set; }

        [Description("Optional caller-selected request deadline in seconds. When omitted, the request runs until it completes or the surrounding work is cancelled.")]
        public int? TimeoutSeconds { get; set; }
    }

    public sealed class Output : IToolExecutionEvidence
    {
        [System.Text.Json.Serialization.JsonIgnore] public bool Succeeded => StatusCode is >= 200 and < 400;
        [System.Text.Json.Serialization.JsonIgnore] public bool LocalProcessCompleted => false;
        public required int StatusCode { get; init; }
        public required Dictionary<string, string> Headers { get; init; }
        public required string Body { get; init; }
        public required bool Truncated { get; init; }
    }

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HttpRequestToolOptions _options;
    private readonly ILogger<HttpRequestTool> _logger;
    private readonly ICredentialStore? _credentialStore;

    public HttpRequestTool(
        IHttpClientFactory httpClientFactory,
        IOptions<HttpRequestToolOptions> options,
        ILogger<HttpRequestTool> logger,
        ICredentialStore? credentialStore = null)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
        _credentialStore = credentialStore;
    }

    public ToolDescriptor Descriptor => ToolDescriptor.Create<Input, Output>(
        toolId: ToolId,
        enforcesMissionResourceBoundary: true,
        ownerAgentId: "primitive",
        description:
            "Issue an HTTP request (GET/POST/PUT/DELETE/PATCH) and return status, headers, and body. " +
            "http/https only, and redirects are not followed: a 3xx comes back with its Location header " +
            "so you can re-issue the request against that URL if you still want it. Header values may " +
            "reference locked credentials as {{secret:name}} (including inside values such as " +
            "'Bearer {{secret:name}}'); the secret is injected only at send time, only for a host the credential " +
            "is bound to, and never returned. " +
            "There is no fixed request deadline unless you supply TimeoutSeconds; the surrounding work remains cancellable.",
        cost: ToolCostClass.External,
        sideEffect: ToolSideEffect.ExternalSend,
        // GET/HEAD stay read-only (safe to repeat, so mission recovery may retry them); one that carries composed
        // data still goes through the invocation guard, like a send.
        resolveSideEffect: input => IsReadMethod(input) ? ToolSideEffect.ReadOnly : ToolSideEffect.ExternalSend,
        transmitsComposedData: input => !IsPlainRead(input),
        keywords: ["http", "web", "fetch", "api", "rest", "curl", "request"],
        invoke: async (input, ct) =>
        {
            if (!Uri.TryCreate(input.Url, UriKind.Absolute, out var uri))
            {
                throw new ArgumentException($"Invalid URL: '{input.Url}'.");
            }

            // http/https only. Without this, file:// turns this tool into a local-file reader
            // whose result is then fenced as "WEB CONTENT".
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new ArgumentException(
                    $"Only http and https URLs are supported; '{uri.Scheme}' is not. Use the fs.* tools for local files.");
            }

            await MissionPrimitiveFence.AuthorizeAsync(
                new(SwarmCore.Agent.Continual.MissionResourceKind.Endpoint, uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped)),
                string.IsNullOrWhiteSpace(input.Method) || input.Method.Equals("GET", StringComparison.OrdinalIgnoreCase)
                    || input.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
                    ? SwarmCore.Agent.Continual.MissionEffect.Inspect : SwarmCore.Agent.Continual.MissionEffect.Send, ct);

            // SSRF guard. Loopback stays allowed on purpose — "check my local Ollama at
            // localhost:11434" is a normal request here — but the private, CGNAT, link-local and
            // metadata ranges are refused, and an unresolvable host is left to fail on connect.
            if (await PublicAddressGuard.IsBlockedHostAsync(uri, ct, blockLoopback: false, blockUnresolvable: false))
            {
                throw new UnauthorizedAccessException(
                    $"'{uri.Host}' resolves to a private or link-local address, which this tool will not fetch. " +
                    "Only public internet addresses (and localhost) are reachable.");
            }

            if (_options.AllowedHosts.Count > 0 &&
                !_options.AllowedHosts.Any(h => string.Equals(h, uri.Host, StringComparison.OrdinalIgnoreCase) ||
                                                uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase)))
            {
                throw new UnauthorizedAccessException(
                    $"Host '{uri.Host}' is not in the allowed-hosts list. Add it to HttpRequestToolOptions.AllowedHosts to permit.");
            }

            using var client = _httpClientFactory.CreateClient(NoRedirectClientName);
            if (input.TimeoutSeconds is <= 0)
                throw new ArgumentOutOfRangeException(nameof(input.TimeoutSeconds), "TimeoutSeconds must be positive when supplied.");
            client.Timeout = input.TimeoutSeconds is { } timeoutSeconds
                ? TimeSpan.FromSeconds(timeoutSeconds)
                : Timeout.InfiniteTimeSpan;

            var method = string.IsNullOrWhiteSpace(input.Method) ? "GET" : input.Method.ToUpperInvariant();
            using var request = new HttpRequestMessage(new HttpMethod(method), uri);

            if (!string.IsNullOrEmpty(input.Body))
            {
                // StringContent would label everything text/plain, which makes every JSON API
                // answer 415. Callers rarely think to send Content-Type, so infer it from the
                // body and let an explicit header below override the guess.
                request.Content = new StringContent(input.Body, Encoding.UTF8, InferMediaType(input.Body));
            }
            // The same {{secret:name}} resolution as shell.exec: every reference resolves (or the call fails with the
            // stored names) before anything is sent, and resolved values are scrubbed from what comes back.
            var secrets = new ShellSecretReferences(_credentialStore);
            var headerValues = new List<KeyValuePair<string, string>>();
            foreach (var kv in input.Headers ?? [])
                headerValues.Add(new(kv.Key, await secrets.ExpandAsync(kv.Value, ct) ?? kv.Value));
            await secrets.ThrowIfMissingAsync(ct);
            // Every turn, fenced or not: a referenced credential goes only to a host it is bound to.
            await secrets.ThrowIfNotBoundToHostAsync(uri.IdnHost, ct);
            foreach (var (name, value) in headerValues)
            {
                // Content headers need to live on the content, not the request.
                if (request.Headers.TryAddWithoutValidation(name, value) || request.Content is null)
                {
                    continue;
                }

                // Remove before adding: StringContent has already set Content-Type to
                // text/plain, and TryAddWithoutValidation will not replace an existing
                // single-value header. Without the removal a JSON body ships as
                // text/plain and every JSON API answers 415.
                request.Content.Headers.Remove(name);
                request.Content.Headers.TryAddWithoutValidation(name, value);
            }

            // Headers first: a large download or an endless stream is read only up to the cap, never buffered whole.
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var (rawBody, truncated) = await ReadBoundedAsync(response.Content, _options.MaxResponseBytes, ct);
            if (truncated) _logger.LogDebug("http.request response from {Host} was cut at {Bytes} bytes.", uri.Host, _options.MaxResponseBytes);
            var body = secrets.Scrub(truncated ? rawBody + "…" : rawBody);

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in response.Headers) headers[h.Key] = secrets.Scrub(string.Join(", ", h.Value));
            foreach (var h in response.Content.Headers) headers[h.Key] = secrets.Scrub(string.Join(", ", h.Value));

            // Redirects are surfaced, not followed, so the guard above sees every hop. Say so
            // explicitly — otherwise a bare 302 with an empty body looks like a failed fetch.
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = headers.TryGetValue("Location", out var loc) ? loc : "(no Location header)";
                body = $"Redirect not followed automatically. Location: {location}\n" +
                       "Call this tool again with that URL to follow it deliberately.\n\n" + body;
            }

            // Remote responses are third-party content — same fence as email bodies.
            body = UntrustedContent.Fence(body, "WEB CONTENT");

            return new Output
            {
                StatusCode = (int)response.StatusCode,
                Headers = headers,
                Body = body,
                Truncated = truncated
            };
        });

    /// <summary>GET or HEAD (the default method is GET): the request reads, so repeating it changes nothing.</summary>
    internal static bool IsReadMethod(Input input)
    {
        var method = string.IsNullOrWhiteSpace(input.Method) ? "GET" : input.Method.Trim();
        return method.Equals("GET", StringComparison.OrdinalIgnoreCase)
               || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A GET/HEAD that carries no data the model composed beyond a plain address: no body, no
    /// headers (which can carry {{secret:...}} references or conversation data) and no query or
    /// fragment. Anything else can transmit data, so the invocation guard (taint/destination
    /// provenance) inspects it even when it is a read.
    /// </summary>
    internal static bool IsPlainRead(Input input)
    {
        if (!IsReadMethod(input))
            return false;
        if (!string.IsNullOrEmpty(input.Body) || input.Headers is { Count: > 0 })
            return false;
        // An unparseable URL is rejected at invocation; classify it conservatively meanwhile.
        return Uri.TryCreate(input.Url, UriKind.Absolute, out var uri)
               && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    }

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> of the body (one more byte proves truncation) and decodes only that
    /// prefix with the response's charset, or its byte-order mark, or UTF-8. The rest of the response is never read.
    /// </summary>
    internal static async Task<(string Text, bool Truncated)> ReadBoundedAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        var limit = Math.Max(0, maxBytes);
        using var buffer = new MemoryStream();
        await using (var stream = await content.ReadAsStreamAsync(ct))
        {
            var chunk = new byte[16 * 1024];
            while (buffer.Length <= limit)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, limit + 1L - buffer.Length)), ct);
                if (read == 0) break;
                buffer.Write(chunk, 0, read);
            }
        }
        var truncated = buffer.Length > limit;
        var bytes = buffer.GetBuffer().AsSpan(0, (int)Math.Min(buffer.Length, limit));
        var encoding = DetectEncoding(content.Headers.ContentType?.CharSet, bytes, out var preamble);
        bytes = bytes[preamble..];
        // A cut can split a multi-byte character: without a flush the decoder leaves that partial tail out.
        var decoder = encoding.GetDecoder();
        var chars = new char[encoding.GetMaxCharCount(bytes.Length)];
        var count = decoder.GetChars(bytes, chars, flush: !truncated);
        return (new string(chars, 0, count), truncated);
    }

    private static Encoding DetectEncoding(string? charset, ReadOnlySpan<byte> bytes, out int preamble)
    {
        foreach (var bom in new[] { Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode })
        {
            if (bom.Preamble.Length > 0 && bytes.StartsWith(bom.Preamble))
            {
                preamble = bom.Preamble.Length;
                return bom;
            }
        }
        preamble = 0;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { return Encoding.GetEncoding(charset.Trim('"', ' ')); }
            catch (ArgumentException) { /* unknown charset: fall back to UTF-8 like HttpContent does */ }
        }
        return Encoding.UTF8;
    }

    /// <summary>
    /// Best-effort media type for a body the caller did not label. Only JSON is
    /// sniffed — it is the format APIs reject outright when mislabeled — and the
    /// guess is confirmed by an actual parse so prose starting with a brace is
    /// not mislabeled either. Everything else keeps the text/plain default.
    /// </summary>
    private static string InferMediaType(string body)
    {
        var trimmed = body.AsSpan().Trim();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return "text/plain";
        }

        try
        {
            using var _ = JsonDocument.Parse(body);
            return "application/json";
        }
        catch (JsonException)
        {
            return "text/plain";
        }
    }
}
