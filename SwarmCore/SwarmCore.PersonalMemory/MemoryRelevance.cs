namespace SwarmCore.PersonalMemory;

public sealed record MemorySearchHit(MemoryFact Fact, double Score);

/// <summary>
/// Lexical relevance retrieval over facts, IDF-weighted against the candidate pool (the same
/// mechanism Talavine's RelevantUserMemorySelector proved on live data: terms present in a
/// large share of the pool — the person's own name, company — carry no selective power).
/// Deterministic, embedding-free, and total-ordered so identical inputs render identical prompts.
/// Hosts with a vector index may union their semantic hits before calling <see cref="Rank"/>.
/// </summary>
public static class MemoryRelevanceRanker
{
    private const double UbiquitousShare = 0.25;

    public static IReadOnlyList<MemorySearchHit> Rank(
        IReadOnlyList<MemoryFact> pool,
        string? query,
        int limit,
        double minimumScore)
    {
        if (pool.Count == 0 || string.IsNullOrWhiteSpace(query) || limit <= 0) return [];
        var queryTerms = Terms(query);
        if (queryTerms.Count == 0) return [];

        var documentTerms = pool.ToDictionary(f => f.Id, f => Terms(f.Text + " " + f.Key.Replace(':', ' ').Replace('-', ' ')));
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var terms in documentTerms.Values)
            foreach (var term in terms)
                df[term] = df.GetValueOrDefault(term) + 1;

        var hits = new List<MemorySearchHit>();
        foreach (var fact in pool)
        {
            var score = 0.0;
            foreach (var term in documentTerms[fact.Id])
            {
                if (!queryTerms.Contains(term)) continue;
                var frequency = df[term];
                if (pool.Count >= 8 && (double)frequency / pool.Count > UbiquitousShare) continue;
                score += Math.Log(1.0 + (double)pool.Count / frequency) + (term.Length >= 6 ? 0.5 : 0);
            }
            if (score >= minimumScore) hits.Add(new MemorySearchHit(fact, score));
        }

        return hits
            .OrderByDescending(h => h.Score)
            .ThenByDescending(h => h.Fact.UserConfirmed)
            .ThenByDescending(h => h.Fact.Confidence)
            .ThenBy(h => h.Fact.Id)
            .Take(limit)
            .ToList();
    }

    /// <summary>True when the two texts share at least one distinctive content word (stemmed, stop words removed).</summary>
    public static bool SharesContentTerm(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        var terms = Terms(left);
        terms.ExceptWith(GenericTerms);
        return terms.Overlaps(Terms(right));
    }

    /// <summary>Distinctive content terms of a statement (stemmed; stop and generic words removed).</summary>
    public static IReadOnlySet<string> DistinctiveTerms(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new HashSet<string>(StringComparer.Ordinal);
        var terms = Terms(text);
        terms.ExceptWith(GenericTerms);
        return terms;
    }

    // Words too generic to relate two statements about a person ("prefers", "likes", "works" are kept:
    // "no longer works at X" must relate to "works at X" through X anyway).
    private static readonly HashSet<string> GenericTerms = new(StringComparer.Ordinal)
    {
        "prefer", "prefers", "lives", "live", "currently", "always", "never", "really", "very", "much", "more", "less",
        "one", "two", "time", "day", "week", "year", "thing", "things", "new", "old", "good", "great",
    };

    private static HashSet<string> Terms(string text)
        => MemoryUserSupport.Tokenize(text)
            .Where(t => t.Length >= 3 && !StopWords.Contains(t))
            .Select(MemoryUserSupport.Stem)
            .ToHashSet(StringComparer.Ordinal);

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "that", "this", "are", "was", "were", "has", "have", "had", "not",
        "you", "your", "user", "users", "they", "them", "their", "his", "her", "she", "him", "its",
        "about", "what", "when", "where", "which", "who", "why", "how", "does", "did", "can", "could",
        "would", "should", "will", "into", "from", "know", "tell", "please", "remember", "any", "some",
        "also", "just", "like", "auto", "other",
    };
}
