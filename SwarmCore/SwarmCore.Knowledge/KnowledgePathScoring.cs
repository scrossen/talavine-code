namespace SwarmCore.Knowledge;

/// <summary>Pure ranking policy for bounded evidence-backed graph paths.</summary>
public static class KnowledgePathScoring
{
    public static double ScorePath(
        IReadOnlyList<KnowledgePathHopEvidence> hops,
        IReadOnlyList<int> interiorNodeDegrees)
    {
        ArgumentNullException.ThrowIfNull(hops);
        ArgumentNullException.ThrowIfNull(interiorNodeDegrees);
        if (interiorNodeDegrees.Count != Math.Max(0, hops.Count - 1))
            throw new ArgumentException("One degree is required for every interior path node.", nameof(interiorNodeDegrees));

        var score = 1.0;
        foreach (var hop in hops)
            score *= ScoreHop(hop);
        foreach (var degree in interiorNodeDegrees)
            score *= TransitPenalty(degree);

        return score * Math.Pow(0.65, Math.Max(0, hops.Count - 1));
    }

    public static double ScoreHop(KnowledgePathHopEvidence hop)
    {
        var typeWeight = hop.RelationType switch
        {
            KnowledgeRelationType.MentionsWith => 0.15,
            KnowledgeRelationType.WorksAt => 0.80,
            KnowledgeRelationType.Discusses => 0.60,
            KnowledgeRelationType.CorrespondsWith => 0.50,
            _ => string.IsNullOrWhiteSpace(hop.Predicate) ? 0.40 : 1.00
        };

        if (!string.IsNullOrWhiteSpace(hop.Predicate)
            && hop.RelationType != KnowledgeRelationType.MentionsWith)
        {
            typeWeight = Math.Max(typeWeight, 1.00);
        }

        var corroboration = 1.0 + (Math.Log(1 + Math.Max(0, hop.MentionCount)) / 4.0);
        var strength = 0.75 + (0.5 * Math.Clamp(hop.Strength, 0, 1));
        var evidence = hop.HasSourceEvidence ? 1.0 : 0.5;
        return typeWeight * corroboration * strength * evidence;
    }

    public static double TransitPenalty(int degree)
        => degree <= 10 ? 1.0 : 1.0 / (1.0 + Math.Log10(degree / 10.0));
}

public readonly record struct KnowledgePathHopEvidence(
    KnowledgeRelationType RelationType,
    string? Predicate,
    int MentionCount,
    double Strength,
    bool HasSourceEvidence);
