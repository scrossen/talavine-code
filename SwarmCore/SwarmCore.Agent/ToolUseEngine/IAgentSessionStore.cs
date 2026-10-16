using System.Collections.Concurrent;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.Agent.ToolUseEngine;

/// <summary>
/// Persists the tool-use transcript across turns. The chat service hydrates
/// a session from the store at the start of each turn and appends any new
/// messages produced during the turn.
/// </summary>
public interface IAgentSessionStore
{
    /// <summary>
    /// Load the ordered message list for a conversation. Returns an empty
    /// list when no transcript exists yet.
    /// </summary>
    Task<List<ChatMessage>> LoadAsync(string conversationId, CancellationToken ct = default);

    /// <summary>
    /// Append one message to the transcript.
    /// </summary>
    Task AppendAsync(string conversationId, ChatMessage message, CancellationToken ct = default);

    /// <summary>
    /// Convenience: append multiple messages atomically (a single turn's
    /// worth of messages).
    /// </summary>
    Task AppendRangeAsync(string conversationId, IReadOnlyList<ChatMessage> messages, CancellationToken ct = default);
}

/// <summary>
/// Process-local transcript store. Safe for single-process use. Host
/// applications that need durability can provide their own store.
/// </summary>
public sealed class InMemoryAgentSessionStore : IAgentSessionStore
{
    private readonly ConcurrentDictionary<string, List<ChatMessage>> _byConversation = new();

    public Task<List<ChatMessage>> LoadAsync(string conversationId, CancellationToken ct = default)
    {
        var list = _byConversation.GetOrAdd(conversationId, _ => new List<ChatMessage>());
        lock (list)
        {
            return Task.FromResult(new List<ChatMessage>(list));
        }
    }

    public Task AppendAsync(string conversationId, ChatMessage message, CancellationToken ct = default)
    {
        var list = _byConversation.GetOrAdd(conversationId, _ => new List<ChatMessage>());
        lock (list) { list.Add(message); }
        return Task.CompletedTask;
    }

    public Task AppendRangeAsync(string conversationId, IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
    {
        var list = _byConversation.GetOrAdd(conversationId, _ => new List<ChatMessage>());
        lock (list) { list.AddRange(messages); }
        return Task.CompletedTask;
    }
}
