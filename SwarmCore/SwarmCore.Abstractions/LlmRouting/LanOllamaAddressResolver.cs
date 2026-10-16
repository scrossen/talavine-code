using System.Net;
using System.Net.Sockets;

namespace SwarmCore.Abstractions.LlmRouting;

public static class LanOllamaAddressResolver
{
    public static async Task<string?> ResolveAsync(string name, CancellationToken ct = default,
        Func<string, CancellationToken, Task<IPAddress[]>>? lookup = null)
    {
        name = name.Trim();
        if (Uri.CheckHostName(name) == UriHostNameType.Unknown) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var addresses = await (lookup ?? Dns.GetHostAddressesAsync)(name, timeout.Token);
            return addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                .Where(a => !IPAddress.IsLoopback(a))
                .Select(a => new UriBuilder("http", a.ToString(), 11434).Uri.AbsoluteUri.TrimEnd('/'))
                .FirstOrDefault(LlmProviderLocalityPolicy.IsLocalEndpoint);
        }
        catch (SocketException) { return null; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }
}
