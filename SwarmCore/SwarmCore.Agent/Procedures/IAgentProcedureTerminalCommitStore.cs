using SwarmCore.Agent.Runtime;

namespace SwarmCore.Agent.Procedures;

/// <summary>
/// Optional host transaction boundary for a verified procedure and its public terminal event.
/// Stage is called only with the live host context after required output and persistence checks.
/// A serialized candidate is integrity data, never authority to create a stage.
/// </summary>
public interface IAgentProcedureTerminalCommitStore
{
    Task<AgentProcedureTerminalStageLease> StageAsync(AgentProcedureContext context, Guid presentationProfileId, string inputText,
        string threadId, Guid turnId, string eventId, TurnCompleted completion,
        CancellationToken ct = default);

    /// <summary>Completes the saved turn even if a competing procedure head wins; in that case learning is not accepted.</summary>
    Task<AgentProcedureTerminalCommitResult> CommitAsync(string threadId, Guid turnId,
        string eventId, TurnCompleted completion, AgentProcedureTerminalStageLease lease,
        Func<CancellationToken, Task<AgentProcedureCurrentAuthority>> refreshAuthority,
        CancellationToken ct = default);

    /// <summary>
    /// Fresh exact readback after an uncertain commit. Pending does not authorize replaying
    /// tools, accepting a candidate, or publishing a successful terminal event.
    /// </summary>
    Task<AgentProcedureTerminalCommitResult?> ReadCommittedAsync(string threadId, Guid turnId,
        string eventId, TurnCompleted completion, CancellationToken ct = default);
}

public sealed record AgentProcedureTerminalCommitResult(bool LearningAccepted);

/// <summary>In-memory authority issued by the stage call. Its digest, never its value, is saved.</summary>
public readonly record struct AgentProcedureTerminalStageLease(Guid Value);

/// <summary>Fresh host-owned authority for the exact pending turn; a saved stage cannot supply this.</summary>
public sealed record AgentProcedureCurrentAuthority(AgentProcedureContext Context, string ToolFingerprint);
