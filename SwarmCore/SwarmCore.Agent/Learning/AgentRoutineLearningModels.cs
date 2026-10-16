using SwarmCore.Agent.Procedures;

namespace SwarmCore.Agent.Learning;

/// <summary>Host-derived policy identity. Every value is a SHA-256 digest, never source data.</summary>
public sealed record AgentRoutineSafetyFingerprint(
    string Authority,
    string Privacy,
    string Delivery,
    string NoSend);

/// <summary>Fresh host context for staging, adoption, and rendering a routine practice.</summary>
public sealed record AgentRoutineLearningContext(
    AgentProcedureKey Key,
    string BindingFingerprint,
    string ToolFingerprint,
    string PreconditionsFingerprint,
    AgentRoutineSafetyFingerprint Safety)
{
    /// <summary>Host route configuration digest, when the host can revalidate it at adoption/render time.</summary>
    public string? ProfileRouteFingerprint { get; init; }
}

/// <summary>Host-created identity passed from a real employee turn to the learning adapter.</summary>
public sealed record AgentRoutineLearningTurnScope(
    Guid EmployeeProfileId,
    string ActingAgentId,
    AgentProcedureJob ProcedureJob)
{
    /// <summary>Set only by AgentTurnRunner from the actual procedure context it creates.</summary>
    public AgentRoutineLearningContext? LearningContext { get; private set; }

    /// <summary>True only when the matching procedure CAS committed before turn completion.</summary>
    public bool AcceptedProcedurePrefix { get; private set; }

    internal void BindLearningContext(AgentRoutineLearningContext context)
        => LearningContext = context ?? throw new ArgumentNullException(nameof(context));

    internal void RecordAcceptedProcedurePrefix(bool accepted)
        => AcceptedProcedurePrefix = accepted;
}

/// <summary>
/// Optional employee lifecycle seam for hosts with bounded routine learning.
/// Implementations must derive current authority/fingerprints from host state,
/// never accept verification claims or policy data from model arguments.
/// </summary>
public interface IAgentRoutineLearningTurnHook
{
    Task<string> RenderCurrentPromptSectionAsync(AgentRoutineLearningTurnScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>Called only after the employee's final delivery guard returned Delivered.</summary>
    Task<bool> OnAcceptedTurnAsync(AgentRoutineLearningTurnScope scope,
        CancellationToken cancellationToken = default);
}

public enum AgentRoutineLearningRevisionState
{
    Staged = 0,
    Active = 1,
    Superseded = 2,
    Rejected = 3,
    Adopted = 4,
}

/// <summary>
/// Durable history item. Trigger/procedure/reason are audit-only; renderers may compile only Kind.
/// An active revision retains the id and host verifier receipt of the accepted procedure revision.
/// </summary>
public sealed record AgentRoutineLearningRevision(
    Guid Id,
    Guid? ParentRevisionId,
    int Sequence,
    AgentLearnedPractice Practice,
    string BindingFingerprint,
    string ToolFingerprint,
    string PreconditionsFingerprint,
    AgentRoutineSafetyFingerprint Safety,
    DateTimeOffset CreatedAtUtc,
    AgentRoutineLearningRevisionState State,
    Guid? CandidateRevisionId = null,
    Guid? AcceptedProcedureRevisionId = null,
    long? AcceptedProcedureHistoryVersion = null,
    AgentProcedureVerification? Verification = null,
    Guid? RevertsRevisionId = null);

public sealed record AgentRoutineLearningHistory(
    AgentProcedureKey Key,
    long Version,
    Guid? ActiveRevisionId,
    IReadOnlyList<AgentRoutineLearningRevision> Revisions);

/// <summary>Host persistence contract. Implementations must perform exact-version CAS atomically.</summary>
public interface IAgentRoutineLearningStore
{
    Task<AgentRoutineLearningHistory?> GetAsync(AgentProcedureKey key, CancellationToken ct = default);

    Task<bool> CompareExchangeAsync(AgentProcedureKey key, long expectedVersion,
        AgentRoutineLearningHistory replacement, CancellationToken ct = default);
}

public sealed class AgentRoutineLearningConflictException(string message) : InvalidOperationException(message);
