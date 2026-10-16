using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.OAuth;

namespace SwarmCore.Mcp.OAuth;

public sealed class RemoteOAuthClientOptions
{
    public string[] AllowedClientHosts { get; set; } = [];
    public bool AllowLoopbackClients { get; set; }
}

public sealed record RemoteOAuthClient(Uri ClientIdUrl, OAuthClientIdDocument Document)
{
    public string DisplayName => Document.DisplayName(ClientIdUrl);
}

/// <summary>
/// Fetches and validates Client ID Metadata Documents. The fetch is the one place an unauthenticated
/// caller makes this server issue an outbound request, so it refuses private, loopback, and link-local
/// destinations at connect time (after DNS, so a rebinding hostname cannot slip through), caps size
/// and time, and caches results briefly.
/// </summary>
public sealed class RemoteOAuthClientResolver
{
    public const string HttpClientName = "remote-oauth-client-metadata";
    private const int MaxDocumentBytes = 8 * 1024;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly IOptionsMonitor<RemoteOAuthClientOptions> _options;
    private readonly ILogger<RemoteOAuthClientResolver> _logger;

    public RemoteOAuthClientResolver(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IOptionsMonitor<RemoteOAuthClientOptions> options,
        ILogger<RemoteOAuthClientResolver> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _options = options;
        _logger = logger;
    }

    /// <summary>Returns the client, or an error safe to show on the authorization page.</summary>
    public async Task<(RemoteOAuthClient? Client, string? Error)> ResolveAsync(
        string? clientId,
        string? redirectUri,
        CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var urlError = OAuthClientIdPolicy.ValidateClientIdUrl(clientId, options.AllowLoopbackClients, out var url);
        if (urlError is not null)
        {
            return (null, urlError);
        }

        if (options.AllowedClientHosts.Length > 0
            && !options.AllowedClientHosts.Contains(url!.Host, StringComparer.OrdinalIgnoreCase))
        {
            return (null, $"{url.Host} is not an approved assistant for this data source.");
        }

        var document = await _cache.GetOrCreateAsync(
            "oauth-cimd:" + url!.OriginalString,
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CacheLifetime;
                return await FetchAsync(url, options.AllowLoopbackClients, cancellationToken);
            });

        var documentError = OAuthClientIdPolicy.ValidateDocument(url, document, redirectUri);
        if (documentError is not null)
        {
            // A bad document must not stay cached if the publisher fixes it.
            _cache.Remove("oauth-cimd:" + url.OriginalString);
            return (null, documentError);
        }

        return (new RemoteOAuthClient(url, document!), null);
    }

    private async Task<OAuthClientIdDocument?> FetchAsync(Uri url, bool allowLoopback, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(allowLoopback ? HttpClientName + ":loopback" : HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxDocumentBytes)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[MaxDocumentBytes + 1];
            var read = 0;
            int n;
            while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken)) > 0)
            {
                read += n;
            }

            return read > MaxDocumentBytes
                ? null
                : OAuthClientIdDocument.Parse(System.Text.Encoding.UTF8.GetString(buffer, 0, read));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _logger.LogInformation(ex, "Client metadata document {Url} could not be fetched.", url);
            return null;
        }
    }

    /// <summary>Registers the named HTTP clients with the connect-time address guard.</summary>
    public static void AddHttpClients(IServiceCollection services)
    {
        services.AddHttpClient(HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(5))
            .ConfigurePrimaryHttpMessageHandler(() => CreateHandler(allowLoopback: false));
        services.AddHttpClient(HttpClientName + ":loopback", c => c.Timeout = TimeSpan.FromSeconds(5))
            .ConfigurePrimaryHttpMessageHandler(() => CreateHandler(allowLoopback: true));
    }

    // The shared guard refuses every non-public answer at connect time and bypasses system
    // proxies: through a proxy the connect-time check would only ever see the proxy's address.
    private static SocketsHttpHandler CreateHandler(bool allowLoopback)
        => SwarmCore.Abstractions.Networking.PublicNetworkAddress.CreateGuardedHandler(allowLoopback, allowAutoRedirect: false);

    /// <summary>True for globally routable unicast addresses only.</summary>
    public static bool IsPublic(IPAddress address) => SwarmCore.Abstractions.Networking.PublicNetworkAddress.IsPublic(address);
}
