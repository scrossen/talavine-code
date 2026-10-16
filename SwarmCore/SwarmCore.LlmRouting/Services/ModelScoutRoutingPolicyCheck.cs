using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Configuration;

namespace SwarmCore.LlmRouting.Services;

/// <summary>One route as the routing policy check sees it.</summary>
public sealed record ModelScoutRouteFacts(
    string Provider,
    string Model,
    bool Eligible,
    bool IsLocal,
    bool IsSubscription,
    decimal CapabilityScore,
    ModelScoutCapabilityEvidenceKind EvidenceKind,
    string? EvidenceFunctionType = null,
    int? MaxContextTokens = null,
    bool SubscriptionPressured = false,
    int InFlight = 0,
    int? MaxGrowableContextTokens = null)
{
    public string Key => $"{Provider}/{Model}";

    /// <summary>
    /// The largest window the route serves a request whole at: its current
    /// window, or the larger window its runtime grows to.
    /// </summary>
    public int? ContextCapacity
        => MaxGrowableContextTokens is > 0 and var growable
           && (MaxContextTokens is not > 0 || growable > MaxContextTokens)
            ? growable
            : MaxContextTokens;

    /// <summary>
    /// Route facts for a provider name alone, when a receipt predates the
    /// recorded local/subscription flags. Uses the shared locality and
    /// subscription registries, never a model name.
    /// </summary>
    public static (bool IsLocal, bool IsSubscription) ClassifyProvider(string provider)
        => (LlmProviderLocalityPolicy.IsLocalProviderName(provider),
            SubscriptionCliProviders.IsSubscriptionCliProvider(provider));
}

/// <summary>A routing decision reduced to what the policy needs to judge it.</summary>
public sealed record ModelScoutRoutingObservation(
    string FunctionType,
    LlmResolvedTaskDemand Demand,
    LlmPrivacyClass PrivacyClass,
    bool CallerOwnsRoute,
    ModelScoutRouteFacts? Selected,
    IReadOnlyList<ModelScoutRouteFacts> Alternatives);

/// <summary>A decision that broke a routing rule, with the route that should have served.</summary>
public sealed record ModelScoutRoutingViolation(
    string Rule,
    string Explanation,
    string? Selected = null,
    string? Preferred = null);

/// <summary>
/// Judges a Model Scout decision against the routing policy rather than
/// against a remembered model name. Every rule is stated over the candidates
/// the decision actually had, so it holds for any customer's inventory:
/// <list type="bullet">
/// <item>complex or high-stakes work someone is waiting on is served by a
/// frontier-class route whenever one was eligible (a local route's categorical
/// catalog prior does not count; measured function evidence does);</item>
/// <item>a waiting assistant turn is served by an unpressured subscription
/// route whenever one was eligible;</item>
/// <item>background work is served locally whenever a local route was eligible;</item>
/// <item>LocalOnly work is served locally;</item>
/// <item>the serving window holds the part of the request that cannot be
/// compacted, and for waiting work holds the whole request when another
/// eligible route could, without reloading its model at a larger window when
/// another eligible route holds it as is. A runtime that grows its window
/// holds up to its largest window.</item>
/// </list>
/// Explicit caller authority (exact model, provider constraint, model request,
/// custom weights, user pins) is exempt from the demand rules, never from
/// privacy or window rules.
/// </summary>
public static class ModelScoutRoutingPolicyCheck
{
    public const string FrontierRouteExpected = "frontier-route-for-complex-waiting-work";
    public const string SubscriptionFirstExpected = "subscription-first-for-waiting-assistant";
    public const string LocalFirstExpected = "local-first-for-background-work";
    public const string LocalOnlyRequiresLocal = "local-only-requires-local";
    public const string WindowMustHoldRequest = "window-must-hold-request";
    public const string WholeRequestShouldFit = "whole-request-should-fit-when-possible";
    public const string WholeRequestWithoutReload = "waiting-request-should-fit-without-reload";
    public const string DemandLost = "host-demand-reached-the-router";

    private const decimal ContextHeadroomFraction = 0.90m;

    /// <summary>Reduce a full ranked decision to a policy observation.</summary>
    public static ModelScoutRoutingObservation Observe(
        ModelScoutRecommendationDecision decision,
        ModelScoutSubscriptionQuotaContext? quota = null,
        ModelScoutRouteLoadContext? load = null)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var request = decision.Request;
        var demand = decision.Demand ?? ModelScoutCandidateRanker.ResolveDemand(request);
        ModelScoutRouteFacts Facts(ModelScoutRankedCandidate ranked)
        {
            var candidate = ranked.Candidate;
            var pressured = candidate.IsSubscription
                            && request.SubscriptionQuotaThreshold is { } threshold
                            && (quota ?? ModelScoutSubscriptionQuotaContext.Empty)
                                .TryGetBurnFraction(candidate.Provider, out var burn)
                            && burn >= Math.Clamp(threshold, 0m, 1m);
            var inFlight = (load ?? ModelScoutRouteLoadContext.Empty).TryGet(candidate.Provider, out var nodeLoad)
                ? nodeLoad.InFlight
                : 0;
            return new ModelScoutRouteFacts(
                candidate.Provider,
                candidate.Model,
                ranked.IsEligible,
                candidate.IsLocal,
                candidate.IsSubscription,
                candidate.CapabilityScore,
                candidate.CapabilityEvidenceKind,
                candidate.EvidenceFunctionType,
                candidate.MaxContextTokens,
                pressured,
                inFlight,
                candidate.MaxGrowableContextTokens);
        }

        return new ModelScoutRoutingObservation(
            request.FunctionType,
            demand,
            request.PrivacyClass,
            CallerOwnsRoute(request),
            decision.Selected is null ? null : Facts(decision.Selected),
            decision.Candidates.Where(c => !ReferenceEquals(c, decision.Selected)).Select(Facts).ToList());
    }

    public static IReadOnlyList<ModelScoutRoutingViolation> Check(
        ModelScoutRecommendationDecision decision,
        ModelScoutSubscriptionQuotaContext? quota = null,
        ModelScoutRouteLoadContext? load = null)
        => Check(Observe(decision, quota, load));

    public static IReadOnlyList<ModelScoutRoutingViolation> Check(ModelScoutRoutingObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var violations = new List<ModelScoutRoutingViolation>();
        if (observation.Selected is not { } selected) return violations;

        var demand = observation.Demand;
        var eligible = observation.Alternatives
            .Where(route => route.Eligible && !string.Equals(route.Key, selected.Key, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (observation.PrivacyClass == LlmPrivacyClass.LocalOnly && !selected.IsLocal)
        {
            violations.Add(new(LocalOnlyRequiresLocal,
                "LocalOnly work was sent to a route that is not local.", selected.Key));
        }

        if (selected.ContextCapacity is > 0 and var window && demand.IrreducibleContextTokens > window)
        {
            violations.Add(new(WindowMustHoldRequest,
                $"The serving window ({window:N0} tokens) cannot hold the {demand.IrreducibleContextTokens:N0} tokens compaction cannot remove.",
                selected.Key));
        }

        var selectedFit = FitOf(demand, selected);
        if (demand.IsUserWaiting && selectedFit == Fit.Compacts
            && eligible.FirstOrDefault(route => route.ContextCapacity is > 0 && FitOf(demand, route) != Fit.Compacts) is { } roomier)
        {
            violations.Add(new(WholeRequestShouldFit,
                $"A person is waiting on a ~{demand.RequiredContextTokens:N0}-token request; the selected window needs compaction while an eligible route holds it whole.",
                selected.Key, roomier.Key));
        }

        if (demand.IsUserWaiting && selectedFit == Fit.Grows
            && eligible.FirstOrDefault(route => route.MaxContextTokens is > 0 && FitOf(demand, route) == Fit.AsIs) is { } asIs)
        {
            violations.Add(new(WholeRequestWithoutReload,
                $"A person is waiting on a ~{demand.RequiredContextTokens:N0}-token request; the selected route reloads its model at a larger window while an eligible route holds the request as is.",
                selected.Key, asIs.Key));
        }

        if (observation.CallerOwnsRoute || observation.PrivacyClass == LlmPrivacyClass.LocalOnly)
            return violations;

        // Judge demand preferences within the selected route's fit class: a
        // route that holds the request whole rightly outranks one that would
        // have to drop part of it, and for a waiting request one that holds it
        // as is outranks one that must reload at a larger window. Work nobody
        // is waiting on only separates holding the request from dropping part.
        int Order(ModelScoutRouteFacts route)
        {
            var fit = FitOf(demand, route);
            return demand.IsUserWaiting ? (int)fit : fit == Fit.Compacts ? 1 : 0;
        }

        var comparable = eligible.Where(route => Order(route) <= Order(selected)).ToList();
        if (!demand.IsUserWaiting)
        {
            if (!selected.IsLocal && comparable.FirstOrDefault(route => route.IsLocal) is { } local)
            {
                violations.Add(new(LocalFirstExpected,
                    "Nobody is waiting on this work, yet it left the local routes while one was eligible.",
                    selected.Key, local.Key));
            }
        }
        else if (demand.IsComplexOrHighStakes)
        {
            if (!IsFrontierClass(selected, observation.FunctionType)
                && comparable.FirstOrDefault(route => IsFrontierClass(route, observation.FunctionType)) is { } frontier)
            {
                violations.Add(new(FrontierRouteExpected,
                    $"{demand.Class} work someone is waiting on was served below the frontier bar while a frontier-class route was eligible"
                    + (selected.IsLocal ? " (a local route's categorical catalog prior is not frontier evidence)." : "."),
                    selected.Key, frontier.Key));
            }
        }
        else if (demand.ConversationalAssistant)
        {
            var selectedIsHealthySubscription = selected.IsSubscription && !selected.SubscriptionPressured;
            if (!selectedIsHealthySubscription
                && comparable.FirstOrDefault(route => route.IsSubscription && !route.SubscriptionPressured) is { } subscription)
            {
                violations.Add(new(SubscriptionFirstExpected,
                    "A waiting assistant turn was served off-subscription while a healthy subscription route was eligible.",
                    selected.Key, subscription.Key));
            }
        }

        return violations;
    }

    public static bool IsFrontierClass(ModelScoutRouteFacts route, string functionType)
        => ModelScoutCandidateRanker.IsFrontierClass(
            route.CapabilityScore,
            route.IsLocal,
            route.EvidenceKind,
            route.EvidenceFunctionType,
            functionType);

    /// <summary>How a route's windows hold the whole request. Unknown windows are not judged.</summary>
    private enum Fit { AsIs = 0, Grows = 1, Compacts = 2 }

    private static Fit FitOf(LlmResolvedTaskDemand demand, ModelScoutRouteFacts route)
    {
        if (Overfills(demand, route.ContextCapacity)) return Fit.Compacts;
        return Overfills(demand, route.MaxContextTokens) ? Fit.Grows : Fit.AsIs;
    }

    private static bool Overfills(LlmResolvedTaskDemand demand, int? window)
        => window is > 0 && demand.RequiredContextTokens > window.Value * ContextHeadroomFraction;

    private static bool CallerOwnsRoute(ModelScoutRecommendationRequest request)
        => request.Weights is not null
           || !string.IsNullOrWhiteSpace(request.ExactModel)
           || !string.IsNullOrWhiteSpace(request.Provider)
           || request.ModelRequest?.PreferredModels.Count > 0;
}

/// <summary>
/// One realistic routing request a host issues, without its content.
/// <paramref name="MinimumDemand"/> is the least demand the router must see
/// for it; a lower class means host metadata (a surface, a declared
/// complexity) was lost on the way, which the demand rules alone cannot see
/// because they judge the demand that arrived.
/// </summary>
public sealed record ModelScoutRoutingScenario(
    string Id,
    string Description,
    string FunctionType,
    LlmInteractivity Interactivity,
    LlmPrivacyClass PrivacyClass,
    bool RequiresToolUse,
    int PromptTokens,
    int OutputTokens,
    LlmTaskDemand? Demand,
    LlmDemandClass MinimumDemand = LlmDemandClass.Bulk)
{
    /// <summary>A violation when the router saw less demand than this scenario carries.</summary>
    public ModelScoutRoutingViolation? CheckDemand(LlmResolvedTaskDemand resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        return resolved.Class >= MinimumDemand
            ? null
            : new ModelScoutRoutingViolation(
                ModelScoutRoutingPolicyCheck.DemandLost,
                $"The router saw {resolved.Class} demand for a call that carries at least {MinimumDemand}; host metadata was lost before routing.");
    }

    /// <summary>The router options a host would send for this scenario.</summary>
    public LlmRouterOptions ToRouterOptions(string? sessionId = null, CancellationToken ct = default) => new()
    {
        Interactivity = Interactivity,
        PrivacyClass = PrivacyClass,
        RequiresToolUse = RequiresToolUse,
        MaxTokens = OutputTokens,
        TaskDemand = Demand,
        SessionId = sessionId,
        RequestCancellationToken = ct,
    };

    /// <summary>A neutral prompt of the scenario's size; it carries no risk vocabulary.</summary>
    public string SyntheticPrompt()
        => "Routing scenario " + Id + ". Reply with OK.\n"
           + string.Concat(Enumerable.Repeat("filler ", Math.Max(0, PromptTokens * 4 / 7)));
}

/// <summary>
/// The standard routing matrix: the kinds of calls a communication host makes.
/// Hosts pass their own mapping for an evidence workspace (for Talavine, a
/// matter), so SwarmCore never names a host surface.
/// </summary>
public static class ModelScoutRoutingScenarios
{
    public const string AssistantRoutine = "assistant-routine-waiting";
    public const string AssistantEvidenceWorkspace = "assistant-evidence-workspace-waiting";
    public const string AssistantOversizedEvidence = "assistant-oversized-evidence-waiting";
    public const string LongFormRewrite = "long-form-rewrite-waiting";
    public const string EvidenceDigest = "evidence-digest-waiting";
    public const string BackgroundSummary = "summary-background";
    public const string BackgroundOversizedDocument = "summary-background-oversized";
    public const string UnattendedAgent = "agent-unattended";
    public const string LocalOnlyAssistant = "assistant-local-only";

    public static IReadOnlyList<ModelScoutRoutingScenario> Standard(LlmTaskDemand? evidenceWorkspaceDemand = null)
    {
        var workspace = evidenceWorkspaceDemand ?? new LlmTaskDemand
        {
            Surface = LlmWorkSurface.EvidenceWorkspace,
            Complexity = LlmTaskComplexity.HighStakes,
            Signals = ["evidence-workspace"],
        };
        var authored = new LlmTaskDemand
        {
            Surface = LlmWorkSurface.EvidenceWorkspace,
            Complexity = LlmTaskComplexity.Complex,
            Signals = ["authored-brief"],
        };
        // An agent turn's own contribution (see the tool-use engine): the
        // system prompt and request cannot be compacted; one tool round is expected.
        LlmTaskDemand Turn(LlmTaskDemand? host, int irreducible)
            => LlmTaskDemand.Merge(host, new LlmTaskDemand
            {
                IrreducibleInputTokens = irreducible,
                ExpectedToolResultTokens = 6_000,
            })!;

        return
        [
            new(AssistantRoutine, "A person asks the assistant a quick question.",
                FunctionTypes.AgentChat, LlmInteractivity.UserWaiting, LlmPrivacyClass.CloudOk,
                RequiresToolUse: true, PromptTokens: 6_000, OutputTokens: 500, Turn(null, 3_000),
                LlmDemandClass.InteractiveRoutine),
            new(AssistantEvidenceWorkspace, "A person asks about a dispute inside an evidence workspace.",
                FunctionTypes.AgentChat, LlmInteractivity.UserWaiting, LlmPrivacyClass.CloudOk,
                RequiresToolUse: true, PromptTokens: 17_000, OutputTokens: 500, Turn(workspace, 17_000),
                LlmDemandClass.InteractiveHighStakes),
            new(AssistantOversizedEvidence, "An evidence-workspace turn whose history and results outgrew a small window.",
                FunctionTypes.AgentChat, LlmInteractivity.UserWaiting, LlmPrivacyClass.CloudOk,
                RequiresToolUse: true, PromptTokens: 40_000, OutputTokens: 500, Turn(workspace, 20_000),
                LlmDemandClass.InteractiveHighStakes),
            new(LongFormRewrite, "A person asks for a long-form brief rewrite.",
                FunctionTypes.Summarization, LlmInteractivity.UserWaiting, LlmPrivacyClass.CloudOk,
                RequiresToolUse: false, PromptTokens: 6_000, OutputTokens: 1_600, authored,
                LlmDemandClass.InteractiveComplex),
            new(EvidenceDigest, "One source digest inside a brief a person is waiting for.",
                FunctionTypes.Summarization, LlmInteractivity.UserWaiting, LlmPrivacyClass.CloudOk,
                RequiresToolUse: false, PromptTokens: 3_000, OutputTokens: 900, authored,
                LlmDemandClass.InteractiveComplex),
            new(BackgroundSummary, "Automatic brief upkeep nobody is waiting for.",
                FunctionTypes.Summarization, LlmInteractivity.Background, LlmPrivacyClass.CloudOk,
                RequiresToolUse: false, PromptTokens: 6_000, OutputTokens: 1_600, null),
            // A plain prompt cannot be compacted: a window smaller than the whole
            // document would silently drop part of it, so such a route must not serve.
            new(BackgroundOversizedDocument, "Automatic upkeep of a document larger than a small local window.",
                FunctionTypes.Summarization, LlmInteractivity.Background, LlmPrivacyClass.CloudOk,
                RequiresToolUse: false, PromptTokens: 40_000, OutputTokens: 1_600, null),
            new(UnattendedAgent, "An unattended repair or scheduled agent turn.",
                FunctionTypes.AgentChat, LlmInteractivity.Background, LlmPrivacyClass.CloudOk,
                RequiresToolUse: true, PromptTokens: 20_000, OutputTokens: 500, Turn(null, 4_000)),
            new(LocalOnlyAssistant, "A person asks with a LocalOnly privacy requirement.",
                FunctionTypes.AgentChat, LlmInteractivity.UserWaiting, LlmPrivacyClass.LocalOnly,
                RequiresToolUse: true, PromptTokens: 6_000, OutputTokens: 500, Turn(null, 3_000),
                LlmDemandClass.InteractiveRoutine),
        ];
    }
}
