using System.Text.RegularExpressions;

namespace SwarmCore.Agent.InstructionChat;

/// <summary>Why a value was detected as a credential.</summary>
public enum DetectedCredentialSource
{
    /// <summary>A vendor token with a distinctive high-entropy prefix (<c>sk-</c>, <c>ghp_</c>, <c>AKIA</c>, ...).</summary>
    KnownPrefix,
    /// <summary>An explicitly labeled value whose label ends with a credential keyword and whose value has a credential shape.</summary>
    Labeled,
}

/// <summary>A credential value found in typed text and the vault name it would be stored under.</summary>
public sealed record DetectedCredential(string Name, string Value)
{
    public DetectedCredentialSource Source { get; init; } = DetectedCredentialSource.Labeled;
}

/// <summary>
/// The shared detector for credentials typed into chat text. The default (<see cref="Detect"/>) is
/// strict, matching Talavine's <c>CredentialPatterns</c> semantics, because it runs over ordinary prose
/// and code where a false positive shreds text or captures data: a value is a credential only when it
/// carries a known vendor token prefix, or when its label ENDS with a credential keyword (password,
/// passphrase, secret, token, api key; never a bare <c>key</c>, so <c>partitionKey</c>/<c>cacheKey</c>
/// are ignored) AND the value itself has a credential shape (no whitespace or code syntax, at least
/// 8 characters with three character classes, or 20+ characters of high entropy). Short hex-only or
/// word-like values such as <c>3f9a8b7c6d5e4f30</c> or <c>user-profile-v2</c> are not credentials;
/// a labeled hex or UUID value of 32+ characters is.
/// <see cref="DetectLenient"/> keeps SwarmCode's original coding-chat intake rules unchanged.
/// Detection never logs, stores, or returns a value anywhere but to its caller.
/// </summary>
public static partial class CredentialShapeDetector
{
    [GeneratedRegex(@"(?<![A-Za-z0-9_-])(?<value>sk-ant-[A-Za-z0-9_-]{20,}|sk-[A-Za-z0-9_-]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{30,}|xox[abpsr]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{30,}|glpat-[A-Za-z0-9_-]{20,}|npm_[A-Za-z0-9]{30,}|pypi-[A-Za-z0-9_-]{30,}|hf_[A-Za-z0-9]{30,}|pat-[a-z]{2}\d-[A-Za-z0-9-]{20,})(?![A-Za-z0-9_-])", RegexOptions.CultureInvariant)]
    private static partial Regex VendorToken();

    [GeneratedRegex(@"^(?:sk-[A-Za-z0-9_-]{20,}|sk-ant-[A-Za-z0-9_-]{20,}|ghp_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|gho_[A-Za-z0-9]{30,}|xox[abps]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{30,}|glpat-[A-Za-z0-9_-]{20,}|npm_[A-Za-z0-9]{30,}|pypi-[A-Za-z0-9_-]{30,}|hf_[A-Za-z0-9]{30,}|eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,})$", RegexOptions.CultureInvariant)]
    private static partial Regex KnownSecretShape();

    // Label (optionally quoted) that ENDS with a credential keyword, then "is", ":" or "=", then the value.
    [GeneratedRegex("""(?<![A-Za-z0-9_])["']?(?<label>(?:[A-Za-z][A-Za-z0-9 ._-]{0,40}?)?(?:password|passwd|pwd|passphrase|secret|token|api[ _-]?key|apikey))["']?(?![A-Za-z0-9_])\s*(?:\bis\b\s*[:=]?|[:=])\s*(?:"(?<dq>[^"\r\n]{4,300})"|'(?<sq>[^'\r\n]{4,300})'|(?<bare>[^\s"',;{}()\[\]]{4,300}))""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LabeledValue();

    /// <summary>
    /// Strict detection (the shared default): known vendor token prefixes anywhere, and explicitly
    /// labeled values that pass <see cref="LooksLikeStrictCredentialValue"/>. Longest first, so a
    /// caller replacing them never lets a shorter value nibble at a longer one.
    /// </summary>
    public static IReadOnlyList<DetectedCredential> Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var found = new List<DetectedCredential>();
        // A {{secret:name}} reference names a vault entry: neither its "secret:" nor its name is a credential.
        text = SwarmCore.Abstractions.Security.CredentialPatterns.BlankSecretReferences(text);

        foreach (Match m in VendorToken().Matches(text))
            Add(GuessName(m.Groups["value"].Value), m.Groups["value"].Value, DetectedCredentialSource.KnownPrefix);

        foreach (Match m in LabeledValue().Matches(text))
        {
            var value = (m.Groups["dq"].Success ? m.Groups["dq"].Value
                : m.Groups["sq"].Success ? m.Groups["sq"].Value
                : m.Groups["bare"].Value).Trim().TrimEnd('.', '!', '?');
            if (!LooksLikeStrictCredentialValue(value)) continue;
            Add(LabelToName(m.Groups["label"].Value), value,
                KnownSecretShape().IsMatch(value) ? DetectedCredentialSource.KnownPrefix : DetectedCredentialSource.Labeled);
        }

        return found.OrderByDescending(c => c.Value.Length).ToList();

        void Add(string name, string value, DetectedCredentialSource source)
        {
            if (found.Any(c => string.Equals(c.Value, value, StringComparison.Ordinal))) return;
            found.Add(new DetectedCredential(name, value) { Source = source });
        }
    }

    /// <summary>
    /// A labeled value has a credential shape: a known token prefix, or no whitespace/code syntax/URL/path
    /// and either (8+ characters with at least three character classes) or (20+ characters with high
    /// Shannon entropy and at least two classes). Short hex-only and word-like identifiers do not
    /// qualify; a 32+ character hex or UUID value does.
    /// </summary>
    public static bool LooksLikeStrictCredentialValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim();
        if (KnownSecretShape().IsMatch(v)) return true;
        if (v.Length < 8 || v.Length > 300 || v.Any(char.IsWhiteSpace)) return false;
        if (v.Contains("{{", StringComparison.Ordinal) || v.Contains("://", StringComparison.Ordinal)) return false;
        if (v.IndexOfAny(['(', ')', '[', ']', '{', '}', '<', '>', ';', ',', '"', '\'', '`', '\\']) >= 0) return false;
        if (v.Contains("..", StringComparison.Ordinal)) return false;
        // Several slashes usually mean a path, but long high-entropy secrets (AWS secret keys,
        // base64 tokens) contain them too; only word-like dotted or low-entropy values are paths.
        if (v.Count(c => c == '/') > 1 && (v.Length < 30 || v.Contains('.') || Entropy(v) < 4.5)) return false;
        // A short hex or dashed-hex value under a credential label is usually an id or hash
        // fragment; at 32+ characters (API keys, UUID tokens, hex secrets) it is a credential.
        if (v.Length < 32 && v.All(c => char.IsAsciiHexDigit(c) || c == '-')) return false;
        if (v.Length >= 32 && v.All(c => char.IsAsciiHexDigit(c) || c == '-')) return true;
        var classes = (v.Any(char.IsUpper) ? 1 : 0) + (v.Any(char.IsLower) ? 1 : 0) + (v.Any(char.IsDigit) ? 1 : 0)
            + (v.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_' && c != '.') ? 1 : 0);
        if (classes >= 3) return true;
        return v.Length >= 20 && classes >= 2 && Entropy(v) >= 3.5;
    }

    private static double Entropy(string v)
        => -v.GroupBy(c => c).Select(g => (double)g.Count() / v.Length).Sum(p => p * Math.Log2(p));

    /// <summary>
    /// SwarmCode's original coding-chat intake rules (unchanged): any value labeled by the instruction
    /// extractor that <see cref="LooksLikeSecretValue"/> accepts. Not the shared default.
    /// </summary>
    public static IReadOnlyList<DetectedCredential> DetectLenient(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var extraction = InstructionSecretExtractor.Extract(text);
        if (extraction.Credentials.Count == 0) return [];
        return extraction.Credentials
            .Where(c => LooksLikeSecretValue(c.Secret))
            .Select(c => new DetectedCredential(RenameForVault(c.Name), c.Secret)
            {
                Source = KnownSecretShape().IsMatch(c.Secret.Trim()) ? DetectedCredentialSource.KnownPrefix : DetectedCredentialSource.Labeled,
            })
            .OrderByDescending(c => c.Value.Length)
            .ToList();
    }

    /// <summary>SwarmCode's lenient value rule (used only by <see cref="DetectLenient"/>).</summary>
    public static bool LooksLikeSecretValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim();
        if (KnownSecretShape().IsMatch(v)) return true;
        if (v.Length < 8 || v.Length > 300) return false;
        if (v.Any(char.IsWhiteSpace)) return false;
        if (v.IndexOfAny(['(', ')', '[', ']', '{', '}', '<', '>', ';', ',', '"', '\'', '`']) >= 0) return false;
        if (v.EndsWith('.') || v.EndsWith(':')) return false;
        if (v.Contains("://", StringComparison.Ordinal) || v.StartsWith("--", StringComparison.Ordinal)) return false;
        if (v.Contains('\\') || (v.Contains('/') && !v.Contains('=') && v.Count(c => c == '/') > 1)) return false; // a path
        if (v.Contains("..", StringComparison.Ordinal)) return false;
        var hasDigit = v.Any(char.IsDigit);
        var hasSymbol = v.Any(c => !char.IsLetterOrDigit(c));
        var hasUpper = v.Any(char.IsUpper);
        var hasLower = v.Any(char.IsLower);
        return (hasDigit && (hasSymbol || v.Length >= 10 || (hasUpper && hasLower))) || (hasSymbol && hasUpper && hasLower && v.Length >= 10);
    }

    /// <summary>Vault name for an extractor label ("mailbox-password" is a mail-instruction label; in general chat it is "password").</summary>
    public static string RenameForVault(string extractorName) => extractorName switch
    {
        "mailbox-password" => "password",
        _ => extractorName,
    };

    private static string LabelToName(string label)
    {
        var chars = label.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var name = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return name.Length == 0 ? "credential" : name.Length <= 60 ? name : name[..60].TrimEnd('-');
    }

    private static string GuessName(string value) => value switch
    {
        _ when value.StartsWith("sk-ant-", StringComparison.Ordinal) => "anthropic-api-key",
        _ when value.StartsWith("sk-", StringComparison.Ordinal) => "openai-api-key",
        _ when value.StartsWith("gh", StringComparison.Ordinal) || value.StartsWith("github_pat_", StringComparison.Ordinal) => "github-token",
        _ when value.StartsWith("xox", StringComparison.Ordinal) => "slack-token",
        _ when value.StartsWith("AKIA", StringComparison.Ordinal) => "aws-access-key",
        _ when value.StartsWith("AIza", StringComparison.Ordinal) => "google-api-key",
        _ when value.StartsWith("glpat-", StringComparison.Ordinal) => "gitlab-token",
        _ when value.StartsWith("npm_", StringComparison.Ordinal) => "npm-token",
        _ when value.StartsWith("pypi-", StringComparison.Ordinal) => "pypi-token",
        _ when value.StartsWith("hf_", StringComparison.Ordinal) => "huggingface-token",
        _ when value.StartsWith("pat-", StringComparison.Ordinal) => "hubspot-token",
        _ => "credential",
    };
}
