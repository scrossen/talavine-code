namespace SwarmCore.Agent.Runtime;

/// <summary>
/// Optional telemetry sink invoked by <see cref="IAgentTurnRunner"/> after
/// each turn completes (success or failure). Implementations write to whatever
/// store the host application uses for agent activity history — a journal
/// table, a structured log, an analytics pipeline. The runner never blocks on
/// this call and never lets a sink failure propagate to the user.
/// </summary>
public interface IAgentTelemetrySink
{
    Task RecordTurnAsync(AgentTurnTelemetry telemetry, CancellationToken ct = default);
}

/// <summary>
/// One row per completed turn. The runner fills all fields it knows; hosts
/// are free to ignore any they don't care about.
/// </summary>
public sealed record AgentTurnTelemetry(
    DateTime TimestampUtc,
    string TurnId,
    Guid ProfileId,
    string ProfileName,
    string ConversationId,
    bool Errored,
    int ToolCallCount,
    int ToolErrorCount,
    TimeSpan Duration,
    string? ProviderOverride,
    string? ModelOverride);
