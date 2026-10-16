namespace SwarmCore.Abstractions.Security;

/// <summary>Metadata-only matching shared by credential forms and host adapters.</summary>
public static class SavedCredentialMatching
{
    public static bool Matches(SavedCredentialQuery query, string? site, string? username, string? label)
    {
        if (query.RequireUsername && string.IsNullOrWhiteSpace(username)) return false;
        if (!string.IsNullOrWhiteSpace(query.Username) && !UsernamesMatch(query.Username, username)) return false;
        // Password-free-account fields are typically API keys/tokens/client secrets. A domain alone
        // cannot distinguish those from the person's website login at the same provider.
        if (!query.RequireUsername && !LabelMatches(query.ServiceHint, label)) return false;

        if (!string.IsNullOrWhiteSpace(site))
        {
            var host = SiteHost(site);
            if (host is null) return false;
            return (query.Sites ?? []).Select(SiteHost).OfType<string>().Any(expected =>
                host.Equals(expected, StringComparison.OrdinalIgnoreCase)
                || (Uri.CheckHostName(expected) == UriHostNameType.Dns
                    && host.EndsWith("." + expected, StringComparison.OrdinalIgnoreCase)));
        }

        // A populated but unrelated URL is never overridden by a friendly label.
        return LabelMatches(query.ServiceHint, label);
    }

    private static bool LabelMatches(string? serviceHint, string? label)
    {
        var hint = Tokens(serviceHint);
        var name = Tokens(label);
        if (hint.Length == 0) return false;
        // Short names must identify the entire service; only generic credential nouns may follow.
        // "X password" is useful, while "Company X" must not select an unrelated credential.
        if (hint.All(token => token.Length < 3))
            return hint.SequenceEqual(name.Where(token => !CredentialNouns.Contains(token)), StringComparer.OrdinalIgnoreCase);
        return hint.All(token => name.Contains(token, StringComparer.OrdinalIgnoreCase));
    }

    public static bool UsernamesMatch(string? left, string? right)
        => NormalizeUsername(left).Equals(NormalizeUsername(right), StringComparison.OrdinalIgnoreCase);

    public static string? SiteHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        var host = uri.IdnHost.TrimEnd('.');
        return Uri.CheckHostName(host) == UriHostNameType.Unknown ? null : host.ToLowerInvariant();
    }

    private static string NormalizeUsername(string? value)
    {
        var text = value?.Trim() ?? "";
        return text.StartsWith('@') && !text.AsSpan(1).Contains('@') ? text[1..] : text;
    }

    private static string[] Tokens(string? value)
        => new string((value ?? "").Select(character => char.IsLetterOrDigit(character) ? character : ' ').ToArray())
            .Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static readonly HashSet<string> CredentialNouns = new(StringComparer.OrdinalIgnoreCase)
        { "password", "login", "account" };
}
