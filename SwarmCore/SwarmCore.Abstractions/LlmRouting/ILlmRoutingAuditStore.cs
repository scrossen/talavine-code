namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Append-only, host-persisted evidence describing an LLM route selection or a
/// Model Scout control-plane change. The core router emits these records without
/// knowing which database the host uses; applications can then answer historical
/// questions such as "why did you use this model?" without retaining prompt text.
/// </summary>
public sealed class LlmRoutingAuditEntry
{
    public string AuditId { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public string EventKind { get; set; } = LlmRoutingAuditEventKinds.Selection;
    public string Actor { get; set; } = "router";
    public string Trigger { get; set; } = "request";
    public string? FunctionType { get; set; }
    public string? Context { get; set; }
    public string? BeforeProvider { get; set; }
    public string? BeforeModel { get; set; }
    public string? AfterProvider { get; set; }
    public string? AfterModel { get; set; }
    public string? FallbackProvider { get; set; }
    public string? FallbackModel { get; set; }
    public string? AdvisorOrPolicy { get; set; }
    public string? Reason { get; set; }

    /// <summary>
    /// Privacy-safe JSON evidence. Implementations should store benchmark ids,
    /// score components, candidate routes, and policy inputs—not prompt bodies.
    /// </summary>
    public string? EvidenceJson { get; set; }

    public string? PromptSha256 { get; set; }
    public string? BenchmarkPromptSha256 { get; set; }
    public string? BenchmarkSuiteSha256 { get; set; }
    public string? CorrelationId { get; set; }
    public string? SessionId { get; set; }
    public string? TurnId { get; set; }
    public long? UsageId { get; set; }
    public bool IsUserOverride { get; set; }
    public bool Succeeded { get; set; } = true;
    public string? Error { get; set; }
}

public static class LlmRoutingAuditEventKinds
{
    public const string Selection = "selection";
    public const string ExecutionStarted = "execution-started";
    public const string Execution = "execution";
    public const string Recommendation = "recommendation";
    public const string ControlRequested = "control-requested";
    public const string PreferenceChanged = "preference-changed";
    public const string PreferenceCleared = "preference-cleared";
    public const string AutomationChanged = "automation-changed";
    public const string PolicyChanged = "policy-changed";
    public const string OptimizationRun = "optimization-run";
    public const string BenchmarkRun = "benchmark-run";
    public const string RecoveryRun = "recovery-run";
    public const string OnDeviceSetup = "on-device-setup";
    public const string RouteAdmitted = "route-admitted";
    public const string RouteExecuted = "route-executed";
    public const string RouteExecutionFailed = "route-execution-failed";
    public const string RoutePaused = "route-paused";
    public const string SurvivalSetChanged = "survival-set-changed";
    /// <summary>A coding request moved from one lane to the next (refusal, unavailable route, stall).</summary>
    public const string CodingLaneHandOff = "coding-lane-handoff";
}

/// <summary>
/// Stable values written to the top-level <c>failureScope</c> field in routing
/// audit evidence. Consumers must not treat a request-scoped proof/policy refusal
/// as evidence that every call for the function has lost an executable route.
/// </summary>
public static class LlmRoutingAuditFailureScopes
{
    public const string RequestProofOrPolicyGate = "request-proof-or-policy-gate";
    public const string FunctionRouteFailure = "function-route-failure";
}

public sealed class LlmRoutingAuditQuery
{
    public string? AuditId { get; set; }
    public string? CorrelationId { get; set; }
    public string? SessionId { get; set; }
    public string? TurnId { get; set; }
    /// <summary>Optional exact actor filter for control-plane state reads.</summary>
    public string? Actor { get; set; }
    /// <summary>Optional exact trigger filter for control-plane state reads.</summary>
    public string? Trigger { get; set; }
    public string? FunctionType { get; set; }
    /// <summary>
    /// Optional set filter for bounded dashboard/history reads. When supplied,
    /// stores return rows for any listed function in one store operation.
    /// </summary>
    public IReadOnlyCollection<string>? FunctionTypes { get; set; }
    public string? EventKind { get; set; }
    /// <summary>Optional set filter matching any listed event kind.</summary>
    public IReadOnlyCollection<string>? EventKinds { get; set; }
    public DateTime? SinceUtc { get; set; }
    public int Limit { get; set; } = 50;
    /// <summary>
    /// When positive with <see cref="FunctionTypes"/>, returns up to this many
    /// newest rows for every requested function before applying the global limit.
    /// This prevents one hot function from hiding another function's current state.
    /// </summary>
    public int? LimitPerFunction { get; set; }
}

/// <summary>
/// Host seam for a durable, append-only routing decision ledger.
/// Implementations intentionally expose no update or delete operation.
/// </summary>
public interface ILlmRoutingAuditStore
{
    Task AppendAsync(LlmRoutingAuditEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<LlmRoutingAuditEntry>> QueryAsync(
        LlmRoutingAuditQuery query,
        CancellationToken ct = default);
}
