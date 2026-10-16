namespace SwarmCore.Abstractions.Crm;

/// <summary>Pure URI policy for sending credentials and opening the existing desktop handoff.</summary>
public static class CrmLinkPolicy
{
    public const string DefaultPath = "/dashboard";
    public const string HandoffPath = "/Account/CompleteDesktopHandoff";
    public const int MaxTargetLength = 2_000;

    public static bool TryGetOrigin(string? baseUrl, out Uri origin)
    {
        origin = null!;
        if (!Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(parsed.Host)
            || !string.IsNullOrEmpty(parsed.UserInfo) || parsed.AbsolutePath != "/"
            || !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment)) return false;
        origin = parsed;
        return true;
    }

    public static bool SameOrigin(string? first, string? second) =>
        TryGetOrigin(first, out var left) && TryGetOrigin(second, out var right) && SameOrigin(left, right);

    private static bool SameOrigin(Uri left, Uri right) =>
        left.Scheme == right.Scheme && string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port && string.IsNullOrEmpty(right.UserInfo);

    public static bool TryResolve(string? baseUrl, string? link, out Uri absolute, out string target)
    {
        absolute = null!;
        target = string.Empty;
        if (!TryGetOrigin(baseUrl, out var origin)) return false;
        var value = string.IsNullOrWhiteSpace(link) ? DefaultPath : link.Trim();
        if (!value.StartsWith('/') && Uri.TryCreate(value, UriKind.Absolute, out var supplied))
        {
            if (!SameOrigin(origin, supplied)) return false;
            value = supplied.PathAndQuery + supplied.Fragment;
        }
        else if (!value.StartsWith('/')) value = "/" + value;
        if (!IsValidTarget(value) || !Uri.TryCreate(origin, value, out var result)
            || !SameOrigin(origin, result) || IsHandoffPath(result.AbsolutePath)
            || IsAuthenticationEntryPath(result.AbsolutePath)) return false;
        target = result.PathAndQuery + result.Fragment;
        absolute = result;
        return true;
    }

    public static bool IsValidTarget(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxTargetLength || value[0] != '/'
            || value.StartsWith("//", StringComparison.Ordinal) || value.Contains('\\')
            || value.Contains("://", StringComparison.Ordinal)
            || value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))) return false;
        // Encoded authority separators and control characters are not navigation paths.
        var decodedPath = value.Split('?', '#')[0];
        for (var pass = 0; pass < 3; pass++)
        {
            decodedPath = Uri.UnescapeDataString(decodedPath);
            if (decodedPath.StartsWith("//", StringComparison.Ordinal) || decodedPath.Contains('\\')
                || decodedPath.Any(char.IsControl)) return false;
        }
        return Uri.TryCreate(value, UriKind.Relative, out _);
    }

    /// <summary>Keep an isolated CRM window inside its tenant; handoffs are only allowed on first load.</summary>
    public static bool IsAllowedPageNavigation(string baseUrl, string? url, bool allowHandoff = false)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 16_384
            || !TryGetOrigin(baseUrl, out var origin) || !Uri.TryCreate(url, UriKind.Absolute, out var page)
            || !SameOrigin(origin, page) || !IsValidTarget(page.AbsolutePath)
            || url.Any(char.IsControl)) return false;
        return !IsAuthenticationEntryPath(page.AbsolutePath) && (allowHandoff || !IsHandoffPath(page.AbsolutePath));
    }

    private static bool IsHandoffPath(string path)
    {
        return string.Equals(DecodePath(path), HandoffPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAuthenticationEntryPath(string path)
    {
        path = DecodePath(path);
        return path.StartsWith("/Account/Login", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/Account/Register", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "/Account/ExternalLogin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "/Account/PerformExternalLogin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "/Account/CompleteTenantLogin", StringComparison.OrdinalIgnoreCase);
    }

    private static string DecodePath(string path)
    {
        for (var pass = 0; pass < 3; pass++) path = Uri.UnescapeDataString(path);
        return path.TrimEnd('/');
    }

    /// <summary>Validate the short-lived credential without returning it in an error or log.</summary>
    public static bool IsValidHandoff(string baseUrl, string target, string? url, DateTime? expiresAtUtc, DateTime utcNow)
    {
        if (!TryGetOrigin(baseUrl, out var origin) || !Uri.TryCreate(url, UriKind.Absolute, out var handoff)
            || !SameOrigin(origin, handoff) || handoff.AbsolutePath != HandoffPath
            || !string.IsNullOrEmpty(handoff.Fragment) || expiresAtUtc is null || expiresAtUtc <= utcNow) return false;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in handoff.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            var value = parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty;
            if (!values.TryAdd(key, value)) return false;
        }
        return values.Count == 2 && values.TryGetValue("ticket", out var ticket) && !string.IsNullOrWhiteSpace(ticket)
            && values.TryGetValue("target", out var destination) && string.Equals(destination, target, StringComparison.Ordinal);
    }
}
