using System.Security.Cryptography;
using System.Text;

namespace SwarmCore.Knowledge;

public readonly record struct KnowledgeGraphParticipant(Guid Id, KnowledgeEntityType Type);

public readonly record struct KnowledgeEntityPair(Guid SubjectId, Guid ObjectId);

public sealed record KnowledgeNeighborhoodHashEntity(
    string Name,
    KnowledgeEntityType Type,
    string CanonicalKey,
    string? Description,
    int MentionCount);

public sealed record KnowledgeNeighborhoodHashEdge(
    string NeighborCanonicalKey,
    KnowledgeRelationType RelationType,
    string? Predicate,
    int MentionCount);

/// <summary>
/// Source-neutral graph-shape algorithms shared by persistence hosts.
/// </summary>
public static class KnowledgeGraphAlgorithms
{
    public const int MaxCorrespondenceCliqueParticipants = 12;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static IReadOnlyList<KnowledgeEntityPair> EnumerateCorrespondencePairs(
        IEnumerable<KnowledgeGraphParticipant> participants)
    {
        ArgumentNullException.ThrowIfNull(participants);
        var validated = participants.ToList();
        foreach (var participant in validated)
        {
            if (participant.Id == Guid.Empty)
                throw new ArgumentException("Knowledge graph participant IDs cannot be empty.", nameof(participants));
            KnowledgeContractValidation.RequireDefined(participant.Type, nameof(participants));
        }

        var people = validated
            .Where(static participant => participant.Type == KnowledgeEntityType.Person)
            .DistinctBy(static participant => participant.Id)
            .ToList();
        var pairs = new List<KnowledgeEntityPair>();

        if (people.Count <= MaxCorrespondenceCliqueParticipants)
        {
            for (var i = 0; i < people.Count; i++)
            {
                for (var j = i + 1; j < people.Count; j++)
                    pairs.Add(NormalizeUndirected(people[i].Id, people[j].Id));
            }

            return pairs;
        }

        // Input order is meaningful: communication ingest places the sender
        // first. Large rosters become a sender-centered star to avoid O(n²)
        // graph growth while retaining every participant.
        var center = people[0].Id;
        foreach (var participant in people.Skip(1))
            pairs.Add(NormalizeUndirected(center, participant.Id));
        return pairs;
    }

    /// <summary>
    /// Preserves the desktop version-1 neighborhood hash so extraction does
    /// not trigger a costly full semantic-summary rebuild. A future encoding
    /// change must be introduced as a versioned, deliberate reindex operation.
    /// </summary>
    public static string ComputeNeighborhoodHashV1(
        KnowledgeNeighborhoodHashEntity entity,
        IEnumerable<KnowledgeNeighborhoodHashEdge> neighborhood)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(neighborhood);
        KnowledgeContractValidation.RequireDefined(entity.Type, nameof(entity));
        if (entity.MentionCount < 0)
            throw new ArgumentOutOfRangeException(nameof(entity), "Entity mention count cannot be negative.");

        var validatedNeighborhood = neighborhood.ToList();
        foreach (var edge in validatedNeighborhood)
        {
            if (edge is null)
                throw new ArgumentException("Knowledge neighborhoods cannot contain null edges.", nameof(neighborhood));
            KnowledgeContractValidation.RequireDefined(edge.RelationType, nameof(neighborhood));
            if (edge.MentionCount < 0)
                throw new ArgumentOutOfRangeException(nameof(neighborhood), "Relation mention count cannot be negative.");
        }

        var builder = new StringBuilder();
        builder.Append(entity.Name)
            .Append('|').Append(entity.Type)
            .Append('|').Append(entity.CanonicalKey)
            .Append('|').Append(entity.Description)
            .Append('|').Append(KnowledgeMentionMilestones.GetBucket(entity.MentionCount))
            .Append(';');

        var sorted = validatedNeighborhood
            .Select(edge => $"{edge.NeighborCanonicalKey}:{edge.RelationType}:{edge.Predicate}:{KnowledgeMentionMilestones.GetBucket(edge.MentionCount)}")
            .OrderBy(static value => value, StringComparer.Ordinal);
        foreach (var entry in sorted)
            builder.Append(entry).Append(';');

        try
        {
            var hash = SHA256.HashData(StrictUtf8.GetBytes(builder.ToString()));
            return Convert.ToHexString(hash, 0, 16);
        }
        catch (EncoderFallbackException ex)
        {
            throw new ArgumentException(
                "Knowledge neighborhood fields must be valid UTF-16.",
                nameof(entity),
                ex);
        }
    }

    private static KnowledgeEntityPair NormalizeUndirected(Guid a, Guid b)
        => a.CompareTo(b) <= 0
            ? new KnowledgeEntityPair(a, b)
            : new KnowledgeEntityPair(b, a);
}
