using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace SwarmCore.Abstractions.Networking;

/// <summary>
/// Outbound-fetch (SSRF) policy shared by every server that fetches a URL a
/// client supplied: only globally routable unicast addresses may be reached.
/// IPv6 forms that carry an IPv4 address (mapped, IPv4-compatible, NAT64 and
/// 6to4) are judged by the IPv4 address they lead to, because a NAT64 or 6to4
/// gateway would deliver them to a private IPv4 host.
/// </summary>
public static class PublicNetworkAddress
{
    /// <summary>
    /// Conventional named <see cref="System.Net.Http.IHttpClientFactory"/> client that hosts
    /// register with <see cref="CreateGuardedHandler"/> for fetching untrusted URLs.
    /// </summary>
    public const string HttpClientName = "public-network";

    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (EmbeddedIPv4(address) is { } embedded)
        {
            address = embedded;
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(b[0] == 10
                     || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                     || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 169 && b[1] == 254)
                     || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                     || (b[0] == 192 && b[1] == 0 && b[2] == 0)             // 192.0.0.0/24 IETF protocol assignments
                     || (b[0] == 192 && b[1] == 0 && b[2] == 2)             // 192.0.2.0/24 documentation
                     || (b[0] == 192 && b[1] == 88 && b[2] == 99)           // 192.88.99.0/24 6to4 relay anycast
                     || (b[0] == 198 && (b[1] == 18 || b[1] == 19))         // 198.18.0.0/15 benchmarking (fake-IP proxies)
                     || (b[0] == 198 && b[1] == 51 && b[2] == 100)          // 198.51.100.0/24 documentation
                     || (b[0] == 203 && b[1] == 0 && b[2] == 113)           // 203.0.113.0/24 documentation
                     || b[0] == 0 || b[0] >= 224);
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        // Allowlist: only 2000::/3 global unicast is reachable on the public internet. Everything
        // else (::/8 reserved, 100::/64 discard, local-use NAT64 that is not in /96 form, ULA,
        // link-local, site-local, multicast) is refused.
        if ((b[0] & 0xE0) != 0x20)
        {
            return false;
        }

        return !((b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00)  // 2001::/32 Teredo
                 || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x02 && b[4] == 0x00 && b[5] == 0x00) // 2001:2::/48 benchmarking
                 || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8)  // 2001:db8::/32 documentation
                 || (b[0] == 0x3F && b[1] == 0xFF && (b[2] & 0xF0) == 0x00));       // 3fff::/20 documentation
    }

    /// <summary>
    /// True for this machine's loopback (127.0.0.0/8, ::1, IPv4-mapped loopback). NAT64 and 6to4
    /// forms of 127.x are not loopback: a gateway would deliver them somewhere else.
    /// </summary>
    public static bool IsLocalLoopback(IPAddress address)
        => IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    /// <summary>
    /// An HTTP handler that may only connect to public addresses, checked against every address
    /// DNS returns at connect time. Because the check runs per connection it also covers each
    /// redirect hop and DNS rebinding, which a pre-flight host check does not. System proxies are
    /// bypassed so the check sees the real destination.
    /// </summary>
    /// <param name="allowLoopback">
    /// Also allow this machine's loopback — for the user's own local services on the desktop.
    /// Servers (CRM, website) must pass false.
    /// </param>
    /// <param name="allowAutoRedirect">Follow redirects; each hop is still address-checked.</param>
    public static SocketsHttpHandler CreateGuardedHandler(bool allowLoopback, bool allowAutoRedirect = false) => new()
    {
        AllowAutoRedirect = allowAutoRedirect,
        MaxAutomaticRedirections = 5,
        UseProxy = false,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            // Refuse when any answer is non-public, so a mixed DNS answer cannot be raced.
            if (addresses.Length == 0
                || addresses.Any(a => !IsPublic(a) && !(allowLoopback && IsLocalLoopback(a))))
            {
                throw new HttpRequestException($"Refusing to connect to a non-public address ({host}).");
            }

            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    if (cancellationToken.IsCancellationRequested) throw;
                }
            }

            throw new HttpRequestException($"Could not connect to {host}.");
        },
    };

    private static IPAddress? EmbeddedIPv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return null;
        if (address.IsIPv4MappedToIPv6) return address.MapToIPv4();

        var b = address.GetAddressBytes();
        // ::a.b.c.d (IPv4-compatible, deprecated). :: and ::1 are handled as IPv6.
        if (b.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0 && (b[12] | b[13]) != 0)
            return new IPAddress(b.AsSpan(12, 4));
        // 64:ff9b::/96 well-known NAT64 prefix.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0)
            return new IPAddress(b.AsSpan(12, 4));
        // 64:ff9b:1::/48 local-use NAT64 prefix (RFC 8215). The operator picks the prefix length
        // (/48, /56, /64 or /96, RFC 6052), and with a shorter one the IPv4 address sits in bytes
        // 6-12, not the last 32 bits. Only the /96 form (bytes 6-11 zero) is judged by its last 32
        // bits; any other form could carry a private IPv4 address the check would not see, so it
        // is returned as unspecified (refused).
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b[4] == 0x00 && b[5] == 0x01)
            return b.AsSpan(6, 6).IndexOfAnyExcept((byte)0) < 0 ? new IPAddress(b.AsSpan(12, 4)) : IPAddress.Any;
        // 2002::/16 6to4: the IPv4 address follows the prefix.
        if (b[0] == 0x20 && b[1] == 0x02)
            return new IPAddress(b.AsSpan(2, 4));
        return null;
    }
}
