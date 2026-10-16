using System.Text;
using System.Text.RegularExpressions;
using SwarmCore.Services.Abstractions;
using SwarmCore.Services.Http;

namespace SwarmCore.Services.Adapters;

/// <summary>
/// Invokes an HTTP service described entirely by config: a base URL + path
/// template (with <c>{arg}</c> placeholders filled from the call args), optional
/// query/body/headers, and an auth scheme that injects the credential at call
/// time. Config keys: baseUrl, method, path, query (k=template;…), headers
/// (k=template;…), body, authScheme (bearer|header|query|none), authName.
/// </summary>
public sealed class HttpServiceAdapter : IServiceKindAdapter
{
    private const int MaxBody = 12000;
    private readonly IServiceHttpClient _http;
    private readonly ICredentialStore _credentials;

    public HttpServiceAdapter(IServiceHttpClient http, ICredentialStore credentials)
    {
        _http = http;
        _credentials = credentials;
    }

    public ServiceKind Kind => ServiceKind.Http;

    public async Task<ServiceCallResult> InvokeAsync(ServiceDefinition s, IReadOnlyDictionary<string, object?> args, CancellationToken ct = default)
    {
        await SwarmCore.Abstractions.Security.ExecutionResourceBoundary.CheckUnclassifiedAsync("service.http", s.Name, ct);
        var cfg = s.Config;
        if (!cfg.TryGetValue("baseUrl", out var baseUrl) || string.IsNullOrWhiteSpace(baseUrl))
            return ServiceCallResult.Fail("Service has no baseUrl configured.");

        var method = cfg.GetValueOrDefault("method", "GET")!.ToUpperInvariant();
        // Path values are data, never URL structure: each is percent-encoded (RFC 6570 simple
        // expansion) so '/', '?', '#' or '@' in an argument cannot move the request, carrying
        // this service's credential, to an endpoint the definition never named.
        string? dotSegment = null;
        var path = Substitute(cfg.GetValueOrDefault("path", ""), args, value =>
        {
            if (value is "." or "..") dotSegment ??= value;
            return Uri.EscapeDataString(value);
        });
        if (dotSegment is not null)
            return ServiceCallResult.Fail($"A path argument cannot be '{dotSegment}'.");
        var url = Combine(baseUrl!, path);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target)
            || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var configured)
            || Uri.Compare(target, configured, UriComponents.SchemeAndServer, UriFormat.UriEscaped, StringComparison.OrdinalIgnoreCase) != 0)
            return ServiceCallResult.Fail("The request URL does not stay on the service's configured host.");

        var query = ParsePairs(cfg.GetValueOrDefault("query"));
        var qsParts = query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(Substitute(kv.Value, args))}").ToList();

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in ParsePairs(cfg.GetValueOrDefault("headers")))
            headers[kv.Key] = Substitute(kv.Value, args);

        // Inject credential per auth scheme.
        var secret = string.IsNullOrEmpty(s.CredentialRef) ? null : await _credentials.GetAsync(s.CredentialRef!, ct).ConfigureAwait(false);
        var scheme = cfg.GetValueOrDefault("authScheme", string.IsNullOrEmpty(secret) ? "none" : "bearer")!.ToLowerInvariant();
        if (!string.IsNullOrEmpty(secret))
        {
            switch (scheme)
            {
                case "bearer": headers["Authorization"] = "Bearer " + secret; break;
                case "header": headers[cfg.GetValueOrDefault("authName", "Authorization")!] = secret; break;
                case "query": qsParts.Add($"{Uri.EscapeDataString(cfg.GetValueOrDefault("authName", "key")!)}={Uri.EscapeDataString(secret)}"); break;
            }
        }

        if (qsParts.Count > 0) url += (url.Contains('?') ? "&" : "?") + string.Join("&", qsParts);

        string? body = cfg.TryGetValue("body", out var b) && !string.IsNullOrWhiteSpace(b) ? Substitute(b!, args) : null;

        var resp = await _http.SendAsync(new ServiceHttpRequest(method, url, headers, body), ct).ConfigureAwait(false);
        // APIs echo the request (Authorization header, ?key= URL) in error bodies; the result
        // goes to the model and the transcript, so the injected credential is scrubbed first.
        var received = string.IsNullOrEmpty(secret) ? resp.Body : CredentialEcho.Scrub(resp.Body, secret);
        var text = received.Length > MaxBody ? received[..MaxBody] + "…(truncated)" : received;
        return resp.Ok
            ? ServiceCallResult.Success(text)
            : ServiceCallResult.Fail($"HTTP {resp.Status}: {text}");
    }

    private static string Substitute(string? template, IReadOnlyDictionary<string, object?> args, Func<string, string>? encode = null)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;
        return Regex.Replace(template, @"\{(\w+)\}", m =>
        {
            if (!args.TryGetValue(m.Groups[1].Value, out var v)) return m.Value;
            var value = v?.ToString() ?? "";
            return encode is null ? value : encode(value);
        });
    }

    private static IEnumerable<KeyValuePair<string, string>> ParsePairs(string? config)
    {
        if (string.IsNullOrWhiteSpace(config)) yield break;
        foreach (var part in config.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = part.IndexOf('=');
            if (i > 0) yield return new(part[..i].Trim(), part[(i + 1)..].Trim());
        }
    }

    private static string Combine(string baseUrl, string path)
    {
        if (string.IsNullOrEmpty(path)) return baseUrl;
        return baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');
    }
}
