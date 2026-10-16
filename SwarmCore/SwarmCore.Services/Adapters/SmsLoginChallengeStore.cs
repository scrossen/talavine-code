using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace SwarmCore.Services.Adapters;

/// <summary>Ephemeral login-only authority. Restart and uncertain submission require a new sign-in.</summary>
public sealed class SmsLoginChallengeStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _pending = new();
    public void Record(string key, DateTimeOffset startedAt) => _pending[key] = startedAt;
    public bool TryGet(string key, DateTimeOffset now, out DateTimeOffset startedAt)
    {
        if (_pending.TryGetValue(key, out startedAt) && startedAt <= now && now - startedAt < TimeSpan.FromMinutes(5)) return true;
        _pending.TryRemove(key, out _);
        return false;
    }
    public bool Consume(string key, DateTimeOffset startedAt)
        => ((ICollection<KeyValuePair<string, DateTimeOffset>>)_pending).Remove(new(key, startedAt));

    /// <summary>Local-only conservative matching. Neither the body nor the returned code belongs in model context.</summary>
    public static string? MatchCode(string body, string siteHost)
    {
        var labels = siteHost.ToLowerInvariant().Split('.');
        // Match the registrable identity, never generic subdomains such as
        // "cards" or "login". Common ccTLD namespaces are structural metadata,
        // not a list of providers. Unknown/short identities fail closed.
        var suffixLength = labels.Length >= 3 && labels[^1].Length == 2
            && labels[^2] is "co" or "com" or "net" or "org" or "ac" or "edu" or "gov" ? 2 : 1;
        if (labels.Length <= suffixLength || labels[^(suffixLength + 1)].Length < 4) return null;
        var identities = new[] { labels[^(suffixLength + 1)] };
        // Allow spaces in a brand's domain label, but never match a prefix of
        // a different name (e.g. "Example" must not match "Exampleshire").
        if (!identities.Any(label => Regex.IsMatch(body, @"(?<![a-zA-Z0-9])"
            + string.Join(@"[\s\p{P}]*", Regex.Replace(label, "[^a-z0-9]", "").Select(c => Regex.Escape(c.ToString())))
            + @"(?![a-zA-Z0-9])", RegexOptions.IgnoreCase))) return null;
        // The phrase, refused-purpose, and single-code rules are shared with email sign-in.
        return LoginVerificationCodeMatcher.MatchShortMessageCode(body);
    }
}
