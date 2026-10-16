using System.Text.Json;
using SwarmCore.Agent.Continual;

namespace SwarmCore.Agent.ToolUseEngine;

public static class MissionWorkingContext
{
    public static WorkingContextAuthority Authority(ContinualObjectiveSnapshot objective) => new()
    {
        Objective = objective.ObjectiveText,
        Digest = MissionVerification.Digest(JsonSerializer.Serialize(new { objective.Authority,
            Criteria = MissionVerification.CriteriaDigest(objective), objective.SteeringHistory })),
        SteeringRevision = objective.Mission?.ConstraintVersion ?? 0, MissionStateVersion = objective.Version,
        Constraints = objective.Mission?.Constraints ?? [],
        AcceptanceCriteria = MissionVerification.Criteria(objective).Select(c => JsonSerializer.Serialize(c)).ToArray(),
        // A receipt closed as not-dispatched (a preflight/guard skip, or a read-only call
        // cancelled at a boundary before it ran) is a terminal record, not completed work: it
        // must leave PendingCallIds without being counted as evidence the model can rely on.
        CompletedReceiptIds = objective.Receipts.Where(r => r.State == ContinualToolReceiptState.Completed && r.WasDispatched)
            .Select(r => r.Attempt + ":" + r.ToolCallId).ToArray(),
        PendingCallIds = objective.Receipts.Where(r => r.State == ContinualToolReceiptState.Started)
            .Select(r => r.ToolCallId).ToArray(),
        UnresolvedIssues = string.IsNullOrWhiteSpace(objective.ContinuationFeedback) ? [] : [objective.ContinuationFeedback],
        BudgetReference = objective.Mission?.BudgetReference,
        AllowedSourceAccountIds = objective.Authority.ScopeKind == nameof(SwarmCore.Agent.Runtime.AgentCommunicationScopeKind.SelectedAccounts)
            ? objective.Authority.EffectiveScopeValues.Select(Guid.Parse).ToHashSet()
            : objective.Authority.ScopeKind == nameof(SwarmCore.Agent.Runtime.AgentCommunicationScopeKind.TrustedProfileWide) ? null : new HashSet<Guid>()
    };
}
