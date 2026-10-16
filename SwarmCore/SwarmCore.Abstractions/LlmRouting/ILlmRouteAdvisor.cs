namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Context supplied to application-specific route advisors before the router
/// commits to a function preference. Advisors can recommend a better route for
/// load-bearing functions without putting application-specific benchmark logic
/// inside SwarmCore.
/// </summary>
public sealed class LlmRouteAdvisorContext
{
    /// <summary>Collect workload constraints for one central Scout decision.</summary>
    public bool ConstraintsOnly { get; init; }
    public required string FunctionType { get; init; }
    public required LlmCallContext CallContext { get; init; }
    public LlmFunctionPreference? CurrentPreference { get; init; }
    public string? PromptText { get; init; }
    /// <summary>
    /// Structured latest user-authored text when the calling surface can provide
    /// it. Safety advisors should prefer this over flattened prompt summaries.
    /// </summary>
    public string? UserRequestText { get; init; }
    /// <summary>
    /// Tool ids exposed by the tool-use router for this provider call. The
    /// router derives this from the typed tool definitions; it is not parsed
    /// from prompt text. Safety advisors can use it to distinguish an app
    /// control-plane turn from similarly worded user-data work.
    /// </summary>
    public IReadOnlyCollection<string> AvailableToolIds { get; init; } = [];
    /// <summary>
    /// Tool ids that completed successfully after the latest user message.
    /// Approval-gated tools appear here only after the permission broker
    /// approved and executed them.
    /// </summary>
    public IReadOnlyCollection<string> CompletedToolIds { get; init; } = [];
    /// <summary>
    /// Agent turn identity supplied by the calling surface, when there is one.
    /// Advisors use it only to attach per-turn notices (see
    /// <see cref="ILlmDegradedRouteNoticeSink"/>) that the agent engine can
    /// surface to the user alongside the answer.
    /// </summary>
    public string? TurnId { get; init; }
    /// <summary>
    /// Conversation identity supplied by the calling surface, when there is one
    /// (the router's <c>SessionId</c>). Unlike <see cref="TurnId"/>, an
    /// application knows this before it sends, so a policy that belongs to a
    /// whole workspace rather than one question can be declared against it.
    /// </summary>
    public string? SessionId { get; init; }
    /// <summary>
    /// Host-declared demand for the call (surface, complexity). Safety advisors
    /// may treat a declared evidence workspace or high-stakes demand as risk
    /// without matching words in the message.
    /// </summary>
    public LlmTaskDemand? TaskDemand { get; init; }
    public bool RequiresVision { get; init; }
    public bool RequiresToolUse { get; init; }
    public bool RequiresStructuredOutput { get; init; }
    /// <summary>
    /// True only when the caller owns a strict typed parse/repair boundary and
    /// therefore does not require provider-native structured-output support.
    /// Advisors must carry this into every Model Scout request they derive.
    /// </summary>
    public bool StructuredOutputRepairAvailable { get; init; }
    public int? MinimumContextTokens { get; init; }
    public LocalLlmWorkloadKind WorkloadKind { get; init; } = LocalLlmWorkloadKind.Text;

    /// <summary>
    /// Approximate prompt size supplied by the router before provider selection.
    /// Advisors use this as a coarse cost/latency signal; it is not a billing token count.
    /// </summary>
    public int PromptTokenEstimate { get; init; }

    /// <summary>
    /// Approximate maximum response size requested by the caller. Advisors must
    /// forward this value to Model Scout so route ranking reflects the actual
    /// workload instead of silently falling back to Scout's 500-token default.
    /// </summary>
    public int EstimatedOutputTokens { get; init; } = 500;

    /// <summary>
    /// Host machine state at routing time. Lets advisors shift based on time-of-day, idle
    /// state, or power source. Defaults to <see cref="SystemActivitySnapshot.Active"/>.
    /// </summary>
    public SystemActivitySnapshot SystemActivity { get; init; } = SystemActivitySnapshot.Active();

    /// <summary>
    /// User-configured routing policy (budget caps, quiet hours, urgency knobs). Defaults to
    /// <see cref="RoutingPolicy.Default"/> when the user hasn't configured anything.
    /// </summary>
    public RoutingPolicy Policy { get; init; } = RoutingPolicy.Default;

    /// <summary>
    /// Current cloud-API spend vs. the configured cap. Read by the budget-pressure advisor.
    /// </summary>
    public BudgetPressureSnapshot BudgetPressure { get; init; } = BudgetPressureSnapshot.Unconstrained;
}

/// <summary>
/// A route recommendation returned by an <see cref="ILlmRouteAdvisor"/>.
/// </summary>
public sealed class LlmRouteAdvisorRecommendation
{
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public string? FallbackProvider { get; init; }
    public string? FallbackModel { get; init; }
    public bool RequiresVision { get; init; }
    public string? Reason { get; init; }

    /// <summary>
    /// Correlation id of the Model Scout recommendation that produced this
    /// advised route. The router carries it into selection and execution audit
    /// rows so the entire decision chain is joinable.
    /// </summary>
    public string? AuditCorrelationId { get; init; }
    public bool? SupportsToolUse { get; init; }
    public bool? SupportsStructuredOutput { get; init; }
    public int? MaxContextTokens { get; init; }
    public string? CapabilityEvidenceSource { get; init; }

    /// <summary>
    /// Optional hard capability floor that must survive the router's independent
    /// exact-route re-admission. This is a gate, not a scoring preference.
    /// </summary>
    public decimal? MinimumCapabilityScore { get; init; }
    public bool RequireMeasuredCapabilityScore { get; init; }

    /// <summary>
    /// Requires the router's independent exact-route re-admission to carry
    /// current proof for this function, even when the function is not globally
    /// configured as a strict-proof lane.
    /// </summary>
    public bool RequireCurrentFunctionEvidence { get; init; }

    /// <summary>
    /// Optional Scout tier that must survive independent route re-admission.
    /// </summary>
    public ModelScoutTier? RequiredTier { get; init; }

    /// <summary>
    /// When true, this policy has positively matched the call and lower-priority
    /// policies must not run if its route fails independent re-admission.
    /// </summary>
    public bool BlockWeakerPolicyFallback { get; init; }

    /// <summary>
    /// Explicit last-resort signal from a hard-safety advisor that already
    /// searched for a stronger proven route and is intentionally returning the
    /// best reachable route so the user still receives an answer. The router
    /// honors this only for an actual <see cref="ILlmHardSafetyRouteAdvisor"/>
    /// candidate with every proof/quality gate dropped, and still independently
    /// enforces provider availability, privacy, and request capabilities.
    /// </summary>
    public bool AllowDegradedExecutionWithoutCurrentScoutProof { get; init; }

    /// <summary>
    /// When true, the router writes the recommendation into function
    /// preferences so later calls and settings UI see the same route.
    /// </summary>
    public bool PersistPreference { get; init; } = true;

    /// <summary>
    /// Allows a successful default-context execution to initialize a truly
    /// missing/blank function preference even when this dynamic advisor does not
    /// overwrite configured preferences. Use only for routes that are safe as a
    /// stale-while-revalidate startup cache; time/urgency/budget overrides remain
    /// transient.
    /// </summary>
    public bool AllowSuccessfulBootstrapMaterialization { get; init; }

    public string? UpdatedBy { get; init; }

    /// <summary>
    /// Provider execution hints to merge into the call. Caller-supplied
    /// <see cref="LlmRouterOptions.ExecutionSettings"/> still win.
    /// </summary>
    public Dictionary<string, object>? ExecutionSettings { get; init; }
}

/// <summary>
/// Optional application hook for dynamic, benchmark-aware routing decisions.
/// The core router owns invocation, validation, persistence, and local runtime
/// admission; applications own the domain-specific evidence behind a recommendation.
/// </summary>
public interface ILlmRouteAdvisor
{
    Task<LlmRouteAdvisorRecommendation?> GetRecommendationAsync(
        LlmRouteAdvisorContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Signals that an applicable route policy found no candidate satisfying a
/// non-negotiable per-call safety floor. Routing coordinators must propagate
/// this exception instead of silently falling through to a weaker policy.
/// </summary>
public sealed class LlmRouteSelectionBlockedException : InvalidOperationException
{
    public LlmRouteSelectionBlockedException(string message)
        : base(message)
    {
    }

    public LlmRouteSelectionBlockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Signals that automatic routing is temporarily unable to admit a route while
/// provider/catalog capability discovery or Model Scout recovery is still in
/// progress. Background callers may retry this condition with a bounded,
/// coalesced delay. It must not be used for explicit user pins, privacy gates,
/// spend-policy denials, or other durable policy failures.
/// </summary>
public sealed class LlmRouteTemporarilyUnavailableException : InvalidOperationException
{
    public LlmRouteTemporarilyUnavailableException(string message)
        : base(message)
    {
    }

    public LlmRouteTemporarilyUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
