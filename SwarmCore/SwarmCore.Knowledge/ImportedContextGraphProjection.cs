namespace SwarmCore.Knowledge;

/// <summary>Admission for archive mentions, never assertions about the archive owner.</summary>
public static class ImportedContextGraphProjection
{
    public const string SourceKey = "imported-context:source:v1";
    public const int WindowCharacters = 8_000;

    public static IReadOnlyList<KnowledgeEntityContribution> Admit(
        string excerpt, IEnumerable<ImportedContextMention> candidates)
    {
        var admitted = new Dictionary<string, KnowledgeEntityContribution>(StringComparer.Ordinal);
        foreach (var candidate in candidates.Take(50))
        {
            var name = candidate.Name?.Trim();
            var quote = candidate.Quote?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 120 || name.Any(char.IsControl)
                || string.IsNullOrEmpty(quote) || quote.Length > 800
                || !excerpt.Contains(quote, StringComparison.Ordinal)
                || !quote.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;
            var type = candidate.Type?.ToLowerInvariant() switch
            {
                "person" => KnowledgeEntityType.Person,
                "company" => KnowledgeEntityType.Company,
                "project" => KnowledgeEntityType.Project,
                "location" => KnowledgeEntityType.Location,
                _ => KnowledgeEntityType.Topic
            };
            var key = $"imported-mention:{(int)type}:{name.ToLowerInvariant()}";
            admitted.TryAdd(key, new(type, key, name,
                "Mentioned in imported reference material; not a confirmed fact about you."));
            if (admitted.Count == 25) break;
        }
        return admitted.Values.ToArray();
    }
}

public sealed record ImportedContextMention(string? Name, string? Type, string? Quote);
