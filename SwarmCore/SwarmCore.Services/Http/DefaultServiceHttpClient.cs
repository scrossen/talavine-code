using System.Text;
using SwarmCore.Abstractions.Networking;
using SwarmCore.Services.Abstractions;

namespace SwarmCore.Services.Http;

/// <summary>
/// Default <see cref="IServiceHttpClient"/> over <see cref="IHttpClientFactory"/> — every
/// host needs essentially this same wrapper, so it lives here instead of being
/// reimplemented per app. Hosts with existing bespoke implementations can keep them; new
/// hosts wiring up <c>AddSwarmCoreServices()</c> should register this by default.
/// </summary>
public sealed class DefaultServiceHttpClient : IServiceHttpClient
{
    /// <summary>
    /// The largest response body one call reads. Authored code and models choose these URLs,
    /// and callers keep only a bounded prefix of the text anyway, so a multi-gigabyte or
    /// never-ending body is refused instead of being materialized in process memory.
    /// </summary>
    public const long MaxResponseBytes = 16L * 1024 * 1024;

    // Authored service and tool code chooses these URLs, so by default they may only reach the
    // public internet, checked at connect time (covers redirects and DNS rebinding).
    private static readonly HttpClient PublicRedirectClient = Guarded(allowLoopback: false, redirects: true);
    private static readonly HttpClient PublicNoRedirectClient = Guarded(allowLoopback: false, redirects: false);
    private static readonly HttpClient LoopbackRedirectClient = Guarded(allowLoopback: true, redirects: true);
    private static readonly HttpClient LoopbackNoRedirectClient = Guarded(allowLoopback: true, redirects: false);

    // Factory-created clients can't change handler settings per request, so allowlist-gated
    // calls (AllowRedirects=false) go through this shared no-redirect client: a 3xx comes back
    // to the caller instead of silently hopping to a host the allowlist never checked.
    private static readonly HttpClient NoRedirectClient = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        // The objective/progress lease owns cancellation. A fixed wall-clock
        // timeout kills healthy long-running exports, uploads, and APIs.
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly IHttpClientFactory _factory;
    private readonly string _userAgent;
    private readonly ServiceNetworkReach _reach;

    public DefaultServiceHttpClient(
        IHttpClientFactory factory,
        string userAgent = "SwarmCore.Services/1.0",
        ServiceNetworkReach reach = ServiceNetworkReach.PublicOnly)
    {
        _factory = factory;
        _userAgent = userAgent;
        _reach = reach;
    }

    public async Task<ServiceHttpResponse> SendAsync(ServiceHttpRequest request, CancellationToken ct = default)
    {
        if (_reach != ServiceNetworkReach.Unrestricted)
        {
            var loopback = _reach == ServiceNetworkReach.PublicAndLoopback;
            var client = request.AllowRedirects
                ? loopback ? LoopbackRedirectClient : PublicRedirectClient
                : loopback ? LoopbackNoRedirectClient : PublicNoRedirectClient;
            return await SendCoreAsync(client, request, ct).ConfigureAwait(false);
        }

        if (!request.AllowRedirects)
        {
            return await SendCoreAsync(NoRedirectClient, request, ct).ConfigureAwait(false);
        }

        try
        {
            using var client = _factory.CreateClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            return await SendCoreAsync(client, request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ServiceHttpResponse(0, ex.Message, false);
        }
    }

    private async Task<ServiceHttpResponse> SendCoreAsync(HttpClient client, ServiceHttpRequest request, CancellationToken ct)
    {
        try
        {
            using var msg = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
            msg.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            if (request.Headers is not null)
                foreach (var h in request.Headers) msg.Headers.TryAddWithoutValidation(h.Key, h.Value);
            if (request.BodyBytes is not null)
            {
                msg.Content = new ByteArrayContent(request.BodyBytes);
                msg.Content.Headers.TryAddWithoutValidation(
                    "Content-Type", request.ContentType ?? "application/octet-stream");
            }
            else if (!string.IsNullOrEmpty(request.Body))
                msg.Content = new StringContent(request.Body, Encoding.UTF8, "application/json");

            // Headers first, then a capped buffer: the default completion option would buffer
            // the whole body (up to 2 GB) before this method saw the response.
            using var resp = await client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var status = (int)resp.StatusCode;
            if (resp.Content.Headers.ContentLength is > MaxResponseBytes)
                return TooLarge(status);
            try
            {
                await resp.Content.LoadIntoBufferAsync(MaxResponseBytes, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (!ct.IsCancellationRequested)
            {
                // Chunked or mislabelled bodies are only found to be too large while reading.
                return TooLarge(status);
            }
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return new ServiceHttpResponse(status, body, resp.IsSuccessStatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller's lease or objective stopped the call; that is not an HTTP failure.
            throw;
        }
        catch (Exception ex)
        {
            return new ServiceHttpResponse(0, ex.Message, false);
        }
    }

    private static ServiceHttpResponse TooLarge(int status)
        => new(status, $"The response body was larger than {MaxResponseBytes / (1024 * 1024)} MiB and was not read.", false);

    private static HttpClient Guarded(bool allowLoopback, bool redirects)
        => new(PublicNetworkAddress.CreateGuardedHandler(allowLoopback, redirects))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
}

/// <summary>Which networks a <see cref="DefaultServiceHttpClient"/> may reach.</summary>
public enum ServiceNetworkReach
{
    /// <summary>Public internet only. Required for servers and untrusted authored code.</summary>
    PublicOnly = 0,

    /// <summary>Public internet plus this machine's loopback (the user's own local services).</summary>
    PublicAndLoopback = 1,

    /// <summary>No address restriction; uses the host's <see cref="IHttpClientFactory"/> client.</summary>
    Unrestricted = 2,
}
