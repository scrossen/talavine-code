using System.Text.Json;
using Microsoft.Extensions.Logging;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// Reusable <see cref="IModelScout"/> for hosts that supply evidence through
/// <see cref="IModelScoutCandidateSource"/>: apply persisted policy weights,
/// rank with the shared <see cref="ModelScoutCandidateRanker"/>, fall back to a
/// disclosed best-effort selection when exact function proof is not yet
/// available, and append a durable recommendation receipt. Hosts with richer
/// evidence pipelines (local hardware fingerprints, proof recovery episodes)
/// register their own <see cref="IModelScout"/> instead.
/// </summary>
public sealed class CandidateSourceModelScout(
    IModelScoutCandidateSource candidateSource,
    ILlmRoutingAuditStore auditStore,
    IRoutingPolicyStore? routingPolicyStore = null,
    ILogger<CandidateSourceModelScout>? logger = null,
    ISubscriptionQuotaTracker? subscriptionQuotaTracker = null,
    ILlmRouteLoadTracker? routeLoadTracker = null) : IModelScout
{
    private const int MaxAuditedCandidates = 10;

    public async Task<ModelScoutRecommendationDecision> RecommendAsync(
        ModelScoutRecommendationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var effectiveRequest = await ApplyPersistedPolicyWeightsAsync(request, cancellationToken)
            .ConfigureAwait(false);

        ModelScoutRecommendationDecision decision;
        try
        {
            var candidates = await candidateSource.LoadCandidatesAsync(effectiveRequest, cancellationToken)
                .ConfigureAwait(false);
            var subscriptionQuota = ModelScoutSubscriptionQuotaContext.FromTracker(subscriptionQuotaTracker);
            var routeLoad = routeLoadTracker?.Snapshot();
            decision = ModelScoutCandidateRanker.Rank(effectiveRequest, candidates, subscriptionQuota, routeLoad);
            if (decision.Selected is null && effectiveRequest.RequireCurrentFunctionEvidence)
            {
                // Exact function proof is a promotion gate, not a circuit breaker:
                // re-rank the same inventory without the temporary proof
                // requirement, keeping every hard capability/privacy/cost gate,
                // and disclose that the selection is provisional.
                var bestEffort = ModelScoutCandidateRanker.Rank(
                    CopyWithoutProofRequirement(effectiveRequest),
                    candidates,
                    subscriptionQuota,
                    routeLoad);
                if (bestEffort.Selected is not null)
                {
                    decision = new ModelScoutRecommendationDecision
                    {
                        Request = effectiveRequest,
                        Demand = bestEffort.Demand,
                        AuditCorrelationId = decision.AuditCorrelationId,
                        Selected = bestEffort.Selected,
                        Candidates = bestEffort.Candidates,
                        Reason =
                            $"Best available route while exact {effectiveRequest.FunctionType} proof is refreshed: {bestEffort.Reason}",
                    };
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                ex,
                "Model Scout candidate discovery failed for {FunctionType}; returning an auditable no-route decision",
                effectiveRequest.FunctionType);
            decision = new ModelScoutRecommendationDecision
            {
                Request = effectiveRequest,
                Reason = $"Model Scout could not load candidate evidence for {effectiveRequest.FunctionType}: "
                         + (RoutingDiagnosticSanitizer.Sanitize(ex.Message, 300) ?? "candidate evidence load failed"),
            };
        }

        if (effectiveRequest.PersistAudit)
        {
            await AppendRecommendationAuditAsync(decision, cancellationToken).ConfigureAwait(false);
        }

        return decision;
    }

    private async Task<ModelScoutRecommendationRequest> ApplyPersistedPolicyWeightsAsync(
        ModelScoutRecommendationRequest request,
        CancellationToken ct)
    {
        if (routingPolicyStore is null)
            return request;

        RoutingPolicy policy;
        try
        {
            policy = await routingPolicyStore.GetAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(
                ex,
                "Could not read the routing policy; ranking with tier-default Scout weights");
            return request;
        }

        policy = RoutingPolicy.NormalizeUntrusted(policy);

        return new ModelScoutRecommendationRequest
        {
            FunctionType = request.FunctionType,
            Provider = request.Provider,
            ExactModel = request.ExactModel,
            ModelRequest = request.ModelRequest?.Snapshot(),
            PreferredModelConnection = policy.PreferredModelConnection,
            AllowedProviders = request.AllowedProviders,
            Tier = request.Tier,
            Interactivity = request.Interactivity,
            Workload = request.Workload,
            Demand = request.Demand,
            SubscriptionQuotaThreshold = request.SubscriptionQuotaThreshold ?? policy.SubscriptionQuotaThreshold,
            PrivacyClass = request.PrivacyClass,
            RequiresVision = request.RequiresVision,
            RequiresToolUse = request.RequiresToolUse,
            RequiresStructuredOutput = request.RequiresStructuredOutput,
            StructuredOutputRepairAvailable = request.StructuredOutputRepairAvailable,
            MinimumContextTokens = request.MinimumContextTokens,
            EstimatedInputTokens = request.EstimatedInputTokens,
            EstimatedOutputTokens = request.EstimatedOutputTokens,
            MaximumEstimatedCostUsd = request.MaximumEstimatedCostUsd,
            LatencyTargetMs = request.LatencyTargetMs,
            MinimumCapabilityScore = request.MinimumCapabilityScore,
            RequireMeasuredCapabilityScore = request.RequireMeasuredCapabilityScore,
            MinimumReliabilityScore = request.MinimumReliabilityScore,
            Weights = request.Weights ?? (policy.UseCustomModelScoutWeights ? new ModelScoutScoreWeights(
                policy.ModelScoutCapabilityWeight,
                policy.ModelScoutSpeedWeight,
                policy.ModelScoutCostWeight) : null),
            PersistAudit = request.PersistAudit,
            AuditSessionId = request.AuditSessionId,
            AuditTurnId = request.AuditTurnId,
            RequireCurrentFunctionEvidence = request.RequireCurrentFunctionEvidence,
            MaintainCurrentFunctionEvidenceRecovery = request.MaintainCurrentFunctionEvidenceRecovery,
            AllowLiveInventoryRefresh = request.AllowLiveInventoryRefresh,
            MaintainServingInventory = request.MaintainServingInventory,
        };
    }

    private static ModelScoutRecommendationRequest CopyWithoutProofRequirement(
        ModelScoutRecommendationRequest source) => new()
    {
        FunctionType = source.FunctionType,
        Provider = source.Provider,
        ExactModel = source.ExactModel,
        ModelRequest = source.ModelRequest?.Snapshot(),
        PreferredModelConnection = source.PreferredModelConnection,
        AllowedProviders = source.AllowedProviders,
        Tier = source.Tier,
        Interactivity = source.Interactivity,
        Workload = source.Workload,
        Demand = source.Demand,
        SubscriptionQuotaThreshold = source.SubscriptionQuotaThreshold,
        PrivacyClass = source.PrivacyClass,
        RequiresVision = source.RequiresVision,
        RequiresToolUse = source.RequiresToolUse,
        RequiresStructuredOutput = source.RequiresStructuredOutput,
        StructuredOutputRepairAvailable = source.StructuredOutputRepairAvailable,
        MinimumContextTokens = source.MinimumContextTokens,
        EstimatedInputTokens = source.EstimatedInputTokens,
        EstimatedOutputTokens = source.EstimatedOutputTokens,
        MaximumEstimatedCostUsd = source.MaximumEstimatedCostUsd,
        LatencyTargetMs = source.LatencyTargetMs,
        MinimumCapabilityScore = source.MinimumCapabilityScore,
        RequireMeasuredCapabilityScore = source.RequireMeasuredCapabilityScore,
        MinimumReliabilityScore = source.MinimumReliabilityScore,
        Weights = source.Weights,
        PersistAudit = source.PersistAudit,
        AuditSessionId = source.AuditSessionId,
        AuditTurnId = source.AuditTurnId,
        RequireCurrentFunctionEvidence = false,
        MaintainCurrentFunctionEvidenceRecovery = source.MaintainCurrentFunctionEvidenceRecovery,
        AllowLiveInventoryRefresh = source.AllowLiveInventoryRefresh,
        MaintainServingInventory = source.MaintainServingInventory,
    };

    private async Task AppendRecommendationAuditAsync(
        ModelScoutRecommendationDecision decision,
        CancellationToken ct)
    {
        var selected = decision.Selected?.Candidate;
        var evidence = JsonSerializer.Serialize(new
        {
            request = new
            {
                decision.Request.ModelRequest,
                decision.Request.PreferredModelConnection,
                decision.Request.RequiresStructuredOutput,
                decision.Request.StructuredOutputRepairAvailable,
            },
            weights = decision.Selected?.Score.AppliedWeights,
            candidates = decision.Candidates
                .Take(MaxAuditedCandidates)
                .Select(ranked => new
                {
                    ranked.Candidate.Provider,
                    ranked.Candidate.Model,
                    ranked.Candidate.CapabilityScore,
                    CapabilityProvenance = ranked.Candidate.CapabilityEvidenceKind.ToString(),
                    ranked.Candidate.LatencySampleOutputTokens,
                    ranked.Candidate.EstimatedLatencyMs,
                    ranked.Candidate.TokensPerSecond,
                    ranked.Candidate.EstimatedCostUsd,
                    ranked.Candidate.ReliabilityScore,
                    ranked.Candidate.EvidenceSource,
                    ranked.Score,
                    ranked.RejectionReason,
                }),
            truncatedCandidates = Math.Max(0, decision.Candidates.Count - MaxAuditedCandidates),
        });

        try
        {
            await auditStore.AppendAsync(
                new LlmRoutingAuditEntry
                {
                    EventKind = LlmRoutingAuditEventKinds.Recommendation,
                    Actor = "model-scout",
                    Trigger = "recommendation-request",
                    FunctionType = ModelScoutFunctionEvidence.NormalizeFunctionType(
                        decision.Request.FunctionType),
                    Context =
                        $"{decision.Request.Tier}/{decision.Request.Interactivity}/{decision.Request.PrivacyClass}",
                    AfterProvider = selected?.Provider,
                    AfterModel = selected?.Model,
                    AdvisorOrPolicy = nameof(ModelScoutCandidateRanker),
                    Reason = decision.Reason,
                    EvidenceJson = evidence,
                    CorrelationId = decision.AuditCorrelationId,
                    SessionId = decision.Request.AuditSessionId,
                    TurnId = decision.Request.AuditTurnId,
                    Succeeded = selected is not null,
                    Error = selected is null ? decision.Reason : null,
                },
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // PersistAudit is an explicit contract: callers must never receive a
            // successful recommendation that has no durable receipt.
            throw new InvalidOperationException(
                "Model Scout could not persist the requested recommendation audit receipt.",
                ex);
        }
    }
}
