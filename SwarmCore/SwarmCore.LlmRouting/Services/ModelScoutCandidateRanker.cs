using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.ProviderPacks;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// Pure, deterministic Model Scout ranker shared by every host. Capability is
/// a hard admission gate; speed and cost are comparative optimizers among
/// capable candidates. Missing measurements are penalized and disclosed.
/// <para>
/// Demand orders the admissible set before the weighted score (see
/// <see cref="LlmTaskDemandPolicy"/>). Categorical catalog priors describe a
/// route's position inside its own family (local runtimes, one subscription
/// CLI, one API); they are not comparable across route kinds. So a local
/// route's categorical prior never lifts it above a frontier cloud or
/// subscription route for complex or high-stakes user-waiting work, and only
/// measured function evidence may place a local route in that group.
/// </para>
/// </summary>
public static class ModelScoutCandidateRanker
{
    private const decimal AutomaticReservedSubscriptionCapacityWeight = 25m;

    /// <summary>The frontier capability bar shared with the tier floors.</summary>
    private static readonly decimal FrontierCapabilityFloor =
        ModelScoutCapabilityEvidence.MinimumScoreForTier(ModelScoutTier.Frontier);

    /// <summary>A request that fills more than this share of a window needs compaction.</summary>
    private const decimal ContextHeadroomFraction = 0.90m;

    /// <summary>At or below this share of own work in the total wait, a shared node counts as busy.</summary>
    private const decimal BusyNodeQueueFactor = 0.50m;

    public static ModelScoutRecommendationDecision Rank(
        ModelScoutRecommendationRequest request,
        IEnumerable<ModelScoutCandidate> candidates,
        ModelScoutSubscriptionQuotaContext? subscriptionQuota = null,
        ModelScoutRouteLoadContext? routeLoad = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(candidates);
        if (string.IsNullOrWhiteSpace(request.FunctionType))
            throw new ArgumentException("A function type is required.", nameof(request));

        request.ModelRequest?.Snapshot(); // Validate caller-owned lists before ranking.
        var quota = subscriptionQuota ?? ModelScoutSubscriptionQuotaContext.Empty;
        var load = routeLoad ?? ModelScoutRouteLoadContext.Empty;
        var materialized = candidates.ToList();
        var workload = LlmWorkloadPolicy.Resolve(request.Workload, request.Interactivity, request.FunctionType);
        var demand = ResolveDemand(request);
        var preferQuality = request.Weights is null
                            && LlmWorkloadPolicy.PrefersHighQuality(workload, request.Tier);
        var weights = NormalizeWeights(request.Weights ?? DefaultWeights(request));
        var capabilityFloor = request.MinimumCapabilityScore is { } explicitFloor
            ? Math.Max(explicitFloor, ModelScoutCapabilityEvidence.MinimumScoreForTier(request.Tier))
            : ModelScoutCapabilityEvidence.MinimumScoreForTier(request.Tier);

        // Speed is normalized over idle projections, then a busy shared node's
        // score is scaled by its queue. Folding the queue into the projection
        // instead would compare busy local routes only with each other while
        // routes without measurements keep their categorical prior speed.
        decimal? Projected(ModelScoutCandidate candidate)
            => ProjectedLatencyMs(candidate, request.EstimatedOutputTokens);

        var eligibleForNormalization = materialized
            .Where(c => BasicRejectionReason(request, c, capabilityFloor, demand) is null)
            .ToList();
        var costs = eligibleForNormalization
            .Select(c => EffectiveCost(c))
            .Where(v => v is not null)
            .Select(v => v!.Value)
            .ToList();
        var projectedLatencies = eligibleForNormalization
            .Select(Projected)
            .Where(v => v is > 0)
            .Select(v => v!.Value)
            .ToList();

        var ranked = materialized.Select(candidate =>
        {
            var rejection = BasicRejectionReason(request, candidate, capabilityFloor, demand);
            var routingPrior = CompleteRoutingPrior(candidate);
            var capability = candidate.FunctionPerformance is { } performance && performance.Matches(request, candidate)
                ? performance.Score : CapabilityScore(candidate, routingPrior);
            var projected = Projected(candidate);
            var speed = SpeedScore(
                candidate,
                projected,
                projectedLatencies,
                request.LatencyTargetMs) * QueueFactor(candidate, projected, demand, load);
            var cost = CostScore(candidate, costs, quota);
            var reliability = Clamp01(candidate.ReliabilityScore ?? 0.75m);
            var evidence = EvidenceConfidence(request, candidate);
            var weighted = capability * weights.Capability
                         + speed * weights.Speed
                         + cost * weights.Cost;
            var total = Clamp01(weighted * (0.80m + 0.20m * reliability) * (0.85m + 0.15m * evidence));

            return new ModelScoutRankedCandidate
            {
                Candidate = candidate,
                RejectionReason = rejection,
                RankingNote = BuildRankingNote(request, demand, candidate, quota, load)
                    + (candidate.FunctionPerformance is { } measured && measured.Matches(request, candidate)
                        ? $" Function probe score {measured.Score:0.###} ({measured.CaseCount} cases; suite {measured.SuiteSha256})." : ""),
                Score = new ModelScoutScoreBreakdown(
                    capability,
                    speed,
                    cost,
                    reliability,
                    evidence,
                    total,
                    weights,
                    routingPrior),
            };
        })
        .OrderByDescending(c => c.IsEligible)
        .ThenBy(c => ModelScoutModelRequests.PreferenceIndex(request.ModelRequest, c.Candidate.Provider, c.Candidate.Model))
        .ThenBy(c => ModelScoutModelRequests.ConnectionOrder(request, c.Candidate))
        // A route whose window must drop part of a user-waiting request serves
        // only when no route can hold it whole, and one that must first grow
        // its window (a model reload) ranks after one that holds it as is.
        .ThenBy(c => ContextFitOrder(demand, c.Candidate))
        // Demand is a preference group, not another pass/fail gate: if no
        // preferred route survives the hard gates, the best admissible route serves.
        .ThenBy(c => DemandRouteOrder(request, demand, c.Candidate, quota))
        // A waiting request does not queue behind background work: a shared
        // node already holding at least this request's own work ranks after
        // routes that can start now. Speed normalization is relative, so this
        // cannot be left to the score alone.
        .ThenBy(c => QueueFactor(c.Candidate, Projected(c.Candidate), demand, load) <= BusyNodeQueueFactor ? 1 : 0)
        // Quality is a preference group, not another pass/fail gate. If no
        // preferred route survives hard gates, the best admissible route serves.
        .ThenByDescending(c => preferQuality && HasPreferredQualityEvidence(c.Candidate))
        .ThenBy(c => request.Weights is null
                    && LlmWorkloadPolicy.UsesEconomicalOrdering(workload, request.Tier)
            ? ModelScoutCostPolicy.RoutineOrder(c.Candidate.Provider, c.Candidate.Model,
                c.Candidate.IsLocal, c.Candidate.EstimatedCostUsd)
            : (0, 0m))
        .ThenByDescending(c => c.Score.Total)
        .ThenByDescending(c => c.Candidate.CapabilityScore)
        .ThenBy(c => c.Candidate.EstimatedCostUsd ?? decimal.MaxValue)
        .ThenBy(c => c.ProviderModelKey(), StringComparer.OrdinalIgnoreCase)
        .ToList();

        var selected = ranked.FirstOrDefault(c => c.IsEligible);
        return new ModelScoutRecommendationDecision
        {
            Request = request,
            Demand = demand,
            Selected = selected,
            Candidates = ranked,
            Reason = selected is null
                ? BuildNoRouteReason(request, materialized.Count, capabilityFloor, ranked)
                : BuildSelectionReason(selected, request, weights, quota, demand, load),
        };
    }

    /// <summary>The demand this request carries, resolved exactly as the ranker orders it.</summary>
    public static LlmResolvedTaskDemand ResolveDemand(ModelScoutRecommendationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var declared = request.Demand;
        // A plain prompt cannot be compacted by the router; only a tool-use
        // transcript can. Unless the caller says otherwise, the whole prompt of
        // a non-tool call must fit.
        if (declared?.IrreducibleInputTokens is null && !request.RequiresToolUse)
            declared = (declared ?? new LlmTaskDemand()) with { IrreducibleInputTokens = request.EstimatedInputTokens };
        return LlmTaskDemandPolicy.Resolve(
            request.FunctionType,
            request.Interactivity,
            request.Workload,
            declared,
            request.EstimatedInputTokens,
            request.EstimatedOutputTokens);
    }

    /// <summary>
    /// True when the route meets the frontier bar in a way that is comparable
    /// across route kinds: a remote route cleared or measured at the frontier
    /// tier, or any route with measured evidence for this exact function. A
    /// local route's categorical clearance ranks it among local routes only.
    /// </summary>
    public static bool IsFrontierClassForDemand(ModelScoutCandidate candidate, string functionType)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return IsFrontierClass(
            candidate.CapabilityScore,
            candidate.IsLocal,
            candidate.CapabilityEvidenceKind,
            candidate.EvidenceFunctionType,
            functionType);
    }

    /// <summary>The shared frontier-class rule, also used by the routing policy check.</summary>
    public static bool IsFrontierClass(
        decimal capabilityScore,
        bool isLocal,
        ModelScoutCapabilityEvidenceKind evidenceKind,
        string? evidenceFunctionType,
        string functionType)
    {
        if (capabilityScore < FrontierCapabilityFloor) return false;
        if (!isLocal) return true;
        return evidenceKind == ModelScoutCapabilityEvidenceKind.MeasuredFunctionBenchmark
               && string.Equals(
                   ModelScoutFunctionEvidence.NormalizeFunctionType(evidenceFunctionType),
                   ModelScoutFunctionEvidence.NormalizeFunctionType(functionType),
                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// For a request someone is waiting on: 0 when the route holds it at its
    /// current window, 1 when the route must first grow its window (the model
    /// reloads while the person waits), 2 when even its largest window would
    /// need compaction. Work nobody is waiting on is not ordered by fit.
    /// </summary>
    private static int ContextFitOrder(LlmResolvedTaskDemand demand, ModelScoutCandidate candidate)
    {
        if (!demand.IsUserWaiting) return 0;
        if (NeedsCompaction(demand, candidate)) return 2;
        return Overfills(demand, candidate.MaxContextTokens) ? 1 : 0;
    }

    /// <summary>Even the largest window the route serves needs compaction for the whole request.</summary>
    private static bool NeedsCompaction(LlmResolvedTaskDemand demand, ModelScoutCandidate candidate)
        => Overfills(demand, ContextCapacity(candidate));

    private static bool Overfills(LlmResolvedTaskDemand demand, int? window)
        => window is > 0 && demand.RequiredContextTokens > window.Value * ContextHeadroomFraction;

    /// <summary>
    /// The largest window the route serves a request whole at: its current
    /// window, or the larger window its runtime grows to. Unknown when neither
    /// is reported.
    /// </summary>
    public static int? ContextCapacity(ModelScoutCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return candidate.MaxGrowableContextTokens is > 0 and var growable
               && (candidate.MaxContextTokens is not > 0 || growable > candidate.MaxContextTokens)
            ? growable
            : candidate.MaxContextTokens;
    }

    /// <summary>
    /// Route-kind preference for this demand. Lower sorts first. Explicit
    /// weights, LocalOnly privacy, and work nobody is waiting on add none:
    /// bulk work already prefers local routes through economical ordering.
    /// </summary>
    private static int DemandRouteOrder(
        ModelScoutRecommendationRequest request,
        LlmResolvedTaskDemand demand,
        ModelScoutCandidate candidate,
        ModelScoutSubscriptionQuotaContext quota)
    {
        if (request.Weights is not null
            || request.PrivacyClass == LlmPrivacyClass.LocalOnly
            || !demand.IsUserWaiting)
        {
            return 0;
        }

        if (demand.IsComplexOrHighStakes)
        {
            if (IsFrontierClassForDemand(candidate, request.FunctionType))
                return candidate.IsLocal ? 0 : ChannelOrder(request, candidate, quota);
            return candidate.IsLocal ? 20 : 10 + ChannelOrder(request, candidate, quota);
        }

        if (demand.ConversationalAssistant)
            return candidate.IsSubscription && !IsSubscriptionPressured(request, candidate, quota) ? 0 : 10;

        return 0;
    }

    /// <summary>
    /// Among remote routes of one quality group: the saved connection
    /// preference (subscription-first by default), with a subscription whose
    /// window is under pressure after every unpressured channel.
    /// </summary>
    private static int ChannelOrder(
        ModelScoutRecommendationRequest request,
        ModelScoutCandidate candidate,
        ModelScoutSubscriptionQuotaContext quota)
    {
        if (candidate.IsSubscription && IsSubscriptionPressured(request, candidate, quota)) return 2;
        return request.PreferredModelConnection switch
        {
            ModelScoutConnectionPreference.SubscriptionFirst => candidate.IsSubscription ? 0 : 1,
            ModelScoutConnectionPreference.ApiFirst => candidate.IsSubscription ? 1 : 0,
            _ => 0,
        };
    }

    private static bool IsSubscriptionPressured(
        ModelScoutRecommendationRequest request,
        ModelScoutCandidate candidate,
        ModelScoutSubscriptionQuotaContext quota)
        => candidate.IsSubscription
           && request.SubscriptionQuotaThreshold is { } threshold
           && quota.TryGetBurnFraction(candidate.Provider, out var burn)
           && burn >= Math.Clamp(threshold, 0m, 1m);

    private static string? BuildRankingNote(
        ModelScoutRecommendationRequest request,
        LlmResolvedTaskDemand demand,
        ModelScoutCandidate candidate,
        ModelScoutSubscriptionQuotaContext quota,
        ModelScoutRouteLoadContext load)
    {
        var notes = new List<string>(3);
        if (request.Weights is null
            && request.PrivacyClass != LlmPrivacyClass.LocalOnly
            && demand.IsUserWaiting)
        {
            if (demand.IsComplexOrHighStakes)
            {
                notes.Add(IsFrontierClassForDemand(candidate, request.FunctionType)
                    ? candidate.IsLocal ? "frontier-class (measured function evidence)" : "frontier-class remote route"
                    : candidate.IsLocal
                        ? "local categorical prior; ranks after frontier routes for this demand"
                        : "below the frontier bar for this demand");
            }
            else if (demand.ConversationalAssistant)
            {
                notes.Add(candidate.IsSubscription
                    ? IsSubscriptionPressured(request, candidate, quota)
                        ? "subscription window under pressure; subscription-first preference withheld"
                        : "subscription-first for a waiting assistant turn"
                    : "ranks after healthy subscription routes for a waiting assistant turn");
            }
        }

        if (ContextCapacity(candidate) is > 0 and var window)
        {
            if (demand.IrreducibleContextTokens > window)
                notes.Add($"window {window:N0} cannot hold the {demand.IrreducibleContextTokens:N0}-token irreducible request");
            else if (NeedsCompaction(demand, candidate))
                notes.Add($"window {window:N0} needs compaction for ~{demand.RequiredContextTokens:N0} tokens");
            else if (Overfills(demand, candidate.MaxContextTokens))
                notes.Add($"grows its {candidate.MaxContextTokens:N0}-token window toward {window:N0} to hold ~{demand.RequiredContextTokens:N0} tokens (reloads the model)");
        }

        if (demand.IsUserWaiting && candidate.IsLocal && load.TryGet(candidate.Provider, out var nodeLoad) && nodeLoad.InFlight > 0)
            notes.Add($"node busy: {nodeLoad.InFlight} in flight ({nodeLoad.InFlightBackground} background)");

        return notes.Count == 0 ? null : string.Join("; ", notes);
    }

    public static ModelScoutScoreWeights DefaultWeights(ModelScoutRecommendationRequest request)
    {
        if (LlmWorkloadPolicy.IsLatencySensitive(
                LlmWorkloadPolicy.Resolve(request.Workload, request.Interactivity, request.FunctionType)))
            return new ModelScoutScoreWeights(0.45m, 0.45m, 0.10m);

        if (request.Tier == ModelScoutTier.Frontier)
            return new ModelScoutScoreWeights(0.70m, 0.20m, 0.10m);

        if (request.Tier == ModelScoutTier.Economy)
        {
            return request.Interactivity == LlmInteractivity.UserWaiting
                ? new ModelScoutScoreWeights(0.40m, 0.25m, 0.35m)
                : new ModelScoutScoreWeights(0.40m, 0.15m, 0.45m);
        }

        return request.Interactivity switch
        {
            LlmInteractivity.UserWaiting => new ModelScoutScoreWeights(0.45m, 0.40m, 0.15m),
            LlmInteractivity.Background => new ModelScoutScoreWeights(0.45m, 0.15m, 0.40m),
            _ => new ModelScoutScoreWeights(0.50m, 0.25m, 0.25m),
        };
    }

    private static string? BasicRejectionReason(
        ModelScoutRecommendationRequest request,
        ModelScoutCandidate candidate,
        decimal capabilityFloor,
        LlmResolvedTaskDemand demand)
    {
        if (!candidate.IsAvailable) return "provider/model is not currently available";
        if (ModelScoutModelRequests.Rejection(request.ModelRequest, candidate.Provider, candidate.Model,
                candidate.SupportsToolUse, candidate.SupportsVision, candidate.SupportsStructuredOutput) is { } requirementIssue)
            return requirementIssue;
        if (!string.IsNullOrWhiteSpace(request.ExactModel)
            && !candidate.Model.Equals(request.ExactModel, StringComparison.OrdinalIgnoreCase))
            return "outside the exact model constraint";
        if (!string.IsNullOrWhiteSpace(request.Provider)
            && !ProviderNamesMatch(request.Provider, candidate.Provider))
            return $"outside requested provider {request.Provider}";
        if (request.AllowedProviders is { Count: > 0 }
            && !request.AllowedProviders.Any(provider => ProviderNamesMatch(provider, candidate.Provider)))
            return "provider is not in the caller's available-provider set";
        if (request.PrivacyClass == LlmPrivacyClass.LocalOnly && !candidate.IsLocal)
            return "privacy policy requires a local route";
        if (candidate.IsSubscription
            && string.IsNullOrWhiteSpace(request.ExactModel)
            && ModelScoutModelRequests.PreferenceIndex(request.ModelRequest, candidate.Provider, candidate.Model) == int.MaxValue
            && request.Tier != ModelScoutTier.Frontier
            && ProviderPackCatalog.ResolveSubscriptionCapacityWeight(candidate.Provider, candidate.Model)
            >= AutomaticReservedSubscriptionCapacityWeight)
        {
            return "subscription tier is reserved for explicit use because it consumes scarce session capacity";
        }

        if (request.RequiresVision && candidate.SupportsVision != true)
            return "vision capability is not proven";
        if (ProviderPackCatalog.TryGetModelFacts(candidate.Provider, candidate.Model, out var facts)
            && facts?.ExecutionTransport is { } transport)
        {
            var contract = facts.Transports.SingleOrDefault(t => t.Transport == transport);
            if (contract is null) return "model transport capabilities are unknown";
            if (contract.Reject(request.RequiresToolUse, request.RequiresVision,
                    request.RequiresStructuredOutput && !request.StructuredOutputRepairAvailable) is { } issue)
                return issue;
        }
        if (request.RequiresToolUse && candidate.SupportsToolUse != true)
            return "tool-use capability is not proven";
        if (request.RequiresStructuredOutput)
        {
            // A caller-authorized repair path validates text JSON after the
            // provider call, so provider-native schema support is not required.
            // Without that explicit contract, the strict pre-proven gate stands.
            var structuredIneligible = !request.StructuredOutputRepairAvailable
                                       && candidate.SupportsStructuredOutput != true;
            if (structuredIneligible)
                return "structured-output capability is not proven";
        }
        if (request.MinimumContextTokens is > 0 and var minimumContextTokens)
        {
            if (candidate.MaxContextTokens is null)
                return $"a {minimumContextTokens:N0}-token context window is required but this route has no verified context size";
            if (candidate.MaxContextTokens < minimumContextTokens)
                return $"verified context window {candidate.MaxContextTokens:N0} is below the {minimumContextTokens:N0}-token requirement";
        }
        // A window that cannot hold even the part of the request compaction
        // cannot remove would silently drop it (a local runtime truncates the
        // oldest messages, often the question itself). A runtime that grows its
        // window holds up to its largest one. Unknown windows are not judged
        // here; the provider's own overflow report still applies.
        if (ContextCapacity(candidate) is > 0 and var window
            && demand.IrreducibleContextTokens > window)
            return $"context window {window:N0} cannot hold this request's {demand.IrreducibleContextTokens:N0} tokens that compaction cannot remove";
        if (request.RequireMeasuredCapabilityScore
            && !ModelScoutCapabilityEvidence.IsMeasured(candidate.CapabilityEvidenceKind))
            return "a measured capability score is required; catalog or heuristic priors are not admissible";
        if (candidate.CapabilityScore < capabilityFloor)
            return $"capability {candidate.CapabilityScore:0.0} is below the {capabilityFloor:0.0} gate";
        if (candidate.ReliabilityScore is { } reliability
            && (candidate.ReliabilitySampleCount is null or >= 5)
            && reliability < Math.Clamp(request.MinimumReliabilityScore, 0m, 1m))
            return $"measured reliability {reliability:P0} is below the {Math.Clamp(request.MinimumReliabilityScore, 0m, 1m):P0} gate";
        if (request.MaximumEstimatedCostUsd is { } maxCost)
        {
            var cost = EffectiveCost(candidate);
            if (cost is null)
                return "a hard cost limit was requested but this route has no verified cost";
            if (cost > maxCost)
                return $"estimated cost {cost:C4} exceeds the {maxCost:C4} limit";
        }
        if (request.RequireCurrentFunctionEvidence
            && !ModelScoutFunctionEvidence.IsCoherent(candidate, request.FunctionType))
            return "current function-scoped evidence is required";
        return null;
    }

    private static decimal SpeedScore(
        ModelScoutCandidate candidate,
        decimal? projected,
        IReadOnlyList<decimal> projectedLatencies,
        int? latencyTargetMs)
    {
        if (projected is > 0 and var projectedLatency)
        {
            if (latencyTargetMs is > 0)
            {
                return Clamp01(latencyTargetMs.Value / projectedLatency);
            }

            return InverseNormalize(projectedLatency, projectedLatencies);
        }

        return CompleteRoutingPrior(candidate) is { } prior
            ? prior.Speed switch
            {
                ModelScoutRelativeSpeedPrior.Deliberate => 0.65m,
                ModelScoutRelativeSpeedPrior.Standard => 0.80m,
                ModelScoutRelativeSpeedPrior.Fast => 0.90m,
                _ => 0.25m,
            }
            : 0.25m;
    }

    /// <summary>
    /// The queue ahead of a waiting request on a shared local node. Historical
    /// speed describes an idle node; each call already in flight there (a LAN
    /// GPU serves them one after another) costs roughly one recent response
    /// time before this request starts. Null when nothing is queued, the route
    /// is remote, or nobody is waiting.
    /// </summary>
    private static decimal? QueueWaitMs(
        ModelScoutCandidate candidate,
        decimal? projected,
        LlmResolvedTaskDemand demand,
        ModelScoutRouteLoadContext load)
    {
        if (!demand.IsUserWaiting
            || !candidate.IsLocal
            || !load.TryGet(candidate.Provider, out var nodeLoad)
            || nodeLoad.InFlight <= 0)
        {
            return null;
        }

        var perCallMs = nodeLoad.RecentResponseMs is > 0 and var recent ? recent : projected;
        return perCallMs is > 0 ? nodeLoad.InFlight * perCallMs.Value : null;
    }

    /// <summary>
    /// Share of the waiting time that is this request's own work:
    /// own ÷ (own + queue). With no timing evidence at all, each queued call
    /// is assumed to cost as much as this one. 1 when nothing is queued.
    /// </summary>
    private static decimal QueueFactor(
        ModelScoutCandidate candidate,
        decimal? projected,
        LlmResolvedTaskDemand demand,
        ModelScoutRouteLoadContext load)
    {
        if (!demand.IsUserWaiting
            || !candidate.IsLocal
            || !load.TryGet(candidate.Provider, out var nodeLoad)
            || nodeLoad.InFlight <= 0)
        {
            return 1m;
        }

        var ownMs = projected ?? nodeLoad.RecentResponseMs;
        var queueMs = QueueWaitMs(candidate, projected, demand, load);
        if (ownMs is not > 0 || queueMs is not > 0)
            return 1m / (1m + nodeLoad.InFlight);
        return Clamp01(ownMs.Value / (ownMs.Value + queueMs.Value));
    }

    /// <summary>
    /// Projects user-visible response time for the current workload. A measured
    /// response latency is a complete response sample, not time to first token.
    /// When its sampled output size is known, decompose it with measured
    /// throughput or conservatively extend the baseline sample. Static rows whose
    /// sampled output size is unknown retain their total-latency evidence without
    /// adding it again as fixed startup time. This is ranking evidence only and
    /// never grants or revokes model eligibility.
    /// </summary>
    private static decimal? ProjectedLatencyMs(
        ModelScoutCandidate candidate,
        int estimatedOutputTokens)
    {
        var targetOutputTokens = Math.Max(1, estimatedOutputTokens);
        var hasTotalLatency = candidate.EstimatedLatencyMs is > 0;
        var hasBaselineOutput = candidate.LatencySampleOutputTokens is > 0;
        var hasThroughput = candidate.TokensPerSecond is > 0;
        if (!hasTotalLatency && !hasThroughput)
        {
            return null;
        }

        try
        {
            if (hasTotalLatency && hasBaselineOutput)
            {
                var baselineLatencyMs = candidate.EstimatedLatencyMs!.Value;
                var baselineOutputTokens = candidate.LatencySampleOutputTokens!.Value;
                if (hasThroughput)
                {
                    // Throughput is a decode measurement or a fitted response-time
                    // slope. Subtract only the baseline's measured generation share;
                    // the residual is the bounded startup/prefill estimate.
                    var measuredBaselineGenerationMs =
                        baselineOutputTokens / candidate.TokensPerSecond!.Value * 1_000m;
                    var nonGenerationMs = Math.Max(0m, baselineLatencyMs - measuredBaselineGenerationMs);
                    return nonGenerationMs
                           + targetOutputTokens / candidate.TokensPerSecond.Value * 1_000m;
                }

                // Sparse/non-varying telemetry cannot separate startup from decode.
                // Keep the observed total response as the short-workload floor, then
                // extend only output beyond that baseline at its conservative
                // end-to-end effective rate.
                if (targetOutputTokens <= baselineOutputTokens)
                {
                    return baselineLatencyMs;
                }

                var effectiveTokensPerSecond = baselineOutputTokens / baselineLatencyMs * 1_000m;
                return baselineLatencyMs
                       + (targetOutputTokens - baselineOutputTokens)
                       / effectiveTokensPerSecond
                       * 1_000m;
            }

            if (hasThroughput)
            {
                var generationProjectionMs =
                    targetOutputTokens / candidate.TokensPerSecond!.Value * 1_000m;
                // A static/website total-latency row has no sampled output size, so
                // its latency and throughput cannot safely be added. Preserve both
                // as lower-bound evidence instead of double-counting either one.
                return hasTotalLatency
                    ? Math.Max(candidate.EstimatedLatencyMs!.Value, generationProjectionMs)
                    : generationProjectionMs;
            }

            return candidate.EstimatedLatencyMs;
        }
        catch (Exception ex) when (ex is OverflowException or DivideByZeroException)
        {
            // Speed evidence ranks eligible routes but is never authority. Ignore
            // malformed or arithmetically unsafe throughput/sample combinations;
            // retain a valid measured total latency when one exists.
            return hasTotalLatency
                ? candidate.EstimatedLatencyMs
                : null;
        }
    }

    private static decimal CostScore(
        ModelScoutCandidate candidate,
        IReadOnlyList<decimal> costs,
        ModelScoutSubscriptionQuotaContext quota)
    {
        if (candidate.IsSubscription)
        {
            quota.TryGetBurnFraction(candidate.Provider, out var burn);
            return ModelScoutCostPolicy.CapacityUtility(candidate.Provider, candidate.Model, burn);
        }
        return BaseCostScore(candidate, costs);
    }

    private static decimal BaseCostScore(ModelScoutCandidate candidate, IReadOnlyList<decimal> costs)
    {
        // A subscription route has no marginal API bill, but different model
        // families consume meaningfully different amounts of finite quota. Use
        // the explicit capacity category until real applicable economics exist;
        // never manufacture a dollar value to represent it.
        if (candidate.IsSubscription)
        {
            return ModelScoutCostPolicy.CapacityUtility(candidate.Provider, candidate.Model);
        }

        var cost = EffectiveCost(candidate);
        if (cost is null) return 0.25m;
        return InverseNormalize(cost.Value, costs);
    }

    private static decimal CapabilityScore(
        ModelScoutCandidate candidate,
        ModelScoutRoutingPrior? prior)
    {
        if (ModelScoutCapabilityEvidence.IsMeasured(candidate.CapabilityEvidenceKind)
            || prior is null)
        {
            return Clamp01(candidate.CapabilityScore / 100m);
        }

        return prior.Quality switch
        {
            ModelScoutRelativeQualityPrior.Economy => 0.75m,
            ModelScoutRelativeQualityPrior.Standard => 0.90m,
            ModelScoutRelativeQualityPrior.High => 1.00m,
            _ => Clamp01(candidate.CapabilityScore / 100m),
        };
    }

    private static decimal? EffectiveCost(ModelScoutCandidate candidate)
        => candidate.IsLocal || candidate.IsSubscription ? 0m : candidate.EstimatedCostUsd;

    private static decimal EvidenceConfidence(ModelScoutRecommendationRequest request, ModelScoutCandidate candidate)
    {
        if (!EvidenceIsCurrent(candidate)) return 0.25m;
        if (string.Equals(
                ModelScoutFunctionEvidence.NormalizeFunctionType(candidate.EvidenceFunctionType),
                ModelScoutFunctionEvidence.NormalizeFunctionType(request.FunctionType),
                StringComparison.OrdinalIgnoreCase)) return 1m;
        if (!string.IsNullOrWhiteSpace(candidate.EvidenceFunctionType)) return 0.60m;
        if (candidate.EvidenceSource?.Contains("heuristic", StringComparison.OrdinalIgnoreCase) == true) return 0.45m;
        return 0.75m;
    }

    private static decimal InverseNormalize(decimal value, IReadOnlyList<decimal> values)
    {
        // An empty comparison pool means no eligible candidate carries this
        // measurement; score it like any other unknown instead of above 1.
        if (values.Count == 0) return 0.25m;
        var min = values.Min();
        var max = values.Max();
        if (max == min) return 1m;
        return Clamp01(1m - (value - min) / (max - min));
    }

    private static ModelScoutScoreWeights NormalizeWeights(ModelScoutScoreWeights weights)
    {
        var capability = Math.Max(0, weights.Capability);
        var speed = Math.Max(0, weights.Speed);
        var cost = Math.Max(0, weights.Cost);
        var total = capability + speed + cost;
        return total <= 0
            ? new ModelScoutScoreWeights(0.50m, 0.25m, 0.25m)
            : new ModelScoutScoreWeights(capability / total, speed / total, cost / total);
    }

    private static decimal Clamp01(decimal value) => Math.Clamp(value, 0m, 1m);

    private static bool EvidenceIsCurrent(ModelScoutCandidate candidate)
        => candidate.IsEvidenceCurrent
           && (candidate.EvidenceExpiresAtUtc is null
               || candidate.EvidenceExpiresAtUtc > DateTime.UtcNow);

    private static bool ProviderNamesMatch(string left, string right)
        => NormalizeProviderName(left).Equals(NormalizeProviderName(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeProviderName(string provider) => provider.Trim().ToUpperInvariant() switch
    {
        "ANTHROPIC" => "CLAUDE",
        "GOOGLE" => "GEMINI",
        "XAI" => "GROK",
        "LM STUDIO" => "LMSTUDIO",
        _ => provider.Trim().ToUpperInvariant(),
    };

    private static string BuildSelectionReason(
        ModelScoutRankedCandidate selected,
        ModelScoutRecommendationRequest request,
        ModelScoutScoreWeights weights,
        ModelScoutSubscriptionQuotaContext quota,
        LlmResolvedTaskDemand demand,
        ModelScoutRouteLoadContext load)
    {
        var c = selected.Candidate;
        var prior = CompleteRoutingPrior(c);
        var projectedLatency = ProjectedLatencyMs(c, request.EstimatedOutputTokens);
        var queueText = QueueWaitMs(c, projectedLatency, demand, load) is { } queueMs
            ? $"; about {queueMs / 1_000m:0} s of queued work ahead on this node"
            : string.Empty;
        var speedEvidence = c.EstimatedLatencyMs is { } sampledLatency
                            && c.LatencySampleOutputTokens is { } sampledOutput
            ? $"{projectedLatency:0} ms projected for "
              + $"{Math.Max(1, request.EstimatedOutputTokens):N0} output tokens from a "
              + $"{sampledLatency:0} ms/{sampledOutput:0} token successful baseline"
              + (c.TokensPerSecond is { } fittedThroughput
                  ? $" and {fittedThroughput:0.0} tok/s measured throughput"
                  : string.Empty)
            : c.EstimatedLatencyMs is { } latency
                ? c.TokensPerSecond is { } staticThroughput
                    ? $"{projectedLatency:0} ms projected for "
                      + $"{Math.Max(1, request.EstimatedOutputTokens):N0} output tokens from "
                      + $"{latency:0} ms total-latency and {staticThroughput:0.0} tok/s evidence"
                    : $"{latency:0} ms total-response latency (sample output size unavailable)"
                : c.TokensPerSecond is { } throughput
                    ? $"{throughput:0.0} tok/s projected across "
                      + $"{Math.Max(1, request.EstimatedOutputTokens):N0} output tokens"
                    : prior is not null
                        ? $"{prior.Speed.ToString().ToLowerInvariant()} categorical prior"
                        : "unknown speed";
        var costEvidence = c.EstimatedCostUsd is { } measuredCost
            ? $"estimated request cost {measuredCost:C4}"
            : prior is not null
                ? $"{prior.CapacityEfficiency.ToString().ToLowerInvariant()} categorical subscription-capacity efficiency"
                : c.IsSubscription
                    ? "no marginal API charge; subscription-capacity use unknown"
                    : c.IsLocal
                        ? "no marginal API charge"
                        : "unknown cost";
        var qualityEvidence = prior is null
            ? string.Empty
            : $", {prior.Quality.ToString().ToLowerInvariant()} categorical quality prior";
        var priorSource = prior is null
            ? string.Empty
            : $"; routing prior {prior.Source}";
        if (c.IsSubscription)
        {
            quota.TryGetBurnFraction(c.Provider, out var burn);
            costEvidence += $", relative subscription capacity {ModelScoutCostPolicy.SubscriptionCapacity(c.Provider, c.Model):0.##}, window burn {burn:P0}";
        }

        var workload = LlmWorkloadPolicy.Resolve(request.Workload, request.Interactivity, request.FunctionType);
        var qualityDegraded = request.Weights is null
                              && LlmWorkloadPolicy.PrefersHighQuality(workload, request.Tier)
                              && !HasPreferredQualityEvidence(c);
        var demandText = $"; demand {demand.Class}"
                         + (demand.Signals.Count == 0 ? string.Empty : $" ({string.Join(", ", demand.Signals)})")
                         + $" ({LlmTaskDemandPolicy.Version})";
        var noteText = string.IsNullOrWhiteSpace(selected.RankingNote) ? string.Empty : $"; {selected.RankingNote}";
        return $"Selected {c.Provider}/{c.Model}: capability {c.CapabilityScore:0.0}, "
             + $"speed {speedEvidence}, cost basis {costEvidence}{qualityEvidence}, "
             + $"score {selected.Score.Total:0.000} with capability/speed/cost weights "
             + $"{weights.Capability:0.00}/{weights.Speed:0.00}/{weights.Cost:0.00}; "
             + $"evidence {c.EvidenceSource ?? "unspecified"}{priorSource}; "
             + $"workload {workload} ({LlmWorkloadPolicy.Version})"
             + demandText
             + noteText
             + queueText
             + (qualityDegraded ? "; preferred quality unavailable, using the best admissible route" : string.Empty)
             + ".";
    }

    private static ModelScoutRoutingPrior? CompleteRoutingPrior(ModelScoutCandidate candidate)
        => candidate.RoutingPrior is { IsComplete: true } prior ? prior : null;

    private static bool HasPreferredQualityEvidence(ModelScoutCandidate candidate)
        => ModelScoutCapabilityEvidence.IsMeasured(candidate.CapabilityEvidenceKind)
            ? candidate.CapabilityScore >= 90m
            : CompleteRoutingPrior(candidate)?.Quality is ModelScoutRelativeQualityPrior.Standard
                or ModelScoutRelativeQualityPrior.High;

    private static string BuildNoRouteReason(
        ModelScoutRecommendationRequest request,
        int candidateCount,
        decimal capabilityFloor,
        IReadOnlyList<ModelScoutRankedCandidate> ranked)
    {
        var reasons = ranked
            .Where(c => !c.IsEligible && !string.IsNullOrWhiteSpace(c.RejectionReason))
            .GroupBy(c => c.RejectionReason!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Take(3)
            .Select(g => $"{g.Count()} {g.Key}");
        var detail = string.Join("; ", reasons);
        return $"No eligible route for {request.FunctionType} among {candidateCount} candidate(s); "
             + $"capability floor {capabilityFloor:0.0}."
             + (string.IsNullOrWhiteSpace(detail) ? string.Empty : $" Rejections: {detail}.");
    }

    private static string ProviderModelKey(this ModelScoutRankedCandidate candidate)
        => $"{candidate.Candidate.Provider}/{candidate.Candidate.Model}";
}
