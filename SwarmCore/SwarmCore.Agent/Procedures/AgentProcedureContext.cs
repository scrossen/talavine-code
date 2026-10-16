using System.Text.Json.Nodes;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Learning;

namespace SwarmCore.Agent.Procedures;

/// <summary>
/// Per-turn host authority, never populated from tool output or model arguments. The binding must change
/// when task/manual, account, resource scope, verification contract or standing authority changes.
/// A host must call AcceptAsync only after its final output guards have accepted the completed turn.
/// </summary>
public sealed class AgentProcedureContext
{
    public required AgentProcedureKey Key { get; init; }
    public required string BindingFingerprint { get; init; }
    public required JsonObject Inputs { get; init; }
    public required IAgentProcedureStore Store { get; init; }
    public AgentProcedureMode Mode { get; init; }
    public IAgentProcedurePlanner? Planner { get; init; }
    public AgentProcedureRecovery? Recovery { get; init; }
    /// <summary>
    /// Optional host-created routine-learning context for this exact turn. It is never
    /// persisted as source data and is populated only by the host turn-context provider.
    /// </summary>
    public AgentRoutineLearningContext? RoutineLearningContext { get; init; }

    /// <summary>Eligibility only; every call still traverses normal invocation guards and current authority.</summary>
    public required Func<ToolDescriptor, JsonObject, bool> AllowTool { get; init; }
    /// <summary>Allow durable constants such as operation enums/semantic selectors. Defaults to no literals.</summary>
    public Func<string, string, JsonNode?, bool>? AllowLiteral { get; init; }
    /// <summary>
    /// Optional host equivalence used only to recognise which input (or earlier result) an argument came from,
    /// for example tolerating typographic quotes a model substituted while copying an owner's text. Replay always
    /// materializes the exact input value; equivalence never changes what is dispatched or verified.
    /// </summary>
    public Func<JsonNode?, JsonNode?, bool>? ValueEquivalence { get; init; }
    internal bool Equivalent(JsonNode? left, JsonNode? right) => ValueEquivalence?.Invoke(left, right) ?? JsonNode.DeepEquals(left, right);
    /// <summary>Optional fresh precondition. False returns control to the model before dispatch.</summary>
    public Func<AgentProcedureStep, JsonObject, CancellationToken, Task<bool>>? CheckPreconditionAsync { get; init; }
    /// <summary>Deterministic host proof over fresh results; null refuses completion and promotion.</summary>
    public required Func<AgentProcedureRunEvidence, CancellationToken, Task<AgentProcedureVerification?>> VerifyAsync { get; init; }
    /// <summary>Formats current evidence, never a persisted answer. Required for zero-model completion.</summary>
    public required Func<AgentProcedureRunEvidence, string> FormatResult { get; init; }

    internal Func<CancellationToken, Task<bool>>? PendingAcceptance { get; set; }
    internal AgentProcedureAcceptanceCandidate? PendingCandidate { get; set; }
    internal Func<string, CancellationToken, Task>? PendingInvalidation { get; set; }
    /// <summary>Immutable pending intent only; this does not prove durable terminal or host acceptance.</summary>
    public AgentProcedureAcceptanceCandidate? ExportPendingCandidate() => PendingCandidate;
    /// <summary>Promotes a verified candidate only after the caller's output guard has accepted this turn.</summary>
    public async Task<bool> AcceptAsync(CancellationToken ct = default)
    {
        var accept = PendingAcceptance;
        PendingAcceptance = null;
        PendingCandidate = null;
        return accept is not null && await accept(ct).ConfigureAwait(false);
    }

    public void Reject()
    {
        PendingAcceptance = null;
        PendingCandidate = null;
    }

    /// <summary>Explicit final-contract rejection; unlike candidate cleanup this also invalidates a replayed revision.</summary>
    public async Task InvalidateAsync(string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 80
            || reason.Any(c => c is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_' and not '-'))
            throw new ArgumentException("Procedure invalidation requires a finite reason code.", nameof(reason));
        Reject();
        if (PendingInvalidation is { } invalidate) await invalidate(reason, ct).ConfigureAwait(false);
    }
}
