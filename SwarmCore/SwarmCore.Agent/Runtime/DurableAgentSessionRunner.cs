using System.Runtime.CompilerServices;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.ToolUseEngine;

namespace SwarmCore.Agent.Runtime;

/// <summary>
/// Adapts a host-built raw tool-use session to the common durable thread
/// ledger. Events are committed before they are exposed to the host.
/// </summary>
public sealed class DurableAgentSessionRunner(
    IToolUseAgentEngine engine,
    ITurnEventMapper mapper,
    IAgentThreadEventStore threadEvents) : IDurableAgentSessionRunner
{
    public IAsyncEnumerable<AgentChatEvent> RunAsync(
        AgentSession session,
        string userMessage,
        DurableAgentSessionTurn turn,
        CancellationToken ct = default)
        => RunCoreAsync(session, engine.RunAsync(session, userMessage, ct), turn, ct);

    public IAsyncEnumerable<AgentChatEvent> RunAsync(
        AgentSession session,
        ChatMessage userMessage,
        DurableAgentSessionTurn turn,
        CancellationToken ct = default)
        => RunCoreAsync(session, engine.RunAsync(session, userMessage, ct), turn, ct);

    private async IAsyncEnumerable<AgentChatEvent> RunCoreAsync(
        AgentSession session,
        IAsyncEnumerable<AgentChatEvent> source,
        DurableAgentSessionTurn turn,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(turn);
        if (!string.Equals(session.ConversationId, turn.ThreadId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Session conversation '{session.ConversationId}' does not match durable thread '{turn.ThreadId}'.");
        }

        var started = DateTime.UtcNow;
        var context = new TurnMappingContext(
            turn.StreamTurnId,
            session.Profile.Id,
            session.Profile.Name,
            started);
        var ordinal = 0L;
        var terminal = false;

        await threadEvents.AppendAsync(
            turn.ThreadId,
            turn.TurnId,
            EventId(turn.TurnId, ordinal++),
            new TurnStarted(turn.StreamTurnId, session.Profile.Id, session.Profile.Name),
            CancellationToken.None).ConfigureAwait(false);

        try
        {
            await foreach (var engineEvent in source.WithCancellation(ct).ConfigureAwait(false))
            {
                foreach (var turnEvent in mapper.Map(engineEvent, context))
                {
                    await threadEvents.AppendAsync(
                        turn.ThreadId,
                        turn.TurnId,
                        EventId(turn.TurnId, ordinal++),
                        turnEvent,
                        CancellationToken.None).ConfigureAwait(false);
                    terminal |= turnEvent is TurnCompleted or TurnErrored or TurnAbandoned;
                }

                yield return engineEvent;
            }
        }
        finally
        {
            if (!terminal)
            {
                await threadEvents.AppendAsync(
                    turn.ThreadId,
                    turn.TurnId,
                    EventId(turn.TurnId, ordinal),
                    new TurnAbandoned(
                        turn.StreamTurnId,
                        ct.IsCancellationRequested
                            ? "Agent execution was cancelled."
                            : "Agent execution ended without a terminal event."),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static string EventId(Guid turnId, long ordinal)
        => $"{turnId:N}:session:{ordinal}";
}
