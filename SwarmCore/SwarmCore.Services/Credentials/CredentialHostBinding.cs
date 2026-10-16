namespace SwarmCore.Services.Credentials;

/// <summary>
/// Host binding for credentials sent by general-purpose request tools. A credential may go only to a host it is
/// bound to (or a subdomain of one); stores report bindings through
/// <see cref="Abstractions.ICredentialStore.GetBoundHostsAsync"/>, and HTTP service definitions bind their
/// credential to their configured base URL.
/// </summary>
public static class CredentialHostBinding
{
    /// <summary>The lower-case host of a binding entry written as a host ("api.example.com") or a URL.</summary>
    public static string? HostOf(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return null;
        var text = entry.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return null;
        var host = uri.IdnHost.ToLowerInvariant();
        // A site saved as www.example.com binds the registrable site, as a browser login would.
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    /// <summary>True when <paramref name="host"/> is one of <paramref name="boundEntries"/> or a subdomain of one.</summary>
    public static bool Allows(IEnumerable<string> boundEntries, string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var target = host.Trim().TrimEnd('.').ToLowerInvariant();
        foreach (var entry in boundEntries)
        {
            var bound = HostOf(entry);
            if (bound is null) continue;
            if (target == bound || target.EndsWith("." + bound, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// The base-URL hosts of enabled HTTP service definitions that authenticate with <paramref name="credentialName"/>.
    /// <paramref name="sameName"/> decides whether a definition's credential reference names the same stored credential.
    /// </summary>
    public static IReadOnlyList<string> FromServiceDefinitions(
        IEnumerable<ServiceDefinition> definitions, string credentialName, Func<string, string, bool>? sameName = null)
    {
        sameName ??= static (a, b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        var hosts = new List<string>();
        foreach (var definition in definitions)
        {
            if (!definition.Enabled || definition.Kind != ServiceKind.Http || string.IsNullOrWhiteSpace(definition.CredentialRef)
                || !sameName(definition.CredentialRef!, credentialName)
                || !definition.Config.TryGetValue("baseUrl", out var baseUrl)
                || HostOf(baseUrl) is not { } host
                || hosts.Contains(host, StringComparer.Ordinal))
                continue;
            hosts.Add(host);
        }
        return hosts;
    }
}
