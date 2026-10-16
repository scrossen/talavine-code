using System.Text.RegularExpressions;

namespace SwarmCore.Agent.InstructionChat;

/// <summary>
/// Provider-neutral, deterministic secret pre-pass for instruction chat.
/// Values are captured before a model sees the message and replaced by stable
/// <c>{{secret:name}}</c> references that a host can bind to its own vault.
/// </summary>
public static partial class InstructionSecretExtractor
{
    [GeneratedRegex("\"(?<name>[^\"\\r\\n]{1,60})\"\\s*[:=]\\s*\"(?<value>[^\"\\r\\n]{4,300})\"", RegexOptions.Compiled)]
    private static partial Regex JsonSecretPattern();

    [GeneratedRegex("(?:(?<service>[A-Za-z][A-Za-z0-9._-]{1,30})\\s+)?(?<label>api[ -]?key|access[ -]?token|auth[ -]?token|bearer[ -]?token|client[ -]?secret|secret[ -]?key|password|passwd|pwd|token)(?:\\s+for\\s+(?<service2>[A-Za-z][A-Za-z0-9._-]{1,30}))?\\s*(?:is\\b\\s*[:=-]?\\s*|[:=-]\\s*)[\"'\\x60]?(?<value>[^\"'\\x60\\s{}]{4,300})[\"'\\x60]?", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex LabeledSecretPattern();

    [GeneratedRegex("\\b(?<value>sk-[A-Za-z0-9_-]{20,}|ghp_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|xox[abps]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{30,}|vk_[A-Za-z0-9_-]{25,})\\b", RegexOptions.Compiled)]
    private static partial Regex KnownSecretPattern();

    private static readonly string[] SecretNameFragments =
        ["password", "passwd", "pwd", "secret", "token", "apikey", "api-key", "api_key", "key"];

    public static InstructionExtraction Extract(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return new InstructionExtraction(rawText ?? string.Empty, []);

        var sanitized = rawText;
        var credentials = new List<InstructionCredential>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in JsonSecretPattern().Matches(rawText))
        {
            var property = match.Groups["name"].Value;
            var value = match.Groups["value"].Value;
            if (!LooksLikeSecretName(property) || LooksLikeReference(value)) continue;
            Add(property, value);
        }

        foreach (Match match in LabeledSecretPattern().Matches(sanitized))
        {
            var value = TrimPunctuation(match.Groups["value"].Value);
            if (value.Length < 4 || LooksLikeReference(value)) continue;
            var service = FirstNonEmpty(match.Groups["service2"].Value, match.Groups["service"].Value);
            Add(string.IsNullOrWhiteSpace(service) ? match.Groups["label"].Value : service, value);
        }

        foreach (Match match in KnownSecretPattern().Matches(sanitized))
        {
            var value = match.Groups["value"].Value;
            if (LooksLikeReference(value)) continue;
            Add(GuessName(value), value);
        }

        return new InstructionExtraction(sanitized, credentials);

        void Add(string rawName, string value)
        {
            if (credentials.Any(c => string.Equals(c.Secret, value, StringComparison.Ordinal))) return;
            var baseName = NormalizeName(rawName);
            if (baseName.Length == 0) baseName = "credential";
            if (baseName is "password" or "passwd" or "pwd") baseName = "mailbox-password";
            var name = baseName;
            for (var suffix = 2; !usedNames.Add(name); suffix++) name = $"{baseName}-{suffix}";
            credentials.Add(new InstructionCredential(name, value));
            sanitized = sanitized.Replace(value, Placeholder(name), StringComparison.Ordinal);
        }
    }

    public static string Scrub(string? text, InstructionExtraction extraction)
    {
        var scrubbed = text ?? string.Empty;
        foreach (var credential in extraction.Credentials)
            scrubbed = scrubbed.Replace(credential.Secret, Placeholder(credential.Name), StringComparison.Ordinal);
        return scrubbed;
    }

    public static string Placeholder(string name) => $"{{{{secret:{name}}}}}";

    private static bool LooksLikeSecretName(string name)
    {
        var normalized = name.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return SecretNameFragments.Any(normalized.Contains)
               && !normalized.Contains("url", StringComparison.Ordinal)
               && !normalized.Contains("endpoint", StringComparison.Ordinal)
               && !normalized.Contains("name", StringComparison.Ordinal);
    }

    private static bool LooksLikeReference(string value) =>
        value.Contains("{{secret:", StringComparison.OrdinalIgnoreCase)
        || value.Contains("://", StringComparison.Ordinal)
        || value.Equals("none", StringComparison.OrdinalIgnoreCase)
        || value.Equals("null", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeName(string value)
    {
        var words = Regex.Replace(value.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return words.Length <= 48 ? words : words[..48].TrimEnd('-');
    }

    private static string GuessName(string value) => value switch
    {
        _ when value.StartsWith("ghp_", StringComparison.Ordinal) || value.StartsWith("github_pat_", StringComparison.Ordinal) => "github-api-key",
        _ when value.StartsWith("xox", StringComparison.Ordinal) => "slack-token",
        _ when value.StartsWith("AKIA", StringComparison.Ordinal) => "aws-access-key",
        _ when value.StartsWith("AIza", StringComparison.Ordinal) => "google-api-key",
        _ when value.StartsWith("vk_", StringComparison.Ordinal) => "swarmcrm",
        _ => "api-key",
    };

    private static string TrimPunctuation(string value) => value.TrimEnd('.', ',', ';', ':');

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}
