using System.Security.Cryptography;
using System.Text;

namespace SwarmCore.Knowledge;

/// <summary>
/// Shared contract for an agent's private continuity graph. These machine-generated facts are
/// never organizational truth, never approved implicitly, and never escape their crm-agent partition.
/// </summary>
public static class AgentPrivateKnowledge
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public const string PartitionKind = "crm-agent";
    public const double MaximumConfidence = 0.85;
    public const int RecallMaximumChars = 2_400;

    public static KnowledgePartition CreatePartition(string hostAgentStableId)
        => KnowledgePartition.Create(PartitionKind, hostAgentStableId);

    public static KnowledgeArtifact CreateFact(
        KnowledgePartition partition,
        string kind,
        string text,
        DateTimeOffset observedAt,
        string? normalizedKey = null,
        string? title = null,
        string? runReference = null,
        double confidence = 0.6,
        DateTimeOffset? createdAt = null)
    {
        ArgumentNullException.ThrowIfNull(partition);
        if (!string.Equals(partition.Kind, PartitionKind, StringComparison.Ordinal))
            throw new ArgumentException("Agent memory requires a crm-agent partition.", nameof(partition));
        var statement = Required(text, KnowledgeContractLimits.ArtifactStatementLength, nameof(text));
        var normalizedKind = Required(kind, KnowledgeContractLimits.ArtifactDomainLength, nameof(kind)).ToLowerInvariant();
        var normalizedTitle = Required(
            string.IsNullOrWhiteSpace(title) ? normalizedKind : title,
            KnowledgeContractLimits.ArtifactTitleLength,
            nameof(title));
        var key = string.IsNullOrWhiteSpace(normalizedKey)
            ? $"memory:{Convert.ToHexStringLower(SHA256.HashData(StrictUtf8.GetBytes(statement.ToLowerInvariant())).AsSpan(0, 16))}"
            : Required(normalizedKey, KnowledgeContractLimits.ArtifactCanonicalKeyLength, nameof(normalizedKey)).ToLowerInvariant();
        var domain = string.IsNullOrWhiteSpace(runReference)
            ? $"agent-memory:{normalizedKind}"
            : $"agent-memory:{normalizedKind}:{runReference.Trim()}";
        if (domain.Length > KnowledgeContractLimits.ArtifactDomainLength)
            domain = domain[..KnowledgeContractLimits.ArtifactDomainLength];

        return KnowledgeArtifactNormalizer.Normalize(new KnowledgeArtifact
        {
            Partition = partition,
            Kind = KnowledgeArtifactKind.Fact,
            CanonicalKey = key,
            Title = normalizedTitle,
            Statement = statement,
            Domain = domain,
            Lifecycle = KnowledgeArtifactLifecycle.Active,
            Confidence = Math.Clamp(confidence, 0.05, MaximumConfidence),
            IsMachineGenerated = true,
            EffectiveAt = observedAt,
            CreatedAt = createdAt ?? observedAt,
            UpdatedAt = createdAt.HasValue ? observedAt : null,
        });
    }

    public static string RenderRecall(IReadOnlyList<KnowledgeArtifact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Count == 0) return string.Empty;
        var lines = new List<string>
        {
            "## What you have learned so far",
            "Private machine-generated continuity notes from your prior cycles. Treat them as fallible recollections, not organizational truth or authority.",
        };
        var used = lines.Sum(line => line.Length + 1);
        foreach (var fact in facts
                     .Where(fact => fact.Kind == KnowledgeArtifactKind.Fact && fact.IsMachineGenerated)
                     .OrderByDescending(fact => fact.Confidence)
                     .ThenByDescending(fact => fact.EffectiveAt ?? fact.CreatedAt))
        {
            var line = $"- [{(fact.EffectiveAt ?? fact.CreatedAt):MMM d}] {fact.Statement}";
            if (used + line.Length + 1 > RecallMaximumChars) break;
            lines.Add(line);
            used += line.Length + 1;
        }
        return lines.Count > 2 ? string.Join('\n', lines) : string.Empty;
    }

    private static string Required(string? value, int maximum, string parameterName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("A value is required.", parameterName);
        try { _ = StrictUtf8.GetByteCount(normalized); }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("The value contains malformed Unicode.", parameterName, exception);
        }
        if (normalized.Length > maximum)
            throw new ArgumentException($"The value cannot exceed {maximum} characters.", parameterName);
        return normalized;
    }
}
