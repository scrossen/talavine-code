namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// A once-only notification that the router is about to start physical model
/// execution. The same instance follows cloned options through retries and
/// fallbacks, so caller deadlines can switch from routing time to provider time
/// without granting a fresh budget for every attempt.
/// </summary>
public sealed class LlmProviderExecutionStartCallback
{
    private readonly Action _callback;
    private int _invoked;

    public LlmProviderExecutionStartCallback(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _callback = callback;
    }

    /// <summary>True after the first route reaches the physical execution boundary.</summary>
    public bool HasStarted => Volatile.Read(ref _invoked) != 0;

    /// <summary>
    /// Invoke the callback at most once. False means another attempt already
    /// started execution and must share its remaining deadline.
    /// </summary>
    public bool TryInvoke()
    {
        if (Interlocked.CompareExchange(ref _invoked, 1, 0) != 0)
            return false;

        _callback();
        return true;
    }
}

/// <summary>
/// Provenance of an exact provider/model override. Override fields alone do not
/// imply a user choice: calibration, policy, retries, and tool loops all pin an
/// execution route internally.
/// </summary>
public enum LlmOverrideProvenance
{
    Unspecified = 0,
    User = 1,
    Profile = 2,
    Policy = 3,
    Calibration = 4,
    Benchmark = 5,
    Fallback = 6,
    SelfHeal = 7,
    /// <summary>
    /// The host's dedicated proof authority admitted this exact route and supplied
    /// a receipt that the router must revalidate. This never means "trust the
    /// caller": without a registered <see cref="ILlmRouteAdmissionValidator"/>
    /// and an exact valid receipt, selection fails closed.
    /// </summary>
    ValidatedAdmission = 8,
    /// <summary>Exact generation-scoped embedding rebuild or repair execution.</summary>
    EmbeddingRebuild = 9,
}

/// <summary>
/// The route-selection semantics under which a host authority issued an
/// admission receipt. Revalidation must preserve this context: an automatic
/// policy route may legitimately cross a soft quota threshold when no proven
/// alternative exists, while an exact user route must remain exact.
/// </summary>
public enum LlmRouteAdmissionMode
{
    ExactRoute = 0,
    PolicySelectedRoute = 1,
}

/// <summary>
/// Policy lane used when a host authority selected an automatic route. The lane
/// is part of admission provenance because routine and priority work can use
/// different scorers. Exact routes retain it for audit continuity, although an
/// exact revalidation never uses it to substitute another route.
/// </summary>
public enum LlmRouteAdmissionPolicyLane
{
    Routine = 0,
    Priority = 1,
}

/// <summary>
/// Privacy-safe evidence from a host-specific route proof authority. The router
/// does not interpret prompt/suite identities; it requires the host validator to
/// confirm that the receipt is current and bound to the exact function and route.
/// </summary>
public sealed record LlmRouteAdmissionReceipt(
    string FunctionType,
    string Provider,
    string Model,
    string Source,
    string PromptSha256,
    string SuiteSha256,
    string? ProofIdentitySha256,
    DateTime EvaluatedAtUtc,
    DateTime? ExpiresAtUtc)
{
    /// <summary>
    /// Selection semantics used by the host proof authority. The default is
    /// exact for backward-safe fail-closed behavior; automatic policy callers
    /// must opt in explicitly when creating the receipt.
    /// </summary>
    public LlmRouteAdmissionMode AdmissionMode { get; init; } = LlmRouteAdmissionMode.ExactRoute;

    /// <summary>
    /// Scoring lane used by the admitting host policy. Automatic revalidation
    /// must use the same lane so a priority route is not silently judged under
    /// routine backlog policy.
    /// </summary>
    public LlmRouteAdmissionPolicyLane PolicyLane { get; init; } = LlmRouteAdmissionPolicyLane.Routine;

    /// <summary>
    /// One-call spend lease for a metered message-pipeline route. The host
    /// validator atomically claims it; replay or a different exact route fails.
    /// Zero-cost routes leave it null.
    /// </summary>
    public Guid? MeteredReservationId { get; init; }
}

/// <summary>
/// Result of revalidating a host admission receipt at the router boundary.
/// Affirmative capability values are exact-route evidence supplied by that same
/// authority; unknown values remain null and cannot satisfy a hard requirement.
/// </summary>
public sealed record LlmRouteAdmissionValidation(
    bool IsValid,
    string Reason,
    bool? SupportsVision = null,
    bool? SupportsToolUse = null,
    bool? SupportsStructuredOutput = null,
    int? MaxContextTokens = null,
    string? CapabilityEvidenceSource = null,
    LlmRouteAdmissionReceipt? ValidatedReceipt = null,
    decimal? CapabilityScore = null,
    ModelScoutCapabilityEvidenceKind CapabilityEvidenceKind = ModelScoutCapabilityEvidenceKind.Unknown);

/// <summary>
/// Host seam for exact, proof-bound routes whose domain-specific admission gate
/// is stricter than the generic Model Scout evidence lane.
/// </summary>
public interface ILlmRouteAdmissionValidator
{
    Task<LlmRouteAdmissionValidation> ValidateAsync(
        LlmRouteAdmissionReceipt receipt,
        CancellationToken ct = default);
}

/// <summary>
/// The router rejected a host-issued admission before any provider invocation.
/// Hosts may use this typed, pre-execution signal for one bounded fresh
/// re-admission attempt; ordinary provider and response failures must not be
/// retried under this contract.
/// </summary>
public sealed class LlmRouteAdmissionRejectedException(
    string functionType,
    string provider,
    string model,
    string message) : InvalidOperationException(message)
{
    public string FunctionType { get; } = functionType;
    public string Provider { get; } = provider;
    public string Model { get; } = model;
}

/// <summary>
/// Privacy-safe completion facts for one physical provider call. Hosts may use
/// this hook for transactional spend reservations or other fail-closed runtime
/// accounting without receiving the prompt or response body.
/// </summary>
public sealed record LlmUsageCompletion(
    string FunctionType,
    string Provider,
    string Model,
    int InputTokens,
    int OutputTokens,
    decimal EstimatedCost,
    bool WasSuccessful,
    bool WasDurablyRecorded,
    bool MeasurementComplete = false)
{
    public long? UsageId { get; init; }
    public Guid? PhysicalCallId { get; init; }
    public string? RoutingAuditCorrelationId { get; init; }
    public int? ResponseTimeMs { get; init; }
}

/// <summary>
/// Shared bounds for the router's one same-route structured-output retry.
/// Budget admission code must use these values so a retry cannot outgrow its
/// pre-call spend reservation.
/// </summary>
public static class LlmJsonRetryLimits
{
    public const int MinimumOutputTokens = 4_096;
    public const int MaximumOutputTokens = 8_192;
}

/// <summary>
/// Router-owned execution settings that providers may inspect but callers may
/// not override. These keys are control-plane metadata, not provider payload.
/// </summary>
public static class LlmReservedExecutionSettings
{
    public const string ExactModelOnly = "__swarmcore_exact_model_only";
    public const string PhysicalRequestBudget = "__swarmcore_physical_request_budget";
    public static LlmPhysicalRequestBudget? GetPhysicalRequestBudget(IReadOnlyDictionary<string, object>? settings)
    {
        if (settings is null || !settings.TryGetValue(PhysicalRequestBudget, out var value)) return null;
        return value as LlmPhysicalRequestBudget
            ?? throw new InvalidOperationException("Physical inference request budget must retain its typed request-owned identity.");
    }
    public const string RoutingFunctionType = "__swarmcore_routing_function_type";
    public const string RequestCancellationToken = "__swarmcore_request_cancellation_token";
    public const string RequestPrivacyClass = "__swarmcore_request_privacy_class";
    public const string ExpectedModelArtifactDigest = "__swarmcore_expected_model_artifact_digest";
    public const string ExecutionParameterDiagnostics = "__swarmcore_execution_parameter_diagnostics";
    /// <summary>
    /// Host-owned project folder for a subscription-CLI route used as a plain model on behalf of a coding
    /// workspace (a coding lane or its completion audit). A CLI whose print-mode contract loads no project
    /// settings runs in it, so the model never mistakes the CLI's private scratch folder for the project.
    /// Never set from model output.
    /// </summary>
    public const string SubscriptionCliWorkingDirectory = "__swarmcore_subscription_cli_working_directory";

    public static IReadOnlyList<LlmExecutionParameterDiagnostic> GetExecutionParameterDiagnostics(
        IReadOnlyDictionary<string, object>? settings)
        => settings is not null
           && settings.TryGetValue(ExecutionParameterDiagnostics, out var value)
           && value is IReadOnlyList<LlmExecutionParameterDiagnostic> diagnostics
            ? diagnostics
            : Array.Empty<LlmExecutionParameterDiagnostic>();

    public static string? GetExpectedModelArtifactDigest(
        IReadOnlyDictionary<string, object>? settings)
        => settings is not null
           && settings.TryGetValue(ExpectedModelArtifactDigest, out var value)
            ? EmbeddingModelIdentity.NormalizeDigest(value?.ToString())
            : null;

    public static bool RequiresExactModel(IReadOnlyDictionary<string, object>? settings)
    {
        if (settings is null || !settings.TryGetValue(ExactModelOnly, out var value))
        {
            return false;
        }

        return value switch
        {
            bool flag => flag,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => false,
        };
    }

    public static CancellationToken GetRequestCancellationToken(
        IReadOnlyDictionary<string, object>? settings)
        => settings is not null
           && settings.TryGetValue(RequestCancellationToken, out var value)
           && value is CancellationToken token
            ? token
            : CancellationToken.None;

    public static LlmPrivacyClass GetRequestPrivacyClass(
        IReadOnlyDictionary<string, object>? settings)
    {
        if (settings is null || !settings.TryGetValue(RequestPrivacyClass, out var value))
        {
            return LlmPrivacyClass.Unspecified;
        }

        return value switch
        {
            LlmPrivacyClass privacy => privacy,
            int numeric when Enum.IsDefined(typeof(LlmPrivacyClass), numeric) =>
                (LlmPrivacyClass)numeric,
            string text when Enum.TryParse<LlmPrivacyClass>(text, true, out var parsed) => parsed,
            _ => LlmPrivacyClass.Unspecified,
        };
    }
}

/// <summary>
/// Options for LLM router requests. This is the database-agnostic version
/// of routing options that can be used across different applications.
/// </summary>
public class LlmRouterOptions
{
    /// <summary>
    /// Shallow copy for a side call that must route like the original request (pins,
    /// identity, generation settings) without sharing its per-turn callbacks.
    /// </summary>
    public LlmRouterOptions Clone() => (LlmRouterOptions)MemberwiseClone();

    /// <summary>Ordered model preferences and hard capabilities, resolved exclusively by Model Scout.</summary>
    public LlmModelRequest? ModelRequest { get; set; }
    /// <summary>Versioned application execution topology for usage/proof attribution.</summary>
    public string? StrategyId { get; set; }
    public Dictionary<string, object>? ExecutionSettings { get; set; }
    /// <summary>
    /// Provider-neutral generation parameters. This is the authoritative path
    /// for common sampling/output behavior. <see cref="ExecutionSettings"/> is
    /// retained as a narrow compatibility/experimental escape hatch.
    /// </summary>
    public LlmGenerationParameters? GenerationParameters { get; set; }
    /// <summary>
    /// Physical request controls, kept separate from generation semantics.
    /// Router-owned exact-model/proof/privacy/scheduling values win over this
    /// object when the two disagree.
    /// </summary>
    public LlmRequestControl? RequestControl { get; set; }
    /// <summary>
    /// Cancels the physical provider request. This is separate from selection and
    /// fallback policy so runtime-maintenance work can yield without treating a
    /// scheduling cancellation as model failure.
    /// </summary>
    public CancellationToken RequestCancellationToken { get; set; }
    /// <summary>
    /// Optional once-only signal invoked immediately before the first physical
    /// provider call. Selection, retries, and fallback clones share the same
    /// signal; they cannot restart a caller's provider-execution deadline.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public LlmProviderExecutionStartCallback? ProviderExecutionStartCallback { get; set; }
    public string? UserId { get; set; }
    public string? SessionId { get; set; }
    /// <summary>
    /// Optional application turn identifier used to correlate recommendation,
    /// selection, and execution audit records for one user-visible turn.
    /// </summary>
    public string? TurnId { get; set; }
    /// <summary>
    /// Latest user-authored request text supplied separately from flattened
    /// system/transcript summaries for per-call safety routing. It must never be
    /// copied into routing audit evidence; audit retains only the prompt hash.
    /// </summary>
    public string? RoutingUserText { get; set; }
    /// <summary>
    /// Router-owned typed tool ids available for the current tool-use call.
    /// Tool-use entry points overwrite this from <see cref="ChatToolUseRequest.Tools"/>
    /// so prompt text cannot manufacture control-plane authority.
    /// </summary>
    public IReadOnlyCollection<string> RoutingAvailableToolIds { get; set; } = [];
    /// <summary>
    /// Router-owned tool ids with a successful result after the latest user
    /// message. Approval-gated controls are included only after execution.
    /// </summary>
    public IReadOnlyCollection<string> RoutingCompletedToolIds { get; set; } = [];
    /// <summary>
    /// Optional host callback invoked after the router has measured a provider
    /// call. The callback carries no prompt or response text. A failed callback
    /// never makes a completed provider call look unspent; callers that reserve
    /// budget should leave their reservation conservatively committed.
    /// </summary>
    public Func<LlmUsageCompletion, Task>? UsageCompletionObserver { get; set; }
    public bool ForceRefresh { get; set; } = false;
    public string? OverrideProvider { get; set; }
    public string? OverrideModel { get; set; }
    public LlmOverrideProvenance OverrideProvenance { get; set; } = LlmOverrideProvenance.Unspecified;
    /// <summary>
    /// Exact vector generation authorized by the embedding rebuild orchestrator.
    /// Required for <see cref="LlmOverrideProvenance.EmbeddingRebuild"/>.
    /// </summary>
    public Guid? EmbeddingGenerationId { get; set; }
    /// <summary>
    /// Required when <see cref="OverrideProvenance"/> is
    /// <see cref="LlmOverrideProvenance.ValidatedAdmission"/>. It is copied into
    /// selection audit and revalidated before any provider call.
    /// </summary>
    public LlmRouteAdmissionReceipt? RouteAdmissionReceipt { get; set; }
    public int? MaxTokens { get; set; }
    /// <summary>
    /// Removes output-token ceilings inherited from route/provider settings and
    /// prevents JSON recovery from adding one. Use only for workloads where a
    /// provider limit is known to truncate hidden reasoning before any visible
    /// response (for example local chat segmentation). This takes precedence
    /// over <see cref="MaxTokens"/> and execution settings.
    /// </summary>
    public bool SuppressOutputTokenLimit { get; set; }
    public float? Temperature { get; set; }
    /// <summary>
    /// Optional expected format hint for the response (e.g., "json", "html", "text").
    /// </summary>
    public string? ExpectedFormat { get; set; }
    /// <summary>
    /// Optional: identify the originating entity type for this request (e.g., "supporttickets")
    /// </summary>
    public string? SourceType { get; set; }
    /// <summary>
    /// Optional: identify the originating entity ID for this request (e.g., "811")
    /// </summary>
    public string? SourceId { get; set; }
    /// <summary>
    /// Low-level JSON-mode switch used by the router's typed JSON boundary.
    /// Production callers should prefer <see cref="ILlmRouter.GenerateResponseAsync{T}"/>
    /// so JSON extraction, validation, repair, and fallback stay centralized.
    /// </summary>
    public bool ForceJson { get; set; } = false;


    /// <summary>
    /// Skip the router's one same-route structured-output reprompt. Use only
    /// when the application topology can safely isolate an invalid optional
    /// specialist result without failing or substituting the selected route.
    /// The normal fallback policy still applies after the quality failure.
    /// </summary>
    public bool SuppressStructuredOutputRetry { get; set; }

    /// <summary>
    /// Optional caller-owned, deterministic validation for non-JSON response
    /// contracts. The callback receives only the completed response and must do
    /// no I/O; return false when the response is not a complete usable artifact.
    /// Validator exceptions are treated as rejection and never copied to logs.
    /// </summary>
    public Func<string, bool>? ResponseContentValidator { get; set; }

    /// <summary>
    /// When the caller validator rejects a nonempty response, record that
    /// physical execution as failed and return the contract error without
    /// automatically spending another full generation on a fallback. Provider
    /// launch/auth/rate-limit failures still follow the normal fallback policy.
    /// </summary>
    public bool SuppressFallbackOnResponseContentValidationFailure { get; set; }

    /// <summary>
    /// Require automatic routing to ask Model Scout to rank the current request
    /// using its actual input/output-size estimates. This does not add a new
    /// qualification gate or change global model Pass/Fail authority; it only
    /// prevents a stale automatic function preference, survival-set receipt, or
    /// provider default from standing in for workload-sensitive ranking. Exact
    /// user/profile model choices remain sovereign.
    /// </summary>
    public bool RequireCurrentWorkloadSizing { get; set; }

    /// <summary>
    /// When true, model selection must land on a vision-capable model (see
    /// <see cref="ILlmCapabilitiesService.SupportsVision"/>), swapping away from a
    /// configured non-vision preference if needed. Null/false leaves selection
    /// unconstrained by vision support.
    /// </summary>
    public bool? RequiresVision { get; set; }

    /// <summary>
    /// Require a route with affirmative model-level tool-use evidence. Native
    /// tool-use router calls set this automatically; ordinary callers normally
    /// leave it false.
    /// </summary>
    public bool RequiresToolUse { get; set; }

    /// <summary>
    /// Require measured or catalog-declared structured-output support.
    /// Typed JSON calls set this automatically.
    /// </summary>
    public bool RequiresStructuredOutput { get; set; }

    /// <summary>
    /// Allow a route without provider-native structured-output support when the
    /// caller uses the router's typed JSON boundary (or an equivalently strict
    /// parser) and accepts its validated same-route repair pass. This does not
    /// weaken JSON validation: malformed output still fails closed. Leave false
    /// for callers that specifically require a provider-enforced schema.
    /// </summary>
    public bool AllowTextJsonContractRepair { get; set; }

    /// <summary>
    /// Optional hard minimum context-window size for recommendation decisions.
    /// Unknown context size is rejected when this is set.
    /// </summary>
    public int? MinimumContextTokens { get; set; }

    /// <summary>
    /// When true, the router may still retry/repair the selected route, but it
    /// must not switch to another provider/model. Use for benchmarks where a
    /// fallback would contaminate the candidate's score.
    /// </summary>
    public bool DisableFallback { get; set; } = false;

    /// <summary>
    /// When true, the router may discover and use ANY enabled alternate
    /// provider on failure — even when the selected route has no configured
    /// <see cref="LlmModelSelection.FallbackProvider"/>, isn't a
    /// subscription-CLI provider, and the failure reason is a plain
    /// "provider_error" (e.g. a raw HTTP 400). Without this flag, discovered
    /// fallback for a non-default, non-vision, non-subscription-CLI route is
    /// gated off by design (see <c>ShouldAllowDiscoveredFallbackForRuntimeFailure</c>)
    /// so batch/pipeline callers fail fast rather than silently substitute a
    /// possibly-lower-quality model. Interactive chat surfaces where showing
    /// *some* working answer matters more than pinning to one provider
    /// (agent chat turns, regardless of channel — desktop, Telegram, mobile)
    /// may set this for automatically selected routes. Explicit User/Profile,
    /// provider-constrained, and user-pinned function routes ignore this escape
    /// hatch; they may use only an explicitly configured fallback.
    /// </summary>
    public bool AllowAnyProviderFallback { get; set; } = false;

    /// <summary>
    /// Whether a user is waiting on the response or this is background work. Unspecified
    /// (default) means "use the function-type profile" — see <see cref="FunctionTypeProfiles"/>.
    /// </summary>
    public LlmInteractivity Interactivity { get; set; } = LlmInteractivity.Unspecified;

    public LlmWorkloadKind Workload { get; set; } = LlmWorkloadKind.Unspecified;

    /// <summary>
    /// Host-declared demand for this call: the surface it serves, how complex
    /// or consequential it is, and the context it will need. Combined with
    /// <see cref="Interactivity"/> and the call's size by
    /// <see cref="LlmTaskDemandPolicy"/>. It shapes ranking only and never
    /// relaxes privacy, pins, capability, or spend constraints.
    /// </summary>
    public LlmTaskDemand? TaskDemand { get; set; }

    /// <summary>
    /// Whether the prompt may leave the machine. <see cref="LlmPrivacyClass.LocalOnly"/> forces
    /// the router to a local provider; cloud models are skipped.
    /// </summary>
    public LlmPrivacyClass PrivacyClass { get; set; } = LlmPrivacyClass.Unspecified;

    /// <summary>
    /// How aggressively to spend on this call. <see cref="LlmUrgency.Unspecified"/> (default)
    /// uses the function-type profile. <see cref="LlmUrgency.Urgent"/> tells the dynamic
    /// advisors to promote a tier; <see cref="LlmUrgency.Batch"/> tells them they may demote.
    /// </summary>
    public LlmUrgency Urgency { get; set; } = LlmUrgency.Unspecified;

    /// <summary>
    /// Constraint envelope carried across automatic fallback and self-heal.
    /// These fields can only strengthen route admission; every substitute is
    /// independently re-admitted by Model Scout at the same floor.
    /// </summary>
    public decimal? MinimumCapabilityScore { get; set; }
    public bool RequireMeasuredCapabilityScore { get; set; }
    public ModelScoutTier? RequiredTier { get; set; }
    public bool BlockWeakerPolicyFallback { get; set; }
    public bool RequireCurrentFunctionEvidence { get; set; }
}

/// <summary>
/// Result of model selection
/// </summary>
public class LlmModelSelection
{
    public EmbeddingModelIdentity? EmbeddingIdentity { get; set; }
    public required string Provider { get; set; }
    public required string Model { get; set; }
    public string? FallbackProvider { get; set; }
    public string? FallbackModel { get; set; }
    public bool IsDefault { get; set; }
    public string? Reason { get; set; }
    public Dictionary<string, object>? ExecutionSettings { get; set; }

    /// <summary>
    /// Whether a successful execution of this transient selection may initialize a
    /// missing/blank durable function preference. Route advisors set this to false
    /// for per-call recommendations whose <c>PersistPreference</c> contract is false.
    /// Selection alone is never proof that a route is a usable startup cache.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool AllowPreferenceMaterialization { get; set; } = true;

    /// <summary>Resolved per-call routing context (interactivity × privacy). Set by the router.</summary>
    public LlmCallContext? Context { get; set; }

    /// <summary>
    /// Privacy-safe correlation key shared by the selection audit record and the
    /// eventual usage row. It is generated by the router and is not a prompt id.
    /// </summary>
    public string? AuditCorrelationId { get; set; }
    /// <summary>True only when the effective primary route came from an explicit user pin/override.</summary>
    public bool IsUserPinned { get; set; }
    public bool? SupportsToolUse { get; set; }
    public bool? SupportsStructuredOutput { get; set; }
    public int? MaxContextTokens { get; set; }
    public string? CapabilityEvidenceSource { get; set; }
    public decimal? CapabilityScore { get; set; }
    public ModelScoutCapabilityEvidenceKind CapabilityEvidenceKind { get; set; } =
        ModelScoutCapabilityEvidenceKind.Unknown;
    /// <summary>
    /// Compact capability/speed/cost evidence for the actual Scout-selected route.
    /// The router persists it on the selection row even when the full recommendation
    /// audit was intentionally suppressed.
    /// </summary>
    public ModelScoutRuntimeDecisionReceipt? ModelScoutDecisionReceipt { get; set; }
    public ModelScoutConstraintAdmission? ModelScoutConstraintAdmission { get; set; }
    public ModelScoutRuntimeDecisionReceipt? FallbackModelScoutDecisionReceipt { get; set; }
    /// <summary>Non-negotiable per-call capability floor carried from a matched policy.</summary>
    public decimal? MinimumCapabilityScore { get; set; }
    public bool RequireMeasuredCapabilityScore { get; set; }
    /// <summary>Scout tier carried from a matched per-call policy.</summary>
    public ModelScoutTier? RequiredTier { get; set; }
    /// <summary>Prevents runtime failover from dropping a matched safety floor.</summary>
    public bool BlockWeakerPolicyFallback { get; set; }
    /// <summary>
    /// Requires every automatic replacement to carry current evidence for this
    /// exact function, even when that function would normally admit a bounded
    /// provisional route while benchmark recovery continues.
    /// </summary>
    public bool RequireCurrentFunctionEvidence { get; set; }
    /// <summary>
    /// Receipt returned by the registered validator after successful revalidation.
    /// Selection audit uses this trusted copy, never the caller-supplied option.
    /// </summary>
    public LlmRouteAdmissionReceipt? ValidatedAdmissionReceipt { get; set; }
    public bool? FallbackSupportsToolUse { get; set; }
    public bool? FallbackSupportsStructuredOutput { get; set; }
    public int? FallbackMaxContextTokens { get; set; }
    public string? FallbackCapabilityEvidenceSource { get; set; }
    public decimal? FallbackCapabilityScore { get; set; }
    public ModelScoutCapabilityEvidenceKind FallbackCapabilityEvidenceKind { get; set; } =
        ModelScoutCapabilityEvidenceKind.Unknown;
    /// <summary>
    /// Correlation id for a separately proven fallback decision. When absent,
    /// fallback execution remains linked to the primary selection receipt.
    /// </summary>
    public string? FallbackAuditCorrelationId { get; set; }
}

/// <summary>
/// Cost summary for LLM usage reporting
/// </summary>
public class LlmCostSummary
{
    public decimal TotalCost { get; set; }
    public int TotalRequests { get; set; }
    public long TotalTokens { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public Dictionary<string, decimal> CostByFunction { get; set; } = new();
    public Dictionary<string, decimal> CostByModel { get; set; } = new();
    public Dictionary<string, int> RequestsByFunction { get; set; } = new();
    public Dictionary<string, long> TokensByModel { get; set; } = new();
}

/// <summary>
/// Cost breakdown by function or model
/// </summary>
public class LlmCostBreakdown
{
    public required string Category { get; set; }
    public decimal TotalCost { get; set; }
    public int RequestCount { get; set; }
    public long TotalTokens { get; set; }
    public decimal AverageCostPerRequest { get; set; }
    public decimal AverageTokensPerRequest { get; set; }
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
}

/// <summary>
/// Typed outcome for an exact provider/model validation. Deferred means a
/// process-owned provider operation from an older configuration is still
/// finishing; callers should retain one bounded trailing recovery request
/// rather than treating it as a model failure.
/// </summary>
public enum LlmModelValidationStatus
{
    Valid,
    Invalid,
    Unavailable,
    Deferred,
}

public sealed record LlmModelValidationResult(
    LlmModelValidationStatus Status,
    string Message,
    string ConfigurationIdentity)
{
    public bool IsValid => Status == LlmModelValidationStatus.Valid;
    public bool IsDeferred => Status == LlmModelValidationStatus.Deferred;
}

/// <summary>
/// Centralized AI model router interface. This is the main entry point for 
/// generating AI responses across any application using SwarmCore.
/// </summary>
public interface ILlmRouter
{
    /// <summary>
    /// Generates a response using the optimal model for the specified function
    /// </summary>
    Task<string> GenerateResponseAsync(string functionType, string prompt, LlmRouterOptions? options = null);

    /// <summary>
    /// Generates a vision response using the optimal model for the specified function
    /// </summary>
    Task<string> GenerateVisionResponseAsync(string functionType, string prompt, string imageBase64, 
        LlmRouterOptions? options = null, object? payloadMetadata = null);

    /// <summary>
    /// Generates a typed JSON response using the optimal model for the specified function.
    /// This is the production JSON boundary: it enables JSON mode, infers object
    /// vs. array shape from <typeparamref name="T"/>, validates and normalizes the
    /// response, retries/repairs malformed output, and only returns deserialized data.
    /// </summary>
    Task<T> GenerateResponseAsync<T>(string functionType, string prompt, LlmRouterOptions? options = null);

    /// <summary>
    /// Generates a typed vision response using the optimal model for the specified function
    /// </summary>
    Task<T> GenerateVisionResponseAsync<T>(string functionType, string prompt, string imageBase64, 
        LlmRouterOptions? options = null, object? payloadMetadata = null);

    /// <summary>
    /// Selects the optimal model for a function without making a request. Set
    /// <see cref="LlmRouterOptions.RequiresVision"/> to constrain the pick to a
    /// vision-capable model (used by chat surfaces choosing a default before any
    /// image has actually been attached).
    /// </summary>
    Task<LlmModelSelection> SelectOptimalModelAsync(string functionType, string prompt, LlmRouterOptions? options = null);

    /// <summary>
    /// Tests connectivity to all configured AI providers
    /// </summary>
    Task<Dictionary<string, (bool IsHealthy, string Message)>> TestProvidersAsync();

    /// <summary>
    /// Validates that a specific provider/model is usable with a quick, low-cost probe.
    /// This compatibility convenience uses ordinary cached-evidence semantics.
    /// </summary>
    async Task<(bool IsValid, string Message)> ValidateModelAsync(string provider, string model)
    {
        var result = await ValidateModelAsync(
                provider,
                model,
                requireFresh: false,
                CancellationToken.None)
            .ConfigureAwait(false);
        return (result.IsValid, result.Message);
    }

    /// <summary>
    /// Validates an exact provider/model against its current one-way provider
    /// configuration identity. A fresh request bypasses completed cache evidence
    /// but still joins an already-running physical probe for that same identity,
    /// so timeout/cancellation never launches duplicate billable work.
    /// </summary>
    Task<LlmModelValidationResult> ValidateModelAsync(
        string provider,
        string model,
        bool requireFresh,
        CancellationToken cancellationToken);

    // ===== Streaming Support =====

    /// <summary>
    /// Generates a streaming response using the optimal model for the specified function
    /// </summary>
    /// <param name="functionType">The function type for model selection</param>
    /// <param name="prompt">The prompt to send</param>
    /// <param name="onChunk">Callback invoked for each chunk of the response</param>
    /// <param name="options">Optional routing options</param>
    /// <returns>The complete response when finished</returns>
    Task<string> GenerateStreamingAsync(string functionType, string prompt, Func<string, Task> onChunk, LlmRouterOptions? options = null);

    /// <summary>
    /// Generates a streaming response with full metadata
    /// </summary>
    Task<LlmRouterResponse> GenerateStreamingWithMetadataAsync(string functionType, string prompt, Func<string, Task> onChunk, LlmRouterOptions? options = null);

    // ===== Native tool-use (agent engine) =====

    /// <summary>
    /// Execute a non-streaming tool-use turn against the provider selected for
    /// <paramref name="functionType"/>. The provider's native tool-calling
    /// protocol is used (Anthropic tool_use, OpenAI tool_calls, Ollama
    /// OpenAI-compat). Default implementation throws so that existing ILlmRouter
    /// test doubles and partial mocks compile without change.
    /// </summary>
    Task<ChatToolUseResponse> ChatWithToolsAsync(string functionType, ChatToolUseRequest request, CancellationToken ct = default)
        => throw new NotSupportedException(
            $"ILlmRouter implementation '{GetType().FullName}' does not implement ChatWithToolsAsync.");

    /// <summary>
    /// Streaming variant of <see cref="ChatWithToolsAsync"/>. Default
    /// implementation throws so that existing ILlmRouter test doubles and
    /// partial mocks compile without change.
    /// </summary>
    IAsyncEnumerable<ToolUseStreamEvent> StreamChatWithToolsAsync(string functionType, ChatToolUseRequest request, CancellationToken ct = default)
        => throw new NotSupportedException(
            $"ILlmRouter implementation '{GetType().FullName}' does not implement StreamChatWithToolsAsync.");

    /// <summary>
    /// The context window, in tokens (prompt plus generated output), that the
    /// physical tool-use route <paramref name="provider"/>/<paramref name="model"/>
    /// applies to its next request, or null when the router cannot prove it.
    /// Sources are a reviewed provider-pack fact and, for runtimes whose window
    /// is a server setting rather than a model fact (Ollama's num_ctx), the
    /// serving runtime's own report. Default implementation returns null so
    /// existing test doubles and partial mocks compile without change.
    /// </summary>
    Task<int?> GetRouteContextWindowAsync(string provider, string model, CancellationToken ct = default)
        => Task.FromResult<int?>(null);

    // ===== Preference Management =====

    /// <summary>
    /// Sets the preferred model for a specific function type
    /// </summary>
    Task SetFunctionPreferenceAsync(string functionType, string provider, string model);

    /// <summary>
    /// Sets the preferred model for a specific function type with full options
    /// </summary>
    /// <param name="functionType">The function type</param>
    /// <param name="provider">The preferred provider</param>
    /// <param name="model">The preferred model</param>
    /// <param name="fallbackProvider">Optional fallback provider</param>
    /// <param name="fallbackModel">Optional fallback model</param>
    /// <param name="userId">
    /// Caller-minted actor id for the audit trail (free-form; may be a display name).
    /// Never used as pin authority — see <paramref name="userPinned"/>.
    /// </param>
    /// <param name="requiresVision">Whether this function requires vision capabilities</param>
    /// <param name="userPinned">
    /// Whether this write establishes a user pin that autonomous stewardship must respect.
    /// Defaults to inferring from the <see cref="LlmPreferenceAuthors.User"/> actor sentinel.
    /// </param>
    /// <returns>True if successful</returns>
    Task<bool> SetFunctionPreferenceAsync(string functionType, string provider, string model,
        string? fallbackProvider = null, string? fallbackModel = null, string? userId = null, bool? requiresVision = null,
        bool? userPinned = null);

    /// <summary>
    /// Gets the current preference for a function type
    /// </summary>
    Task<LlmModelSelection?> GetFunctionPreferenceAsync(string functionType);

    /// <summary>
    /// Gets all configured function preferences
    /// </summary>
    Task<Dictionary<string, LlmModelSelection>> GetAllFunctionPreferencesAsync();

    /// <summary>
    /// Gets all function preferences as a list with full details
    /// </summary>
    Task<List<LlmFunctionPreference>> GetAllFunctionPreferencesDetailedAsync();

    /// <summary>
    /// Clears the preference for a function type (reverts to default selection)
    /// </summary>
    Task ClearFunctionPreferenceAsync(string functionType);

    // ===== Cost Reporting =====

    /// <summary>
    /// Gets a summary of LLM costs for a time period
    /// </summary>
    Task<LlmCostSummary> GetCostSummaryAsync(DateTime startDate, DateTime endDate);

    /// <summary>
    /// Gets a summary of LLM costs for a time period with optional function filter
    /// </summary>
    Task<LlmCostSummary> GetCostSummaryAsync(string? functionType = null, DateTime? startDate = null, DateTime? endDate = null);

    /// <summary>
    /// Gets detailed cost breakdown by function or model
    /// </summary>
    Task<List<LlmCostBreakdown>> GetCostBreakdownAsync(DateTime startDate, DateTime endDate, string groupBy = "function");

    /// <summary>
    /// Gets detailed cost breakdown by function or model for a time period
    /// </summary>
    Task<List<LlmCostBreakdown>> GetCostBreakdownAsync(TimeSpan period, string groupBy = "function");

    /// <summary>
    /// Gets recent usage statistics
    /// </summary>
    Task<List<LlmUsageRecord>> GetRecentUsageAsync(int count = 100);

    /// <summary>
    /// Gets recent usage statistics with optional function filter
    /// </summary>
    Task<List<LlmUsageRecord>> GetRecentUsageAsync(string? functionType = null, int limit = 100);

    // ===== Configuration Management =====

    /// <summary>
    /// Reload dynamic model costs from external settings
    /// </summary>
    Task ReloadCostsAsync();

    // ===== Embedding Support =====

    /// <summary>
    /// Checks if embedding generation is available (provider is configured and has valid credentials)
    /// </summary>
    /// <returns>Tuple of (isAvailable, message describing status)</returns>
    Task<(bool IsAvailable, string Message)> IsEmbeddingAvailableAsync();

    /// <summary>
    /// Generates text embeddings using the configured embedding model
    /// </summary>
    /// <param name="functionType">The function type for model selection (e.g., FunctionTypes.TextEmbedding)</param>
    /// <param name="text">The text to generate embeddings for</param>
    /// <param name="options">Optional routing options</param>
    /// <returns>The embedding result with vector and metadata</returns>
    Task<LlmEmbeddingResult> GenerateEmbeddingAsync(string functionType, string text, LlmRouterOptions? options = null);

    /// <summary>
    /// Generates embeddings for multiple texts in a batch
    /// </summary>
    /// <param name="functionType">The function type for model selection</param>
    /// <param name="texts">The texts to generate embeddings for</param>
    /// <param name="options">Optional routing options</param>
    /// <returns>The embedding results with vectors and metadata</returns>
    Task<List<LlmEmbeddingResult>> GenerateEmbeddingsAsync(string functionType, IList<string> texts, LlmRouterOptions? options = null);
}

// NOTE: LlmFunctionPreference is defined in ILlmSettingsProvider.cs

/// <summary>
/// Response from LLM router with full metadata
/// </summary>
public class LlmRouterResponse
{
    public required string Content { get; set; }
    public required string Provider { get; set; }
    public required string Model { get; set; }
    public string? FunctionType { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public decimal EstimatedCost { get; set; }
    public TimeSpan Duration { get; set; }
    public bool UsedFallback { get; set; }
    public string? FallbackReason { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

// LlmUsageRecord is defined in ILlmUsageTracker.cs

/// <summary>
/// Result of an embedding generation request
/// </summary>
public class LlmEmbeddingResult
{
    public bool Success { get; set; }
    public float[]? Vector { get; set; }
    public int Dimensions => Vector?.Length ?? 0;
    public required string Provider { get; set; }
    public required string Model { get; set; }
    public string? FunctionType { get; set; }
    public int TokenCount { get; set; }
    public decimal EstimatedCost { get; set; }
    public TimeSpan Duration { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
