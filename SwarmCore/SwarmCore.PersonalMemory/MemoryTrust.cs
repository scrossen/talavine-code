using SwarmCore.Abstractions.Knowledge;

namespace SwarmCore.PersonalMemory;

/// <summary>
/// Trust rules for personal memory. Delegates authority ranking to the shared
/// <see cref="KnowledgeTrustPolicy"/> so Talavine claims, CRM claims and personal memory
/// use one rule.
/// </summary>
public static class MemoryTrust
{
    public static string ToKnowledgeAuthority(MemorySourceAuthority authority) => authority switch
    {
        MemorySourceAuthority.UserAuthored => KnowledgeSourceAuthorities.UserAuthored,
        MemorySourceAuthority.TrustedContact => KnowledgeSourceAuthorities.TrustedContact,
        MemorySourceAuthority.Agent => KnowledgeSourceAuthorities.Agent,
        _ => KnowledgeSourceAuthorities.ThirdParty,
    };

    public static MemorySourceAuthority FromKnowledgeAuthority(string? authority) => authority switch
    {
        KnowledgeSourceAuthorities.UserAuthored => MemorySourceAuthority.UserAuthored,
        KnowledgeSourceAuthorities.TrustedContact => MemorySourceAuthority.TrustedContact,
        KnowledgeSourceAuthorities.Agent => MemorySourceAuthority.Agent,
        _ => MemorySourceAuthority.ThirdParty,
    };

    public static int Rank(MemorySourceAuthority authority)
        => KnowledgeTrustPolicy.AuthorityRank(ToKnowledgeAuthority(authority));

    public static MemorySourceAuthority Best(MemorySourceAuthority left, MemorySourceAuthority right)
        => Rank(right) > Rank(left) ? right : left;

    /// <summary>
    /// Whether a claim (with the given independent-origin count) may become a fact without
    /// the person's confirmation. Third-party text needs two independent origins; agent text
    /// is only admitted as a conversation summary and never anywhere else.
    /// </summary>
    public static bool IsPromotable(MemorySourceAuthority authority, MemoryCategory category, MemoryCaptureMethod method, int independentSources)
    {
        if (category == MemoryCategory.ConversationSummary)
            return method == MemoryCaptureMethod.ConversationSummary;
        if (method is MemoryCaptureMethod.ConversationSummary or MemoryCaptureMethod.AgentObservation)
            return false;

        var status = KnowledgeTrustPolicy.DetermineStatus(
            new KnowledgeClaimFacts(ToKnowledgeAuthority(authority), SourceAccountId: null, IndependentSourceCount: independentSources),
            DateTimeOffset.UtcNow);
        return KnowledgeTrustPolicy.ShouldPromote(
            new KnowledgeClaimFacts(ToKnowledgeAuthority(authority), SourceAccountId: null, IndependentSourceCount: independentSources),
            status,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// True when the fact is the person's own: user-authored or user-confirmed. Such facts are
    /// never deleted by consolidation and can be changed only by the person's own words.
    /// </summary>
    public static bool IsUserOwned(MemoryFact fact)
        => fact.Authority == MemorySourceAuthority.UserAuthored || fact.UserConfirmed;

    /// <summary>
    /// Whether evidence with <paramref name="incoming"/> authority may update/supersede/expire
    /// <paramref name="existing"/>. User-owned facts require user-authored evidence; otherwise
    /// the incoming authority must be at least the fact's. Agent text never changes facts other
    /// than conversation summaries.
    /// </summary>
    public static bool CanChange(MemoryFact existing, MemorySourceAuthority incoming, MemoryCaptureMethod method)
    {
        if (method is MemoryCaptureMethod.AgentObservation) return false;
        if (method is MemoryCaptureMethod.ConversationSummary)
            return existing.Category == MemoryCategory.ConversationSummary && !IsUserOwned(existing);
        if (existing.Category == MemoryCategory.ConversationSummary && incoming != MemorySourceAuthority.UserAuthored)
            return false;
        if (IsUserOwned(existing)) return incoming == MemorySourceAuthority.UserAuthored;
        return Rank(incoming) >= Rank(existing.Authority);
    }

    /// <summary>Default staleness horizon by category (mirrors Talavine's claim defaults).</summary>
    public static DateTimeOffset? DefaultStaleAfter(MemoryCategory category, DateTimeOffset observedAt) => category switch
    {
        MemoryCategory.Identity or MemoryCategory.Relationship or MemoryCategory.Project => observedAt.AddDays(365),
        MemoryCategory.Interest or MemoryCategory.Preference or MemoryCategory.Routine => observedAt.AddDays(540),
        MemoryCategory.ConversationSummary => observedAt.AddDays(45),
        MemoryCategory.Plan => null,
        MemoryCategory.ResponsePreference => null,
        _ => observedAt.AddDays(365),
    };

    public static MemoryDigestSection SectionFor(MemoryCategory category) => category switch
    {
        MemoryCategory.ResponsePreference => MemoryDigestSection.ResponsePreferences,
        MemoryCategory.Project or MemoryCategory.Plan => MemoryDigestSection.ProjectsAndPlans,
        MemoryCategory.ConversationSummary => MemoryDigestSection.RecentConversations,
        _ => MemoryDigestSection.Identity,
    };

    public static string Label(MemorySourceAuthority authority) => authority switch
    {
        MemorySourceAuthority.UserAuthored => "user-stated",
        MemorySourceAuthority.TrustedContact => "trusted contact",
        MemorySourceAuthority.Agent => "assistant summary",
        _ => "corroborated third-party",
    };
}
