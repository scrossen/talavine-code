using System.Text.RegularExpressions;

namespace SwarmCore.LlmRouting.Pricing;

/// <summary>
/// Resolves provider/model aliases against trusted pricing rows. The resolver is
/// deliberately conservative: it handles provider-qualified routes, simple
/// normalization, and dated provider versions, but it never invents a price when
/// no explicit priced family exists.
/// </summary>
public static class ModelPricingAliasResolver
{
    private static readonly HashSet<string> ProviderRoutePrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "openai",
        "claude",
        "anthropic",
        "gemini",
        "google",
        "grok",
        "xai",
        "deepseek",
        "openrouter",
        "huggingface",
        "hugging face"
    };

    public static bool TryResolve<TValue>(
        string? modelKey,
        IReadOnlyDictionary<string, TValue> pricing,
        out string matchedKey,
        out TValue value)
    {
        matchedKey = string.Empty;
        value = default!;

        if (string.IsNullOrWhiteSpace(modelKey) || pricing.Count == 0)
        {
            return false;
        }

        var trimmed = modelKey.Trim();
        var normalized = NormalizeLookupKey(trimmed);
        if (pricing.TryGetValue(trimmed, out var directValue))
        {
            value = directValue;
            matchedKey = trimmed;
            return true;
        }

        if (pricing.TryGetValue(normalized, out var normalizedValue))
        {
            value = normalizedValue;
            matchedKey = normalized;
            return true;
        }

        var datedMatch = pricing.Keys
            .Select(key => new { Key = key, Date = TryExtractDatedAlias(normalized, key) })
            .Where(match => match.Date.HasValue)
            .OrderByDescending(match => match.Date!.Value)
            .ThenBy(match => match.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (datedMatch is null)
        {
            return false;
        }

        matchedKey = datedMatch.Key;
        value = pricing[datedMatch.Key]!;
        return true;
    }

    public static string NormalizeLookupKey(string modelKey)
    {
        var normalized = modelKey.Trim();
        var colonIndex = normalized.IndexOf(':');
        if (colonIndex > 0 && ProviderRoutePrefixes.Contains(normalized[..colonIndex]))
        {
            normalized = normalized[(colonIndex + 1)..].Trim();
        }

        normalized = Regex.Replace(normalized, @"-\d{8}$", "", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"-\d{4}-\d{2}-\d{2}$", "", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"-exp$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        normalized = Regex.Replace(
            normalized,
            @"^(gpt-5(?:\.\d+)?)-chat-latest$",
            "$1",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return normalized;
    }

    private static DateTime? TryExtractDatedAlias(string normalizedModelKey, string candidateKey)
    {
        if (string.IsNullOrWhiteSpace(normalizedModelKey)
            || candidateKey.Length <= normalizedModelKey.Length + 1
            || !candidateKey.StartsWith(normalizedModelKey + "-", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var suffix = candidateKey[(normalizedModelKey.Length + 1)..];
        if (Regex.IsMatch(suffix, @"^\d{8}$", RegexOptions.CultureInvariant)
            && DateTime.TryParseExact(
                suffix,
                "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var compactDate))
        {
            return compactDate;
        }

        if (Regex.IsMatch(suffix, @"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant)
            && DateTime.TryParseExact(
                suffix,
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var dashedDate))
        {
            return dashedDate;
        }

        return null;
    }
}
