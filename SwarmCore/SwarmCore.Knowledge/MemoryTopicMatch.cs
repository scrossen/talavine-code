using System.Text;
using System.Text.RegularExpressions;

namespace SwarmCore.Knowledge;

/// <summary>Literal topical evidence matching shared by memory queries and context admission.</summary>
public static class MemoryTopicMatch
{
    public static int Count(string? text, IReadOnlyCollection<string> terms)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        text = text.Normalize(NormalizationForm.FormC);
        var score = 0;
        foreach (var raw in terms)
        {
            var term = raw.Normalize(NormalizationForm.FormC);
            if (string.IsNullOrWhiteSpace(term)) continue;
            var matches = term.Length <= 4 && !term.Any(char.IsWhiteSpace)
                ? Regex.IsMatch(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(term)}(?![\p{{L}}\p{{N}}])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                : text.Contains(term, StringComparison.OrdinalIgnoreCase);
            if (matches && ++score == 3) break;
        }
        return score;
    }
}
