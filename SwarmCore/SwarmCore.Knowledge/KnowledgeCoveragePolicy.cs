namespace SwarmCore.Knowledge;

/// <summary>Interpret bounded retrieval outcomes without mistaking missing evidence for absence.</summary>
public static class KnowledgeCoveragePolicy
{
    public static string[] MissingClaimSlots(SwarmCore.Abstractions.Context.ContextQueryPlan plan,
        IReadOnlyList<SwarmCore.Abstractions.Context.ContextItem> items)
    {
        var missing = new List<string>();
        foreach (var slot in plan.RequiredClaimSlots)
        {
            var present = slot switch
            {
                "source-evidence" => items.Any(i => i.SourceIds.Count > 0),
                "current-state" => items.Any(i => i.TemporalStatus is not null and not "historical" and not "unknown"),
                "historical-evidence" => items.Any(i => i.TemporalStatus == "historical"),
                "direct-source" => items.Any(i => i.Kind is "message-evidence" or "document-evidence"),
                _ => false
            };
            if (!present) missing.Add("claim-slot-unfilled:" + slot);
        }
        if (plan.Identifiers.Any(id => !items.Any(i => i.Text.Contains(id, StringComparison.OrdinalIgnoreCase))))
            missing.Add("requested-identifier-not-covered");
        // Text mention alone cannot resolve a person's identity.
        if (plan.Entities.Count > 0) missing.Add("requested-entity-identity-not-certified");
        return missing.Distinct(StringComparer.Ordinal).ToArray();
    }

    public static (string Status, string StopReason, string[] Gaps) Assess(
        int evidenceCount, bool conflicting, bool budgetLimited,
        IEnumerable<string> laneStatuses, IEnumerable<string> collectionStatuses)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(evidenceCount);
        var gaps = new List<string>();
        if (laneStatuses.Any(s => s is "failed" or "timed-out" or "partial" or "unavailable"))
            gaps.Add("retrieval-lane-incomplete");
        if (!collectionStatuses.Any() || collectionStatuses.Any(s => s != "recent-sync"))
            gaps.Add("local-collection-freshness-unverified");
        if (conflicting) gaps.Add("conflicting-source-assertions");
        if (evidenceCount == 0) gaps.Add("no-matching-local-evidence");
        if (budgetLimited) gaps.Add("evidence-budget-exhausted");
        return (conflicting ? "conflicting" : evidenceCount == 0 ? "no-matching-evidence" :
            gaps.Count > 0 ? "partial" : "evidence-found",
            budgetLimited ? "budget-exhausted" : "bounded-lanes-exhausted", gaps.ToArray());
    }
}
