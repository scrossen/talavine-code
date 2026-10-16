using System.Net;
using System.Security.Cryptography;
using System.Text;
using SwarmCore.Abstractions.Networking;

namespace SwarmCore.Agent.Business;

/// <summary>
/// Finite host-approved resources, not a model-controlled URL prefix. A GET to an
/// invented path can leak data or perform an action despite having no request body.
/// </summary>
public sealed class ReviewedPublicReadPolicy
{
    private readonly HashSet<string> resources;
    public ReviewedPublicReadPolicy(IEnumerable<string> reviewedUris)
    {
        var values = reviewedUris.Take(65).ToArray();
        if (values.Length > 64) throw new ArgumentException("The reviewed public-read resource budget was exceeded.");
        resources = values.Select(value => ValidateShape(value).AbsoluteUri).ToHashSet(StringComparer.Ordinal);
        Fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("\n", resources.Order(StringComparer.Ordinal)))));
    }
    public string Fingerprint { get; }
    public Uri Require(string value)
    {
        var uri = ValidateShape(value);
        if (!resources.Contains(uri.AbsoluteUri)) throw Denied();
        return uri;
    }
    private static Uri ValidateShape(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Contains('%') || value.Contains('\\')
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || IPAddress.TryParse(uri.Host.Trim('[', ']'), out _) || uri.HostNameType != UriHostNameType.Dns
            || !uri.Host.Contains('.') || uri.Host.EndsWith(".", StringComparison.Ordinal)
            || value.Split('/').Any(part => part is "." or "..")) throw Denied();
        return uri;
    }
    private static UnauthorizedAccessException Denied() => new("Public read denied: the exact host-reviewed HTTPS resource is required.");
}

/// <summary>Final physical public read boundary. Never inherits caller headers or follows a redirect.</summary>
public sealed class ReviewedPublicReadHandler(ReviewedPublicReadPolicy policy, HttpMessageHandler transport)
    : DelegatingHandler(transport)
{
    public const int MaximumResponseBytes = 1048576;
    public static HttpMessageHandler PhysicalTransport()
    {
        var handler = PublicNetworkAddress.CreateGuardedHandler(allowLoopback: false, allowAutoRedirect: false);
        handler.UseCookies = false;
        handler.Credentials = null;
        handler.PreAuthenticate = false;
        handler.AutomaticDecompression = DecompressionMethods.None;
        handler.ConnectTimeout = TimeSpan.FromSeconds(10);
        return handler;
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Method != HttpMethod.Get || request.Content is not null || request.RequestUri is null
            || request.Headers.Any(header => header.Key != "User-Agent"))
            throw new UnauthorizedAccessException("Public read denied: only an uncredentialed, bodyless resource GET is admitted.");
        var uri = policy.Require(request.RequestUri.OriginalString);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        // The ordinary descriptor supplies a constant browser User-Agent. Do not
        // forward it, or any caller-owned request object across physical awaits.
        using var physical = new HttpRequestMessage(HttpMethod.Get, uri);
        var response = await base.SendAsync(physical, deadline.Token);
        try
        {
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new UnauthorizedAccessException("Public read denied: redirects require independent resource review.");
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                throw new IOException("Public read exceeded its response byte budget.");
            using var output = new MemoryStream();
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
            var buffer = new byte[8192];
            int length;
            while ((length = await input.ReadAsync(buffer, deadline.Token)) > 0)
            {
                if (output.Length + length > MaximumResponseBytes) throw new IOException("Public read exceeded its response byte budget.");
                output.Write(buffer, 0, length);
            }
            var contentType = response.Content.Headers.ContentType;
            response.Content.Dispose();
            response.Content = new ByteArrayContent(output.ToArray());
            response.Content.Headers.ContentType = contentType;
            return response;
        }
        catch { response.Dispose(); throw; }
    }
}
