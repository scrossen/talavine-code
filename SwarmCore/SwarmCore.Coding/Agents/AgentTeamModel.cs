using System.Text.Json.Serialization;

namespace SwarmCore.Coding.Agents;

/// <summary>Limits for agents that coding turns start (<c>Coding:Agents</c>). They bound fan-out; they never grant authority.</summary>
public sealed class AgentTeamOptions
{
    public const string SectionName = "Coding:Agents";

    /// <summary>Offer the <c>agents.*</c> tools. Off removes them from new turns; running agents keep running.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How deep agents may nest. A person's session is depth 0, its agents depth 1, theirs depth 2.</summary>
    public int MaxDepth { get; set; } = 2;

    /// <summary>Agents running at the same time across the host. Further agents wait in order.</summary>
    public int MaxRunning { get; set; } = 4;

    /// <summary>Agents one person's session (with all its descendants) may start in total.</summary>
    public int MaxAgentsPerSession { get; set; } = 24;

    /// <summary>Characters of an agent's final answer kept as its result.</summary>
    public int MaxResultCharacters { get; set; } = 12_000;

    /// <summary>Longest one <c>agents.wait</c> call blocks before returning what has finished.</summary>
    public int MaxWaitSeconds { get; set; } = 300;
}

public enum AgentState
{
    /// <summary>Waiting for a running slot.</summary>
    Queued,
    Running,
    /// <summary>Finished its last turn; the result is ready and it accepts follow-up messages.</summary>
    Idle,
    Failed,
    Cancelled,
    /// <summary>Its host stopped (restart, workspace switch) mid-turn. A follow-up message restarts it.</summary>
    Interrupted,
}

/// <summary>
/// One agent another agent started. <see cref="Id"/> is also the agent's own conversation id, so a host can show
/// its full transcript. Parent, root and depth come from the trusted turn that spawned it, never from model input.
/// </summary>
public sealed record AgentRecord
{
    public required string Id { get; init; }
    public required string ParentId { get; init; }
    /// <summary>The person's session at the top of the tree.</summary>
    public required string RootId { get; init; }
    public int Depth { get; init; }
    public required string Title { get; init; }
    public required string Task { get; init; }
    public string? Lane { get; init; }
    public string? Model { get; init; }
    /// <summary>Host-owned model-selection receipt; a finished turn is still evidence for the parent to verify.</summary>
    public string? RoutingNote { get; init; }
    public AgentState State { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime UpdatedUtc { get; init; }
    public DateTime? StartedUtc { get; init; }
    public DateTime? FinishedUtc { get; init; }
    public int Turns { get; init; }
    public int ToolCalls { get; init; }
    /// <summary>What it is doing now, in a few words (host-written).</summary>
    public string? Activity { get; init; }
    public string? Result { get; init; }
    public string? Error { get; init; }
    /// <summary>The parent has received the latest result (through a tool result or a completion notice).</summary>
    public bool ResultDelivered { get; init; }
    /// <summary>Messages waiting for a running slot (the first is the task brief).</summary>
    public IReadOnlyList<string> Pending { get; init; } = [];
    /// <summary>The host instance that owns this record. Only the owner rewrites it; others read it.</summary>
    public string? Owner { get; init; }
    /// <summary>The agent's own git worktree when it was started with <c>isolation: "worktree"</c>; null when it shares the parent's workspace.</summary>
    public AgentWorktree? Worktree { get; init; }

    /// <summary>The named role (<see cref="AgentDefinition"/>) this agent was started as, or null.</summary>
    public string? Agent { get; init; }

    /// <summary>Tool-id globs the role allows (empty: every tool of the session's profile). Host state, never model output.</summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>
    /// Independent follow-up work the agent noticed (out of scope for its own task): it runs at once in its own session
    /// listed under the starting one, and its result is never handed back or waited for. The person reviews it directly.
    /// </summary>
    public bool Independent { get; init; }

    [JsonIgnore]
    public bool IsTerminal => State is AgentState.Idle or AgentState.Failed or AgentState.Cancelled or AgentState.Interrupted;
}

/// <param name="Isolation">Null shares the parent's workspace; <see cref="AgentIsolation.Worktree"/> gives the agent its own git worktree.</param>
/// <param name="Agent">A named role from the agent definitions (<c>agents/*.md</c>); its lane, model, isolation and tools are defaults the request may override.</param>
/// <param name="Independent">Start it as independent follow-up work (<see cref="AgentRecord.Independent"/>): no hand-back, not waited for.</param>
public sealed record AgentSpawnRequest(string Task, string? Title = null, string? Lane = null, string? Model = null, string? Isolation = null, string? Agent = null, bool Independent = false);

/// <summary>What the team asks a host to create for a new agent.</summary>
public sealed record AgentConversationRequest(string ParentConversationId, string Title, string? Lane, string? Model, string? Isolation,
    string? Task = null, bool ExplicitModelSelection = false);

/// <summary>The agent conversation a host created, with the worktree it made for an isolated agent.</summary>
public sealed record AgentConversation(string Id, AgentWorktree? Worktree = null, string? Lane = null, string? Model = null,
    string? RoutingNote = null);

/// <summary>Outcome of forgetting an agent: whether its record went, and what happened to its worktree (null: it had none).</summary>
public sealed record AgentForgetResult(bool Forgotten, AgentWorktreeCleanup? Worktree);

/// <summary>What a host reports when one of a conversation's turns ends.</summary>
/// <param name="MoreQueued">The host already has another message queued for this conversation, so it keeps working.</param>
public sealed record AgentTurnOutcome(bool Completed, bool Cancelled, string? Error, string? FinalText, int ToolCalls, bool MoreQueued = false);

/// <summary>
/// The host side of an agent team: it owns conversations, turn execution, persistence of transcripts and the
/// person's approval policy. The team decides who may start, message and stop which agent; the host runs them.
/// </summary>
public interface IAgentTeamHost
{
    /// <summary>Creates the agent's own conversation (bound to the parent's workspace) and returns its id.</summary>
    Task<string> CreateConversationAsync(string parentConversationId, string title, string? lane, string? model, CancellationToken ct);

    /// <summary>
    /// Creates the agent's conversation, isolated as requested. A host that cannot isolate refuses an isolation request;
    /// the default shares the parent's workspace and refuses any isolation.
    /// </summary>
    async Task<AgentConversation> CreateAgentConversationAsync(AgentConversationRequest request, CancellationToken ct)
    {
        if (request.Isolation is not null)
            throw new InvalidOperationException($"This host cannot start agents with isolation \"{request.Isolation}\". Start the agent without isolation.");
        return new AgentConversation(await CreateConversationAsync(request.ParentConversationId, request.Title, request.Lane, request.Model, ct).ConfigureAwait(false));
    }

    /// <summary>Starts a turn in the agent's conversation (queues behind a running one). False when it cannot start.</summary>
    Task<bool> StartTurnAsync(string conversationId, string message, string? lane, string? model, CancellationToken ct);

    /// <summary>Stops the conversation's running turn and its queued messages.</summary>
    void Cancel(string conversationId);

    /// <summary>True while the conversation is running a turn.</summary>
    bool IsBusy(string conversationId);

    /// <summary>Hands a harness notice to an idle conversation as a continuation turn (agents finished).</summary>
    Task<bool> DeliverAsync(string conversationId, string message, CancellationToken ct);
}

/// <summary>Durable agent records. One record per agent; a host instance rewrites only records it owns.</summary>
public interface IAgentTeamStore
{
    Task<IReadOnlyList<AgentRecord>> LoadAsync(CancellationToken ct);
    Task SaveAsync(AgentRecord record, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
}

public sealed class InMemoryAgentTeamStore : IAgentTeamStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AgentRecord> _records = new(StringComparer.Ordinal);
    public Task<IReadOnlyList<AgentRecord>> LoadAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AgentRecord>>(_records.Values.ToList());
    public Task SaveAsync(AgentRecord record, CancellationToken ct) { _records[record.Id] = record; return Task.CompletedTask; }
    public Task DeleteAsync(string id, CancellationToken ct) { _records.TryRemove(id, out _); return Task.CompletedTask; }
}
