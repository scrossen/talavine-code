using System.Text.Json.Nodes;

namespace SwarmCore.Agent.Procedures;

/// <summary>Stable, host-authenticated partition and task identity. Never inferred from model prose.</summary>
public sealed record AgentProcedureKey(string ProfileId, string AgentId, string TaskId);

public enum AgentProcedureValueSource { Input, PriorResult }
public enum AgentProcedureRevisionState { Candidate, Active, Superseded, Rejected }
public enum AgentProcedureMode { VerifiedCompletion, EvidencePreparation }

/// <summary>RFC 6901 pointers. PriorResult references a strictly earlier step by zero-based index.</summary>
public sealed record AgentProcedureBinding(string TargetPointer, AgentProcedureValueSource Source,
    string SourcePointer, int? StepIndex = null);

/// <summary>Only host-approved constants survive in Arguments; per-run values are supplied by Bindings.</summary>
public sealed record AgentProcedureStep(string ToolId, JsonObject Arguments,
    IReadOnlyList<AgentProcedureBinding> Bindings);

/// <summary>Receipt issued by the host verifier, never by the model. Contains no tool payloads or credentials.</summary>
public sealed record AgentProcedureVerification(string VerifierId, string EvidenceHash, DateTimeOffset VerifiedAt)
{
    /// <summary>Optional host-owned baseline fingerprint for consumers requiring exact-current-state adoption.</summary>
    public string? PreconditionsFingerprint { get; init; }
    /// <summary>Optional host-owned policy fingerprint binding authority, privacy, delivery, and no-send invariants.</summary>
    public string? SafetyFingerprint { get; init; }
}

/// <summary>Plan, fingerprints, parent and creation time are immutable across all stored snapshots.</summary>
public sealed record AgentProcedureRevision(Guid Id, Guid? ParentRevisionId, string BindingFingerprint,
    string ToolFingerprint, IReadOnlyList<AgentProcedureStep> Steps, DateTimeOffset CreatedAt,
    AgentProcedureRevisionState State = AgentProcedureRevisionState.Candidate,
    AgentProcedureVerification? Verification = null, string? FailureCode = null);

public sealed record AgentProcedureHistory(AgentProcedureKey Key, long Version, Guid? ActiveRevisionId,
    IReadOnlyList<AgentProcedureRevision> Revisions);

/// <summary>Local-only host storage. Missing history is version zero. CAS advances exactly one version.</summary>
public interface IAgentProcedureStore
{
    Task<AgentProcedureHistory?> GetAsync(AgentProcedureKey key, CancellationToken ct = default);
    Task<bool> CompareExchangeAsync(AgentProcedureKey key, long expectedVersion,
        AgentProcedureHistory replacement, CancellationToken ct = default);
    Task<IReadOnlyList<AgentProcedureHistory>> ListAsync(string profileId, string agentId,
        CancellationToken ct = default);
}

/// <summary>Ephemeral actual invocation evidence. This is deliberately excluded from stored revisions.</summary>
public sealed record AgentProcedureToolReceipt(string ToolId, JsonObject Arguments, JsonNode? Result,
    bool IsError, bool WasDispatched, bool MayHaveSideEffects,
    bool HostCompletionVerified = false, string? FormattedResult = null, bool? OutcomeUncertain = null,
    string? InvocationId = null);

/// <summary>Optional physical adapter proof distinguishing a confirmed failed outcome from an unknown one.</summary>
public interface IAgentProcedureEffectEvidence : Catalog.IToolExecutionEvidence
{
    bool OutcomeUncertain { get; }
}

public sealed record AgentProcedureRunEvidence(IReadOnlyList<AgentProcedureToolReceipt> Receipts,
    bool IsReplay, Guid? RevisionId);

/// <summary>Host-inspected confirmed prefix of the same retained attempt. Never supplied by a model.
/// The inspection must match the current durable history, binding and tool schema; raw receipts stay ephemeral.</summary>
public sealed record AgentProcedureRecovery(long HistoryVersion, string BindingFingerprint,
    AgentProcedureVerification Inspection, IReadOnlyList<AgentProcedureToolReceipt> Receipts);

public sealed record AgentProcedureCandidate(IReadOnlyList<AgentProcedureStep> Steps);

/// <summary>Optional host/model compiler. Its proposal is untrusted until reproduced against the actual trace.</summary>
public interface IAgentProcedurePlanner
{
    Task<AgentProcedureCandidate?> CreateCandidateAsync(AgentProcedureContext context,
        AgentProcedureRunEvidence evidence, CancellationToken ct = default);
}
