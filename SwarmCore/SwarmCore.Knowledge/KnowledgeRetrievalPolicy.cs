using System.Text.RegularExpressions;

namespace SwarmCore.Knowledge;

/// <summary>
/// Lightweight topic fallback for communications that have not yet received
/// model extraction. It is intentionally conservative and does not replace an
/// extractor.
/// </summary>
public static class KnowledgeFallbackTopicExtractor
{
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "but", "of", "in", "on", "at", "to", "for", "with",
        "by", "from", "is", "was", "are", "were", "be", "been", "being", "this", "that",
        "these", "those", "it", "its", "as", "if", "so", "not", "no", "yes",
        "please", "hi", "hello", "hey", "thanks", "thank", "regards", "cheers",
        "update", "updates", "updated", "question", "questions", "answer", "answers",
        "reply", "replies", "request", "requests", "action", "required",
        "quick", "simple", "new", "old", "re", "fwd", "fw",
        "review", "meeting", "call", "discussion", "notes", "minutes"
    };

    public static IReadOnlyList<string> DeriveFromSubject(string? subject)
    {
        var topics = new List<string>();
        if (string.IsNullOrWhiteSpace(subject))
            return topics;

        var cleaned = Regex.Replace(
            subject,
            @"^\s*(?:re|fwd?|aw|antwort|sv|vs|ref)\s*[:\]]+\s*",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        cleaned = Regex.Replace(cleaned, @"\[[^\]]*\]", " ").Trim();
        if (cleaned.Length is < 4 or > 120)
            return topics;

        var fragments = cleaned
            .Split(['-', '—', '–', '|', ':', '/', '>'], StringSplitOptions.RemoveEmptyEntries)
            .Select(static fragment => fragment.Trim())
            .Where(static fragment => fragment.Length is >= 4 and <= 60);
        foreach (var fragment in fragments)
        {
            var words = fragment.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(static word => word.Length >= 2 && word.Any(char.IsLetter))
                .Where(word => !Stopwords.Contains(word))
                .ToList();
            if (words.Count is < 2 or > 5 || !words.Any(static word => char.IsUpper(word[0])))
                continue;

            var phrase = string.Join(' ', words);
            if (!topics.Contains(phrase, StringComparer.OrdinalIgnoreCase))
                topics.Add(phrase);
        }

        return topics;
    }
}

/// <summary>Shared lexical and semantic score boosts for graph retrieval.</summary>
public static class KnowledgeSearchScoring
{
    public static double ScoreLexical(
        string entityName,
        string canonicalKey,
        string? query,
        int mentionCount)
    {
        ArgumentNullException.ThrowIfNull(entityName);
        ArgumentNullException.ThrowIfNull(canonicalKey);
        ValidateMentionCount(mentionCount);

        var normalizedQuery = (query ?? string.Empty).Trim();
        var exactName = string.Equals(entityName, normalizedQuery, StringComparison.OrdinalIgnoreCase);
        var exactKey = string.Equals(canonicalKey, normalizedQuery, StringComparison.OrdinalIgnoreCase);
        var startsName = entityName.StartsWith(normalizedQuery, StringComparison.OrdinalIgnoreCase);
        var containsName = entityName.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase);
        var containsKey = canonicalKey.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase);
        var lexicalScore = exactName || exactKey
            ? 0.98
            : startsName
                ? 0.9
                : containsName || containsKey
                    ? 0.82
                    : 0.65;
        return Math.Min(1.0, lexicalScore + MentionBoost(mentionCount, 0.03));
    }

    public static double ScoreSemantic(double relevance, int mentionCount)
    {
        if (!double.IsFinite(relevance))
            throw new ArgumentOutOfRangeException(nameof(relevance), "Relevance must be finite.");
        ValidateMentionCount(mentionCount);
        return (relevance * 0.85) + MentionBoost(mentionCount, 0.05);
    }

    private static double MentionBoost(int mentionCount, double weight)
        => Math.Log10(Math.Max(1, mentionCount)) * weight;

    private static void ValidateMentionCount(int mentionCount)
    {
        if (mentionCount < 0)
            throw new ArgumentOutOfRangeException(nameof(mentionCount), "Mention count cannot be negative.");
    }
}

/// <summary>
/// Produces bounded, culture-invariant terms for host search adapters that do
/// not provide semantic retrieval. The terms are deliberately source-neutral
/// so chat, desktop, and service consumers do not grow different token rules.
/// </summary>
public static partial class KnowledgeQueryTerms
{
    private const int MaximumTerms = 8;
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "a", "about", "an", "and", "are", "as", "at", "be", "by", "did", "do", "does",
        "for", "from", "how", "i", "in", "is", "it", "of", "on", "or", "our", "the",
        "this", "to", "was", "we", "were", "what", "when", "where", "which", "who", "why",
        "with", "you", "your"
    };

    public static IReadOnlyList<string> Extract(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        return TermPattern().Matches(query)
            .Select(match => match.Value.ToLowerInvariant())
            .Where(term => !Stopwords.Contains(term))
            .Distinct(StringComparer.Ordinal)
            .Take(MaximumTerms)
            .ToArray();
    }

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}_-]{1,39}", RegexOptions.CultureInvariant)]
    private static partial Regex TermPattern();
}
