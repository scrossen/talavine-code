namespace SwarmCore.Agent.InstructionChat;

/// <summary>
/// Host persistence boundary for a per-agent instruction-chat conversation.
/// Implementations must treat the state as secret-bearing because pending
/// credentials are included until a proposal is applied or discarded.
/// </summary>
public interface IInstructionChatSessionStore
{
    Task<InstructionChatSessionState?> LoadAsync(string agentKey, CancellationToken ct = default);
    Task SaveAsync(string agentKey, InstructionChatSessionState state, CancellationToken ct = default);
    Task DeleteAsync(string agentKey, CancellationToken ct = default);
}
