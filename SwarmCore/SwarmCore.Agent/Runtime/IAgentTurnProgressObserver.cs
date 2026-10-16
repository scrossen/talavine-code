namespace SwarmCore.Agent.Runtime;

/// <summary>
/// Host extension point for durable, live observation of a turn. Unlike the
/// token stream consumed by a UI, observers receive only lifecycle and tool
/// events, so they can checkpoint objectives and publish activity without a
/// database write for every streamed token.
/// </summary>
public interface IAgentTurnProgressObserver
{
    /// <summary>
    /// When true, failure to persist the start checkpoint aborts the turn
    /// before session preparation or tool dispatch. Durable execution cannot
    /// safely degrade into untracked execution merely because its store is
    /// unavailable.
    /// </summary>
    bool RequiresDurableCheckpoints => false;

    Task OnTurnStartedAsync(AgentTurnProgressContext context, CancellationToken ct = default);

    Task OnTurnEventAsync(
        AgentTurnProgressContext context,
        TurnEvent turnEvent,
        CancellationToken ct = default);

    /// <summary>
    /// The stream consumer detached or cancelled before a terminal event. A
    /// durable host should release process-local ownership but leave its saved
    /// objective recoverable unless the initiating surface explicitly stopped it.
    /// </summary>
    Task OnTurnAbandonedAsync(
        AgentTurnProgressContext context,
        CancellationToken ct = default)
        => Task.CompletedTask;
}

/// <summary>
/// Stable host-owned identity and authority for one running turn. The durable
/// <see cref="TurnRowId"/> is deliberately separate from the ephemeral stream
/// event id, allowing a restarted host to join receipts back to persisted work.
/// </summary>
public sealed record AgentTurnProgressContext(
    string ConversationId,
    Guid TurnRowId,
    Guid ProfileId,
    string ProfileName,
    string UserMessage,
    string? Provider,
    string? Model,
    bool UserRequestAuthorized,
    bool UnattendedAgentTurn,
    IReadOnlyDictionary<string, string>? Metadata,
    AgentCommunicationScope? CommunicationScope = null,
    string? ActingAgentId = null);
