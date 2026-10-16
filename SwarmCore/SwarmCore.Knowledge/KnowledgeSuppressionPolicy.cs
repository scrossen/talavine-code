using System.Text;
using System.Text.RegularExpressions;

namespace SwarmCore.Knowledge;

/// <summary>A forgotten statement remains forgotten when an extractor changes its key or category.</summary>
public static class KnowledgeSuppressionPolicy
{
    public static bool Matches(string key, string text, string? category, string? source,
        string ruleKey, string? textPattern, string? categoryPattern, string? sourcePattern,
        bool matchIdenticalTextAcrossKeys = false)
    {
        if (!Contains(source, sourcePattern))
            return false;
        var sameStatement = !string.IsNullOrWhiteSpace(textPattern)
            && string.Equals(Normalize(text), Normalize(textPattern), StringComparison.OrdinalIgnoreCase);
        if (matchIdenticalTextAcrossKeys && sameStatement)
            return true;
        return (ruleKey == "*" || string.Equals(key, ruleKey, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(categoryPattern) || string.Equals(category, categoryPattern, StringComparison.OrdinalIgnoreCase))
            && Contains(text, textPattern);
    }

    private static bool Contains(string? value, string? pattern)
        => string.IsNullOrWhiteSpace(pattern) || (value?.Contains(pattern, StringComparison.OrdinalIgnoreCase) ?? false);

    private static string Normalize(string value)
        => Regex.Replace(value.Normalize(NormalizationForm.FormC).Trim(), @"\s+", " ");
}
