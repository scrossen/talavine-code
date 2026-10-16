namespace SwarmCore.Abstractions.Learning;

/// <summary>
/// One user-approved semantic rule distilled from corrections — the "semantic
/// residue" tier of the unified correction-learning pipeline
/// (docs/unified-correction-learning-design-2026-08.md §2.4). Rules are prose
/// injected into the relevant classifier prompt; anything compilable to a
/// deterministic target belongs in phase 3, not here. The per-domain set is
/// deliberately small (<see cref="ILearnedRuleStore.MaxRulesPerDomain"/>) —
/// that is what keeps prompt adherence high.
/// </summary>
public sealed class LearnedRule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Correction domain the rule belongs to (<see cref="CorrectionDomains"/>).</summary>
    public string Domain { get; set; } = "";

    /// <summary>The rule text as approved (possibly user-edited).</summary>
    public string Text { get; set; } = "";

    /// <summary>
    /// Compiled content rules only (phase 3b): a case-insensitive
    /// subject-contains pattern. Null = a semantic rule that lives in the
    /// prompt; set = a deterministic hint the routing derivation applies
    /// without the model — the rule has LEFT the prompt.
    /// </summary>
    public string? SubjectPattern { get; set; }

    /// <summary>Stream the compiled rule files matches into (default Low Priority).</summary>
    public string? TargetStream { get; set; }

    /// <summary>The proposal (HumanTask) that produced this rule — provenance root.</summary>
    public Guid ProposalId { get; set; }

    /// <summary>Correction ids the distiller claimed this rule generalizes.</summary>
    public List<Guid> SourceCorrectionIds { get; set; } = [];

    public DateTimeOffset ApprovedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Store for the approved semantic rules. Saves replace a domain's whole set —
/// the distiller re-emits the merged list each round (rewrite/merge, recency
/// wins), so the set is a document, not an append log.
/// </summary>
public interface ILearnedRuleStore
{
    /// <summary>Hard cap per domain; saves beyond it are truncated, newest first.</summary>
    const int MaxRulesPerDomain = 12;

    Task<IReadOnlyList<LearnedRule>> GetRulesAsync(string domain, CancellationToken ct = default);

    /// <summary>Replace the domain's rule set (capped at <see cref="MaxRulesPerDomain"/>).</summary>
    Task SaveRulesAsync(string domain, IReadOnlyList<LearnedRule> rules, CancellationToken ct = default);
}

/// <summary>
/// Pure matcher for compiled content rules — shared by the routing-hint
/// lookup and tests. First match wins, newest first.
/// </summary>
public static class LearnedContentRuleMatcher
{
    public const string DefaultStream = "Low Priority";

    public static LearnedRule? TryMatch(IReadOnlyList<LearnedRule> rules, string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject) || rules is not { Count: > 0 }) return null;
        return rules
            .Where(r => !string.IsNullOrWhiteSpace(r.SubjectPattern))
            .OrderByDescending(r => r.ApprovedAt)
            .FirstOrDefault(r => subject.Contains(r.SubjectPattern!, StringComparison.OrdinalIgnoreCase));
    }
}
