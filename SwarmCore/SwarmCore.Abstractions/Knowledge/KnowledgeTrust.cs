namespace SwarmCore.Abstractions.Knowledge;

/// <summary>Lifecycle status of a knowledge claim.</summary>
public static class KnowledgeClaimStatuses
{
    public const string Unverified = "unverified";

    /// <summary>
    /// Honestly agent-attributed knowledge (<see cref="KnowledgeSourceAuthorities.Agent"/>)
    /// anchored to that agent's own <see cref="AgentKnowledgeAnchors"/> partition. Promotable
    /// to a context fact, but only recallable inside that agent's own context packs — never
    /// treated as user-confirmed truth.
    /// </summary>
    public const string AgentObserved = "agent-observed";

    public const string Trusted = "trusted";
    public const string Confirmed = "confirmed";
    public const string Corroborated = "corroborated";
    public const string Contradicted = "contradicted";
    public const string Stale = "stale";
    public const string Suppressed = "suppressed";
}

/// <summary>Who vouches for a claim. Rank order is enforced by <see cref="KnowledgeTrustPolicy"/>.</summary>
public static class KnowledgeSourceAuthorities
{
    public const string UserAuthored = "source:user-authored";
    public const string TrustedContact = "source:trusted-contact";
    public const string ThirdParty = "source:third-party";
    public const string Agent = "source:agent";
}

/// <summary>One piece of evidence behind a claim.</summary>
public sealed record KnowledgeEvidence(
    string SourceAuthority,
    string SourceKind,
    string SourceTitle,
    Guid? SourceAccountId,
    Guid? SourceMessageId,
    string? SourceRecordId,
    string? SourceUri,
    string? Excerpt,
    DateTimeOffset ObservedAt);

/// <summary>
/// The facts about a claim that the trust decision depends on. A struct of inputs rather than
/// an entity, so the SAME policy serves the desktop's sync-stamped <c>KnowledgeClaim</c> row
/// and the hosted CRM's tenant-scoped equivalent without either host's storage shape leaking
/// into the rule.
/// </summary>
/// <param name="SourceAuthority">One of <see cref="KnowledgeSourceAuthorities"/>.</param>
/// <param name="SourceAccountId">Provenance partition — an agent anchor, a real account, or null.</param>
public readonly record struct KnowledgeClaimFacts(
    string SourceAuthority,
    Guid? SourceAccountId,
    bool IsSuppressed = false,
    bool HasContradiction = false,
    int IndependentSourceCount = 1,
    DateTimeOffset? StaleAfter = null);

/// <summary>
/// SINGLE SOURCE OF TRUTH for what an agent's knowledge is worth.
///
/// <para>
/// THE RULE: an agent may record what it learned, and that record may serve THAT AGENT'S own
/// future work — but it is never evidence about the world. Agent authority ranks below a
/// third-party's contact and far below the user's own words; it can reach
/// <see cref="KnowledgeClaimStatuses.AgentObserved"/> and no higher; and it earns even that
/// only when stamped into the agent's own <see cref="AgentKnowledgeAnchors"/> partition, which
/// is what keeps it out of the owner's default context packs. An agent must not be able to
/// teach its owner's assistant "facts".
/// </para>
///
/// <para>
/// This policy exists because the rule was previously re-derived independently in three places
/// (claim minting, claim persistence, and fact promotion). Three copies of a security rule is
/// three chances for one of them to drift; a hosted second host would have made it five.
/// </para>
/// </summary>
public static class KnowledgeTrustPolicy
{
    /// <summary>Authority precedence. Higher wins when merging evidence; Agent sits above only anonymous third parties.</summary>
    public static int AuthorityRank(string? sourceAuthority) => sourceAuthority switch
    {
        KnowledgeSourceAuthorities.UserAuthored => 3,
        KnowledgeSourceAuthorities.TrustedContact => 2,
        KnowledgeSourceAuthorities.Agent => 1,
        _ => 0,
    };

    /// <summary>The stronger of two authorities — merging evidence must never escalate trust.</summary>
    public static string BestAuthority(string left, string right) =>
        AuthorityRank(right) > AuthorityRank(left) ? right : left;

    /// <summary>
    /// True when this claim is agent-attributed AND correctly confined to an agent partition.
    /// Agent authority pointing at a REAL account is honest attribution without promotion —
    /// it stays unverified, because otherwise an agent could write into a human's provenance.
    /// </summary>
    public static bool IsAnchoredAgentClaim(in KnowledgeClaimFacts facts) =>
        facts.SourceAuthority == KnowledgeSourceAuthorities.Agent
        && facts.SourceAccountId is { } accountId
        && AgentKnowledgeAnchors.IsAgentAnchor(accountId);

    /// <summary>
    /// The status a freshly minted claim deserves, before any corroboration history exists.
    /// </summary>
    public static string InitialStatus(in KnowledgeClaimFacts facts)
    {
        if (facts.SourceAuthority == KnowledgeSourceAuthorities.UserAuthored)
            return KnowledgeClaimStatuses.Confirmed;
        if (facts.SourceAuthority == KnowledgeSourceAuthorities.TrustedContact)
            return KnowledgeClaimStatuses.Trusted;
        if (IsAnchoredAgentClaim(facts))
            return KnowledgeClaimStatuses.AgentObserved;
        return KnowledgeClaimStatuses.Unverified;
    }

    /// <summary>
    /// The status a stored claim deserves given its full history. Re-derived on every upsert so
    /// a claim cannot keep a status its evidence no longer supports.
    /// </summary>
    public static string DetermineStatus(in KnowledgeClaimFacts facts, DateTimeOffset nowUtc)
    {
        if (facts.IsSuppressed) return KnowledgeClaimStatuses.Suppressed;
        if (facts.HasContradiction) return KnowledgeClaimStatuses.Contradicted;
        if (facts.StaleAfter is not null && facts.StaleAfter <= nowUtc) return KnowledgeClaimStatuses.Stale;
        if (facts.SourceAuthority == KnowledgeSourceAuthorities.UserAuthored) return KnowledgeClaimStatuses.Confirmed;
        if (facts.IndependentSourceCount >= 2) return KnowledgeClaimStatuses.Corroborated;
        if (facts.SourceAuthority == KnowledgeSourceAuthorities.TrustedContact) return KnowledgeClaimStatuses.Trusted;
        if (IsAnchoredAgentClaim(facts)) return KnowledgeClaimStatuses.AgentObserved;
        return KnowledgeClaimStatuses.Unverified;
    }

    /// <summary>
    /// Whether a claim at <paramref name="status"/> may become a recallable context fact.
    /// Agent-observed claims promote ONLY when anchored: the resulting fact carries the anchor,
    /// so it is invisible to the owner's default packs and recallable only where allowlisted.
    /// </summary>
    public static bool ShouldPromote(in KnowledgeClaimFacts facts, string status, DateTimeOffset nowUtc)
    {
        if (facts.IsSuppressed || facts.HasContradiction) return false;
        if (facts.StaleAfter is not null && facts.StaleAfter <= nowUtc) return false;

        if (status is KnowledgeClaimStatuses.Confirmed
            or KnowledgeClaimStatuses.Trusted
            or KnowledgeClaimStatuses.Corroborated)
            return true;

        return status == KnowledgeClaimStatuses.AgentObserved && IsAnchoredAgentClaim(facts);
    }

    /// <summary>
    /// Whether a fact from <paramref name="sourceAccountId"/> may appear in a context pack whose
    /// allowlist is <paramref name="allowedSourceAccountIds"/> (null = the owner's default pack).
    ///
    /// <para>
    /// THE READ-SIDE HALF of the partition, and the one that matters most: a default pack must
    /// never carry agent-anchored knowledge, or a story-research "fact" an agent concluded
    /// surfaces as grounded truth when the owner asks their own assistant. Hosts must apply this
    /// in the candidate QUERY, not only as a post-filter — agent facts can carry high confidence
    /// and would otherwise starve a confidence-ordered candidate window before filtering runs.
    /// </para>
    /// </summary>
    public static bool IsRecallable(Guid? sourceAccountId, IReadOnlyCollection<Guid>? allowedSourceAccountIds)
    {
        if (sourceAccountId is not { } accountId || !AgentKnowledgeAnchors.IsAgentAnchor(accountId))
            return true;

        return allowedSourceAccountIds is { Count: > 0 } && allowedSourceAccountIds.Contains(accountId);
    }
}
