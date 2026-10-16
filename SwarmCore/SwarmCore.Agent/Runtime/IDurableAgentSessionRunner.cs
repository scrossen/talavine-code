using SwarmCore.Agent.ToolUseEngine;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.Agent.Runtime;

/// <summary>
/// The shared durable boundary for hosts that assemble their own
/// <see cref="AgentSession"/> but still need the same ordered, replayable
/// thread lifecycle as <see cref="IAgentTurnRunner"/>.
/// </summary>
public interface IDurableAgentSessionRunner
{
    IAsyncEnumerable<AgentChatEvent> RunAsync(
        AgentSession session,
        string userMessage,
        DurableAgentSessionTurn turn,
        CancellationToken ct = default);

    IAsyncEnumerable<AgentChatEvent> RunAsync(
        AgentSession session,
        ChatMessage userMessage,
        DurableAgentSessionTurn turn,
        CancellationToken ct = default);
}

/// <summary>
/// Stable identity for one execution attempt. Continual hosts should use
/// <see cref="ForAttempt"/> so reclaiming the same persisted attempt retries
/// the same durable turn instead of creating a duplicate ledger entry.
/// </summary>
public sealed record DurableAgentSessionTurn(
    string ThreadId,
    Guid TurnId,
    string StreamTurnId)
{
    public static DurableAgentSessionTurn Create(string threadId)
    {
        var turnId = Guid.NewGuid();
        return new DurableAgentSessionTurn(RequireThreadId(threadId), turnId, turnId.ToString("N"));
    }

    public static DurableAgentSessionTurn ForAttempt(
        string threadId,
        Guid objectiveId,
        int attempt,
        string? executionLane = null)
    {
        if (objectiveId == Guid.Empty) throw new ArgumentException("An objective id is required.", nameof(objectiveId));
        if (attempt < 1) throw new ArgumentOutOfRangeException(nameof(attempt));

        // Attempt one retains the host's original durable identity. Later
        // attempts use an RFC-4122-shaped deterministic id derived from the
        // objective and attempt number, so restart/reclaim is idempotent. A
        // host with multiple model lanes in one attempt (for example failover)
        // supplies a stable lane name so every lane gets its own durable turn.
        var lane = string.IsNullOrWhiteSpace(executionLane) ? null : executionLane.Trim();
        var turnId = attempt == 1 && lane is null
            ? objectiveId
            : CreateDeterministicGuid($"{objectiveId:N}:{attempt}:{lane ?? "default"}");
        return new DurableAgentSessionTurn(RequireThreadId(threadId), turnId, turnId.ToString("N"));
    }

    /// <summary>
    /// Builds an unambiguous thread identity for a host partition and its
    /// conversation id. This keeps tenant/profile-local conversation names
    /// from colliding in a shared event store.
    /// </summary>
    public static string PartitionedThreadId(string partitionKey, string conversationId)
    {
        var partition = RequireIdentity(partitionKey, nameof(partitionKey));
        var conversation = RequireIdentity(conversationId, nameof(conversationId));
        return $"{partition.Length}:{partition}{conversation}";
    }

    private static string RequireThreadId(string value)
        => RequireIdentity(value, nameof(value));

    private static string RequireIdentity(string value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A durable identity is required.", parameterName)
            : value.Trim();

    private static Guid CreateDeterministicGuid(string value)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("swarmcore-agent-turn:" + value));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        bytes[7] = (byte)((bytes[7] & 0x0f) | 0x50); // name-based version marker
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80); // RFC variant
        return new Guid(bytes);
    }
}
