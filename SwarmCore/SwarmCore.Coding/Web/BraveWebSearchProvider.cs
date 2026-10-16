using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SwarmCore.Coding.Web;

/// <summary>
/// Brave Search API (<c>/res/v1/web/search</c>) behind <see cref="IWebSearchProvider"/>. The key comes from
/// <see cref="IWebSearchKeySource"/> on every call (so a key saved mid-session works at once) and is sent only
/// in the <c>X-Subscription-Token</c> header; it never appears in a result, a log line, or an error message.
/// </summary>
public sealed partial class BraveWebSearchProvider(
    IHttpClientFactory httpClientFactory,
    IWebSearchKeySource keys,
    IOptions<WebToolsOptions> options,
    ILogger<BraveWebSearchProvider>? logger = null) : IWebSearchProvider
{
    /// <summary>Named <see cref="IHttpClientFactory"/> client the provider uses.</summary>
    public const string HttpClientName = "swarmcore-web-search";
    public const string SubscriptionTokenHeader = "X-Subscription-Token";

    public string Name => "Brave Search";

    public async Task<IReadOnlyList<WebSearchHit>> SearchAsync(WebSearchQuery query, CancellationToken ct)
    {
        var o = options.Value;
        var key = (await keys.GetKeyAsync(ct).ConfigureAwait(false))?.Trim();
        if (string.IsNullOrEmpty(key)) throw new WebSearchUnavailableException(o.MissingKeyGuidance, notConfigured: true);

        var q = query.Query.Trim();
        if (NormalizeSite(query.Site) is { } site) q += " site:" + site;
        var count = Math.Clamp(query.Count, 1, Math.Min(20, Math.Max(1, o.MaxCount)));
        var url = $"{o.BraveEndpoint}?q={Uri.EscapeDataString(q)}&count={count}";
        if (NormalizeFreshness(query.Freshness) is { } freshness) url += "&freshness=" + Uri.EscapeDataString(freshness);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation(SubscriptionTokenHeader, key);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, o.TimeoutSeconds)));
        try
        {
            using var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                logger?.LogWarning("Brave Search returned HTTP {Status}.", status);
                throw new WebSearchUnavailableException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                        $"Brave Search rejected the stored API key (HTTP {status}). Ask the person to check or replace it in web search settings.",
                    HttpStatusCode.TooManyRequests =>
                        "Brave Search rate limit or plan quota reached (HTTP 429). Wait and retry, or reduce the number of searches.",
                    HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest =>
                        $"Brave Search refused the request (HTTP {status}). Simplify the query or remove the freshness/site filters.",
                    _ => $"Brave Search is unavailable right now (HTTP {status}). Retry later.",
                });
            }

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token).ConfigureAwait(false);
            return Parse(doc.RootElement, count);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WebSearchUnavailableException("Brave Search did not answer in time. Retry the search.");
        }
        catch (HttpRequestException ex)
        {
            logger?.LogWarning("Brave Search request failed: {Error}", Scrub(ex.Message, key));
            throw new WebSearchUnavailableException("Brave Search could not be reached: " + Scrub(ex.Message, key));
        }
        catch (JsonException)
        {
            throw new WebSearchUnavailableException("Brave Search returned a response that could not be read. Retry the search.");
        }
    }

    internal static IReadOnlyList<WebSearchHit> Parse(JsonElement root, int count)
    {
        var hits = new List<WebSearchHit>();
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("web", out var web) || web.ValueKind != JsonValueKind.Object
            || !web.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return hits;
        foreach (var r in results.EnumerateArray())
        {
            if (hits.Count >= count) break;
            var url = Str(r, "url");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) continue;
            var age = Str(r, "age");
            if (string.IsNullOrWhiteSpace(age)) age = Str(r, "page_age");
            hits.Add(new WebSearchHit(Clean(Str(r, "title")), uri.AbsoluteUri, Clean(Str(r, "description")), string.IsNullOrWhiteSpace(age) ? null : Clean(age)));
        }
        return hits;
    }

    /// <summary>Maps day/week/month/year (or Brave's pd/pw/pm/py, or a YYYY-MM-DDtoYYYY-MM-DD range) to Brave's freshness value.</summary>
    internal static string? NormalizeFreshness(string? freshness)
    {
        if (string.IsNullOrWhiteSpace(freshness)) return null;
        var f = freshness.Trim().ToLowerInvariant();
        return f switch
        {
            "pd" or "day" or "24h" or "today" => "pd",
            "pw" or "week" => "pw",
            "pm" or "month" => "pm",
            "py" or "year" => "py",
            _ when DateRange().IsMatch(f) => f,
            _ => throw new ArgumentException("freshness must be day, week, month, year, or a range like 2026-01-01to2026-03-31."),
        };
    }

    /// <summary>Accepts a bare domain (or a URL whose host is used). Anything else is refused.</summary>
    internal static string? NormalizeSite(string? site)
    {
        if (string.IsNullOrWhiteSpace(site)) return null;
        var s = site.Trim();
        if (Uri.TryCreate(s, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) s = uri.Host;
        s = s.TrimEnd('/').ToLowerInvariant();
        if (!SiteName().IsMatch(s)) throw new ArgumentException("site must be a domain such as learn.microsoft.com.");
        return s;
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Brave marks matches with &lt;strong&gt; and HTML-encodes text; return plain text.</summary>
    internal static string Clean(string s) => WebUtility.HtmlDecode(Tags().Replace(s, "")).Trim();

    private static string Scrub(string message, string key) => message.Replace(key, "[redacted]", StringComparison.Ordinal);

    [GeneratedRegex("<[^>]*>")] private static partial Regex Tags();
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}to\d{4}-\d{2}-\d{2}$")] private static partial Regex DateRange();
    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9-]{2,63}$")] private static partial Regex SiteName();
}
