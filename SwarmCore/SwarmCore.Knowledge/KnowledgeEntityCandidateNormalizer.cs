using System.Text.RegularExpressions;

namespace SwarmCore.Knowledge;

/// <summary>
/// Validates and canonicalizes graph entity candidates produced by extraction.
/// This policy is source-neutral so desktop messages and CRM evidence mint the
/// same graph identity from the same candidate text.
/// </summary>
public static class KnowledgeEntityCandidateNormalizer
{
    public const int MaxEntityNameLength = 160;
    public const int MaxPredicateLength = 160;

    private static readonly Regex EmailAddressRegex = new(
        @"^[\w\.\-\+%]+@[\w\.\-]+\.[A-Z]{2,}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> CommonTldArtifacts = new(StringComparer.OrdinalIgnoreCase)
    {
        "com", "org", "net", "edu", "gov", "mil", "io", "ai", "co", "us", "uk", "ca",
        "test", "example", "invalid", "local"
    };

    public static bool TryNormalize(
        string? value,
        KnowledgeEntityType type,
        bool allowEmailIdentifier,
        out string normalized)
    {
        normalized = string.Empty;
        var cleaned = KnowledgeEntitySurfaceNormalizer.Normalize(value);
        if (cleaned.Length == 0)
            return false;

        if (cleaned.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            cleaned = KnowledgeEntitySurfaceNormalizer.Normalize(cleaned[7..]);

        cleaned = StripPossessiveArtifact(cleaned);
        cleaned = KnowledgeEntitySurfaceNormalizer.Normalize(cleaned);
        if (cleaned.Length == 0
            || IsOneCharacterAlphaToken(cleaned)
            || !cleaned.Any(char.IsLetterOrDigit)
            || cleaned.Length > MaxEntityNameLength)
        {
            return false;
        }

        if (IsEmailAddress(cleaned))
        {
            if (!allowEmailIdentifier)
                return false;

            normalized = cleaned.ToLowerInvariant();
            return true;
        }

        if (LooksLikeEmailOrDomainArtifact(cleaned, type))
            return false;

        normalized = cleaned;
        return true;
    }

    public static IReadOnlyList<string> NormalizeDistinct(
        IEnumerable<string> values,
        KnowledgeEntityType type)
    {
        ArgumentNullException.ThrowIfNull(values);
        var normalizedValues = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (!TryNormalize(value, type, allowEmailIdentifier: false, out var normalized))
                continue;

            if (seen.Add(NormalizeCanonicalSegment(normalized)))
                normalizedValues.Add(normalized);
        }

        return normalizedValues;
    }

    public static string NormalizePredicate(string? value)
    {
        var normalized = Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", " ");
        return normalized.Length <= MaxPredicateLength
            ? normalized
            : normalized[..MaxPredicateLength];
    }

    public static string BuildExplicitRelationEntityKey(string normalizedName)
    {
        ArgumentNullException.ThrowIfNull(normalizedName);
        var canonical = NormalizeCanonicalSegment(normalizedName);
        return normalizedName.Contains('@', StringComparison.Ordinal)
            ? $"person:{canonical}"
            : $"other:{canonical}";
    }

    public static KnowledgeEntityType InferExplicitRelationEntityType(string normalizedName)
    {
        ArgumentNullException.ThrowIfNull(normalizedName);
        return normalizedName.Contains('@', StringComparison.Ordinal)
            ? KnowledgeEntityType.Person
            : KnowledgeEntityType.Other;
    }

    public static string NormalizeCanonicalSegment(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Regex.Replace(
            KnowledgeEntitySurfaceNormalizer.Normalize(value).ToLowerInvariant(),
            @"\s+",
            " ");
    }

    public static string StripCanonicalPrefix(string canonicalKey)
    {
        ArgumentNullException.ThrowIfNull(canonicalKey);
        var colon = canonicalKey.IndexOf(':', StringComparison.Ordinal);
        return colon >= 0 && colon < canonicalKey.Length - 1
            ? canonicalKey[(colon + 1)..]
            : canonicalKey;
    }

    public static bool IsOneCharacterAlphaToken(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length == 1 && char.IsLetter(value[0]);
    }

    public static bool IsEmailAddress(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return EmailAddressRegex.IsMatch(value);
    }

    private static string StripPossessiveArtifact(string value)
    {
        var match = Regex.Match(
            value,
            @"^(?<base>.+?)\s*['’]s$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return value;

        var baseName = match.Groups["base"].Value.Trim();
        if (baseName.Length < 2)
            return value;

        if (baseName.Contains(' ', StringComparison.Ordinal)
            || baseName.Contains('.', StringComparison.Ordinal)
            || baseName.Contains('-', StringComparison.Ordinal)
            || baseName.All(c => !char.IsLetter(c) || char.IsLower(c)))
        {
            return baseName;
        }

        return value;
    }

    private static bool LooksLikeEmailOrDomainArtifact(string value, KnowledgeEntityType type)
    {
        if (value.Contains('@', StringComparison.Ordinal))
            return true;

        if (value.Contains("..", StringComparison.Ordinal)
            || value.StartsWith('@')
            || value.EndsWith('@')
            || value.StartsWith('.')
            || value.EndsWith('.'))
        {
            return true;
        }

        return (type == KnowledgeEntityType.Company || type == KnowledgeEntityType.Location)
            && value.All(c => !char.IsLetter(c) || char.IsLower(c))
            && CommonTldArtifacts.Contains(value);
    }
}
