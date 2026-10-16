using System.Text;
using System.Text.RegularExpressions;

namespace SwarmCore.Secrets;

/// <summary>Naming rules for vault entries and the <c>{{secret:name}}</c> reference syntax agents use.</summary>
public static partial class SecretNames
{
    /// <summary>Prefix for LLM provider API keys: <c>llm.&lt;provider&gt;</c>.</summary>
    public const string LlmPrefix = "llm.";

    public const int MaxLength = 120;

    /// <summary>
    /// Lower-cases, trims, and replaces anything outside <c>a-z 0-9 . _ / -</c> with a dash,
    /// so "GitHub Token" and "github-token" address the same entry.
    /// </summary>
    public static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw new ArgumentException("A secret name is required.", nameof(raw));
        var sb = new StringBuilder(raw.Length);
        var lastDash = false;
        foreach (var ch in raw.Trim().ToLowerInvariant())
        {
            var ok = ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '/' or '-';
            var c = ok ? ch : '-';
            if (c == '-')
            {
                if (lastDash) continue;
                lastDash = true;
            }
            else lastDash = false;
            sb.Append(c);
        }
        var name = sb.ToString().Trim('-', '.', '/');
        if (name.Length == 0) throw new ArgumentException("A secret name must contain a letter or digit.", nameof(raw));
        return name.Length <= MaxLength ? name : name[..MaxLength].TrimEnd('-', '.', '/');
    }

    public static bool TryNormalize(string? raw, out string name)
    {
        name = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        try { name = Normalize(raw); return true; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>Vault name for a provider's API key.</summary>
    public static string ForLlmProvider(string provider) => LlmPrefix + Normalize(provider);

    /// <summary>
    /// Vault name for a website login: the host without "www." plus the username when given,
    /// for example <c>github.com/octocat</c>. Falls back to the label when the URL has no host.
    /// </summary>
    public static string ForSite(string? url, string? username, string? label = null)
    {
        var host = Host(url) ?? (string.IsNullOrWhiteSpace(label) ? null : label);
        if (host is null) throw new ArgumentException("A site URL or label is required.");
        return string.IsNullOrWhiteSpace(username) ? Normalize(host) : Normalize(host + "/" + username.Trim());
    }

    /// <summary>Host of a URL (or bare host string) without a leading "www.", or null.</summary>
    public static string? Host(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var text = url.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return null;
        var host = uri.Host.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    /// <summary>A display hint for a username that does not disclose it: "s***@example.com" or "s***".</summary>
    public static string? MaskUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        var u = username.Trim();
        var at = u.IndexOf('@');
        if (at > 0) return u[0] + "***" + u[at..];
        return u.Length <= 1 ? "*" : u[0] + "***";
    }
}

/// <summary>Result of expanding <c>{{secret:name}}</c> references in text.</summary>
public sealed record SecretExpansion(
    string Text,
    IReadOnlyList<KeyValuePair<string, string>> Resolved,
    IReadOnlyList<string> Missing)
{
    public bool HasReferences => Resolved.Count > 0 || Missing.Count > 0;
}

/// <summary>
/// The <c>{{secret:name}}</c> reference syntax. Models write references; host adapters
/// expand them at the moment of use (HTTP header, shell environment, command line) and
/// scrub the resolved values back out of anything that returns to the model.
/// </summary>
public static partial class SecretReference
{
    [GeneratedRegex(SecretReferenceSyntax.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReferencePattern();

    public static Regex Pattern => ReferencePattern();

    public static string Placeholder(string name) => "{{secret:" + name + "}}";

    public static bool ContainsReference(string? text)
        => !string.IsNullOrEmpty(text) && text.Contains("{{", StringComparison.Ordinal) && ReferencePattern().IsMatch(text);

    /// <summary>Distinct referenced names in order of first appearance.</summary>
    public static IReadOnlyList<string> Names(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var names = new List<string>();
        foreach (Match m in ReferencePattern().Matches(text))
        {
            var name = m.Groups["name"].Value;
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }
        return names;
    }

    /// <summary>
    /// Replaces every reference with the resolved value. Unresolvable names are left in
    /// place and reported in <see cref="SecretExpansion.Missing"/> so the caller can fail
    /// loudly instead of sending a literal placeholder to a program.
    /// </summary>
    public static async Task<SecretExpansion> ExpandAsync(
        string? text,
        Func<string, CancellationToken, Task<string?>> resolve,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text) || !ContainsReference(text))
            return new SecretExpansion(text ?? "", [], []);

        var resolved = new List<KeyValuePair<string, string>>();
        var missing = new List<string>();
        var cache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder(text.Length);
        var last = 0;
        foreach (Match m in ReferencePattern().Matches(text))
        {
            var name = m.Groups["name"].Value;
            if (!cache.TryGetValue(name, out var value))
            {
                value = await resolve(name, ct).ConfigureAwait(false);
                cache[name] = value;
                if (string.IsNullOrEmpty(value)) missing.Add(name);
                else resolved.Add(new KeyValuePair<string, string>(name, value));
            }
            sb.Append(text, last, m.Index - last);
            sb.Append(string.IsNullOrEmpty(value) ? m.Value : value);
            last = m.Index + m.Length;
        }
        sb.Append(text, last, text.Length - last);
        return new SecretExpansion(sb.ToString(), resolved, missing);
    }

    /// <summary>Replaces each resolved value in <paramref name="text"/> with its placeholder, longest value first.</summary>
    public static string Scrub(string? text, IReadOnlyList<KeyValuePair<string, string>> resolved)
    {
        if (string.IsNullOrEmpty(text) || resolved.Count == 0) return text ?? "";
        var result = text;
        foreach (var pair in resolved.OrderByDescending(p => p.Value.Length))
        {
            if (pair.Value.Length < 4) continue; // too short to scrub meaningfully without mangling output
            result = result.Replace(pair.Value, Placeholder(pair.Key), StringComparison.Ordinal);
        }
        return result;
    }
}
