using System.Net;
using System.Net.Sockets;
using SwarmCore.Agent.Marketing;

namespace SwarmCore.Services.Http;

/// <summary>Credential-free, bounded public business-page transport. DNS is checked at connection time.</summary>
public sealed class PublicBusinessPageClient : IDisposable
{
    private readonly HttpClient _http;
    public PublicBusinessPageClient()
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectCallback = ConnectPublicAsync,
        }) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Talavine-BusinessResearch/1.0");
    }
    internal PublicBusinessPageClient(HttpMessageHandler handler) => _http = new HttpClient(handler);

    public async Task<(string Url, string Html)> GetHtmlAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        ct = timeout.Token;
        if (!LeadResearchUrlPolicy.TryGetBusinessUri(url, out var uri)) throw new HttpRequestException("Unsupported business URL.");
        var domain = LeadResearchUrlPolicy.Domain(uri!);
        for (var redirect = 0; redirect <= 3; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var target = response.Headers.Location;
                if (target is null || !Uri.TryCreate(uri, target, out var next)
                    || !LeadResearchUrlPolicy.TryGetBusinessUri(next.AbsoluteUri, out next)
                    || LeadResearchUrlPolicy.Domain(next!) != domain)
                    throw new HttpRequestException("Cross-business or unsupported redirect.");
                uri = next; continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentType?.MediaType is not ("text/html" or "application/xhtml+xml" or "text/plain"))
                throw new HttpRequestException("Not a text business page.");
            await response.Content.LoadIntoBufferAsync(1_048_576, ct).ConfigureAwait(false);
            return (uri!.AbsoluteUri, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        throw new HttpRequestException("Too many redirects.");
    }

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(IsBlocked)) throw new HttpRequestException("Non-public address.");
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); if (ct.IsCancellationRequested) throw; }
        }
        throw new HttpRequestException("Business site unavailable.");
    }
    internal static bool IsBlocked(IPAddress address) => PublicAddressGuard.IsNonPublic(address);
    public void Dispose() => _http.Dispose();
}
