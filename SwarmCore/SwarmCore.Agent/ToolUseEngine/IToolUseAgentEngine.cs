using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.Agent.ToolUseEngine;

/// <summary>
/// The replacement for the old <c>AgentLlmLoop</c>. Runs one user turn against
/// a native tool-use provider, iterating until the model stops calling tools
/// or a limit is hit. Events stream back so the UI can show tool activity.
/// </summary>
public interface IToolUseAgentEngine
{
    /// <summary>
    /// Execute one turn. The caller supplies the session (transcript loaded
    /// from the store) and the user's new message. The engine appends
    /// messages to <see cref="AgentSession.Transcript"/> as it goes; the
    /// caller is responsible for persisting the resulting messages after the
    /// stream completes.
    /// </summary>
    IAsyncEnumerable<AgentChatEvent> RunAsync(
        AgentSession session,
        string userMessage,
        CancellationToken ct = default);

    /// <summary>
    /// Execute one turn with a pre-assembled user message. Use this when the
    /// caller needs to carry richer content than plain text — image
    /// attachments, file references, etc. The engine appends
    /// <paramref name="userMessage"/> verbatim to the transcript and runs
    /// the same tool-use loop as the string overload.
    /// </summary>
    IAsyncEnumerable<AgentChatEvent> RunAsync(
        AgentSession session,
        ChatMessage userMessage,
        CancellationToken ct = default);
}
