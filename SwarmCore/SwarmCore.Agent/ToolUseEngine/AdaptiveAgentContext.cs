using SwarmCore.Abstractions.Context;
using SwarmCore.LlmRouting.Models;

namespace SwarmCore.Agent.ToolUseEngine;

/// <summary>Trusted host observations enter separately from worker/source text.</summary>
public sealed class AdaptiveAgentContext
{
    public IReadOnlyList<AdaptiveReasoningObservation> InitialObservations { get; init; } = [];
    public string? BudgetReference { get; init; }
    public bool RequiresPersonalEvidence { get; set; }
    public IReadOnlyList<string> PriorObservedToolIds { get; init; } = [];
    public AdaptiveExecutionSnapshot? State { get; set; }
    public AdaptiveDecisionRecord? Pending { get; set; }
    public string? EffectiveEffort { get; set; }
    public string? OriginalRequestEffort { get; set; }
}

public static class AdaptiveContextEvidence
{
    public static AdaptiveEvidence Classify(bool required, IReadOnlyList<ContextPack> packs, bool proofComplete)
    {
        if (!required) return AdaptiveEvidence.NotRequired;
        if (!proofComplete) return AdaptiveEvidence.Unverified;
        if (packs.Count == 0 || packs.All(p => p.Sources.Count == 0)) return AdaptiveEvidence.MissingCoverage;
        if (packs.Any(p => p.HasBlockingAuditFailure)) return AdaptiveEvidence.Unverified;
        if (packs.Any(p => p.Coverage.Collections.Any(c => c.Status is "stale-sync" or "ingestion-or-index-incomplete"))) return AdaptiveEvidence.StaleIndex;
        if (packs.Any(p => p.Coverage.Gaps.Any(g => g.Contains("identity", StringComparison.OrdinalIgnoreCase)))) return AdaptiveEvidence.IdentityAmbiguous;
        if (packs.Any(p => p.Coverage.Gaps.Count > 0 || p.Coverage.Status != "evidence-found")) return AdaptiveEvidence.MissingCoverage;
        return packs.All(p => p.Coverage.SourceValidation == "revalidated") ? AdaptiveEvidence.AdmittedComplete : AdaptiveEvidence.Unverified;
    }
}

/// <summary>Safe route/effort diagnostics, never source text or model confidence.</summary>
public sealed record AdaptiveReasoningChangedEvent(Guid DecisionId, string Action, string Outcome, string Reason,
    string? PreviousProvider, string? PreviousModel, string? Provider, string? Model, string? Effort) : AgentChatEvent;

public static class AdaptiveProviderFailure
{
    public static AdaptiveFailure Classify(Exception exception) => exception switch
    {
        SwarmCore.Abstractions.LlmRouting.LlmProviderHttpException http => http.Category switch
        {
            SwarmCore.Abstractions.LlmRouting.LlmFailureCategory.RateLimit => AdaptiveFailure.RateLimited,
            SwarmCore.Abstractions.LlmRouting.LlmFailureCategory.QuotaExhausted => AdaptiveFailure.QuotaExhausted,
            SwarmCore.Abstractions.LlmRouting.LlmFailureCategory.AuthInvalid => AdaptiveFailure.Authentication,
            SwarmCore.Abstractions.LlmRouting.LlmFailureCategory.ContentPolicy => AdaptiveFailure.Permission,
            SwarmCore.Abstractions.LlmRouting.LlmFailureCategory.InvalidRequest or SwarmCore.Abstractions.LlmRouting.LlmFailureCategory.ContextOverflow
                or SwarmCore.Abstractions.LlmRouting.LlmFailureCategory.ModelNotFound => AdaptiveFailure.Configuration,
            SwarmCore.Abstractions.LlmRouting.LlmFailureCategory.MalformedReply => AdaptiveFailure.MalformedOutput,
            _ => AdaptiveFailure.Transport
        },
        System.Text.Json.JsonException => AdaptiveFailure.MalformedOutput,
        UnauthorizedAccessException => AdaptiveFailure.Permission,
        SwarmCore.Abstractions.LlmRouting.LlmExecutionParameterRejectedException => AdaptiveFailure.Configuration,
        _ => AdaptiveFailure.Transport
    };
}
