using System.Net;
using SwarmCore.Abstractions.Networking;

namespace SwarmCore.Services.Http;

/// <summary>
/// Shared SSRF choke point: decides whether a URL points somewhere on the public internet.
/// Anything that fetches a URL chosen by a model, by authored package code, or by content that
/// arrived from outside (email, web page, document) must run it through here first, or the fetch
/// becomes a way to read the host's cloud instance-metadata endpoint (169.254.169.254) and the
/// user's internal network.
///
/// This started life as private helpers on the CRM's Vibe web fetcher; it lives here so the
/// desktop app's primitive <c>http.request</c> tool and the CRM share one implementation.
/// </summary>
public static class PublicAddressGuard
{
    /// <summary>
    /// True when <paramref name="uri"/> resolves to an address we must refuse. Resolves DNS so a
    /// public name that points at a private IP is still caught, and refuses if ANY resolved
    /// address is non-public (defends against DNS that returns a mix).
    /// </summary>
    /// <param name="blockLoopback">
    /// Block 127.0.0.0/8 and ::1. True for untrusted authored code. False for the user's own
    /// agent tools, where "check my local Ollama at localhost:11434" is a legitimate request.
    /// </param>
    /// <param name="blockUnresolvable">
    /// Refuse when DNS can't resolve the host. True fails closed. False lets the caller's own
    /// request fail naturally on connect — the same resolver is about to fail there too, so
    /// nothing reachable slips through, and offline/dev environments don't get a security error
    /// for what is really a network outage.
    /// </param>
    public static async Task<bool> IsBlockedHostAsync(
        Uri uri,
        CancellationToken ct = default,
        bool blockLoopback = true,
        bool blockUnresolvable = true)
    {
        var host = uri.Host;
        IEnumerable<IPAddress> addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = new[] { literal };
        }
        else
        {
            try { addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false); }
            catch { return blockUnresolvable; }
        }

        return addresses.Any(ip => IsNonPublic(ip, blockLoopback));
    }

    /// <summary>
    /// True when the address is not a globally routable unicast address — private, link-local
    /// (incl. cloud metadata), CGNAT, multicast/broadcast, unspecified, IPv6 site-local/ULA, and
    /// IPv6 forms that a NAT64/6to4 gateway would deliver to such an IPv4 host — following
    /// <see cref="PublicNetworkAddress"/>. Loopback is refused unless
    /// <paramref name="blockLoopback"/> is false (the user's own local services).
    /// </summary>
    public static bool IsNonPublic(IPAddress ip, bool blockLoopback = true)
    {
        if (!blockLoopback && IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip)) return false;
        return !PublicNetworkAddress.IsPublic(ip);
    }
}
