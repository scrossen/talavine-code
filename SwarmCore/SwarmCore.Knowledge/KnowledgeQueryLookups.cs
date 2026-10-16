namespace SwarmCore.Knowledge;

/// <summary>Bounded lexical probes that retain names inside natural-language questions.</summary>
public static class KnowledgeQueryLookups
{
    public static string NormalizeTerm(string term)
    {
        var normalized = term.Trim('.', '\'', '’', '-');
        return normalized.EndsWith("'s", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("’s", StringComparison.OrdinalIgnoreCase)
            ? normalized[..^2] : normalized;
    }

    public static IReadOnlyList<string> Create(IReadOnlyList<string> terms, int limit)
    {
        var output = new List<string>();
        // Reserve room for individual names instead of allowing adjacent
        // phrases to exhaust the entire budget in a long question.
        for (var i = 0; i < terms.Count && output.Count < limit; i++)
        {
            Add(terms[i]);
            if (i + 1 < terms.Count) Add(terms[i] + " " + terms[i + 1]);
        }
        return output;

        void Add(string text)
        {
            if (output.Count < limit && !output.Contains(text, StringComparer.OrdinalIgnoreCase))
                output.Add(text);
        }
    }
}
