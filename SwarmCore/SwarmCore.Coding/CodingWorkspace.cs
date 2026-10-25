using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SwarmCore.Coding.Routing;

namespace SwarmCore.Coding;

/// <summary>
/// The repository one coding conversation works in, chosen by the host per conversation
/// (or per request). The orchestrator passes <see cref="RepositoryRoot"/> to the agent turn as
/// its workspace, so sandbox-resolving tools (edit, grep, glob, git, fs, shell) treat it as the
/// root and fence relative paths to it for that turn only; native lanes run in it; the
/// repository-instruction injector reads from it. A host never needs one process per repository.
/// </summary>
public sealed record CodingWorkspace
{
    /// <summary>Absolute repository directory. Must exist when a turn starts.</summary>
    public required string RepositoryRoot { get; init; }

    /// <summary>Lane this workspace prefers when a request names none. Null uses the lane source's default.</summary>
    public string? LaneName { get; init; }

    /// <summary>
    /// Host-trusted acceptance commands for this workspace. Null uses the harness configuration
    /// (<see cref="CodingOptions.Acceptance"/>); an empty list means completion is unverified.
    /// </summary>
    public IReadOnlyList<CodingAcceptanceCommand>? AcceptanceChecks { get; init; }

    /// <summary>
    /// Directories whose instruction files (<see cref="CodingOptions.InstructionFileNames"/>) are
    /// injected, outermost first. Empty means the repository root and each of its ancestors.
    /// </summary>
    public IReadOnlyList<string> InstructionRoots { get; init; } = [];

    /// <summary>Normalized absolute repository root.</summary>
    public string FullRoot => Path.GetFullPath(RepositoryRoot);
}

/// <summary>
/// Host seam: which repository a coding conversation works in. Hosts that serve many
/// conversations (Talavine) look it up from their own conversation state; SwarmCode returns
/// the repository it has open. Returning null means the conversation has no workspace and
/// the orchestrator refuses to start a turn rather than guess a directory.
/// </summary>
public interface ICodingWorkspaceResolver
{
    Task<CodingWorkspace?> ResolveAsync(string conversationId, CancellationToken ct = default);
}

/// <summary>
/// The single-repository resolver used by the SwarmCode hosts: every conversation works in
/// <see cref="CodingOptions.RepositoryRoot"/>.
/// </summary>
public sealed class OptionsCodingWorkspaceResolver(IOptions<CodingOptions> options) : ICodingWorkspaceResolver
{
    public Task<CodingWorkspace?> ResolveAsync(string conversationId, CancellationToken ct = default)
        => Task.FromResult<CodingWorkspace?>(new CodingWorkspace { RepositoryRoot = options.Value.RepositoryRoot });
}

/// <summary>
/// The workspace each running coding turn selected, keyed by conversation. The orchestrator
/// records it for the duration of a turn so components that only see the conversation id
/// (the repository-instruction injector) read the same workspace the turn's tools are fenced
/// to, including a per-request override the host resolver does not know about.
/// </summary>
public sealed class ActiveCodingWorkspaces
{
    private sealed record Entry(CodingWorkspace Workspace, bool PlanMode, IReadOnlyList<string> AllowedTools, bool Unattended, Guid? GoalId, string? GoalLeaseToken,
        Permissions.CodingPermissionPolicy? Permissions)
    {
        public string Capability { get; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        public CancellationTokenSource Lifetime { get; } = new();
    }
    private sealed record BoundAttempt(string ConversationId, Entry Entry);
    private readonly AsyncLocal<BoundAttempt?> _bound = new();
    private Entry? Find(string conversationId) => _bound.Value is { } bound && bound.ConversationId == conversationId
        ? IsCurrent(conversationId, bound.Entry) ? bound.Entry : null
        : _active.GetValueOrDefault(conversationId);
    private bool IsCurrent(string conversationId, Entry entry)
        => _active.TryGetValue(conversationId, out var current) && ReferenceEquals(current, entry) && !entry.Lifetime.IsCancellationRequested;
    /// <summary>Opaque launch binding for the current own-MCP process; never include in model input.</summary>
    public string? BridgeCapability(string conversationId) => Find(conversationId)?.Capability;
    internal Permissions.CodingPermissionPolicy? BridgePermissions(string conversationId) => Find(conversationId)?.Permissions;
    internal bool IsBridgeAttemptCurrent(string conversationId)
        => _bound.Value is { } bound && bound.ConversationId == conversationId && IsCurrent(conversationId, bound.Entry);
    internal CancellationToken BridgeAttemptCancellation(string conversationId)
        => _bound.Value is { } bound && bound.ConversationId == conversationId ? bound.Entry.Lifetime.Token : new(true);
    internal IDisposable? BindBridgeAttempt(string conversationId, string? capability)
    {
        if (string.IsNullOrWhiteSpace(capability) || !_active.TryGetValue(conversationId, out var entry)
            || !string.Equals(entry.Capability, capability, StringComparison.Ordinal) || !IsCurrent(conversationId, entry)) return null;
        var prior = _bound.Value;
        _bound.Value = new(conversationId, entry);
        return new EndBinding(() => _bound.Value = prior);
    }
    private sealed class EndBinding(Action end) : IDisposable { public void Dispose() => end(); }

    private readonly ConcurrentDictionary<string, Entry> _active = new(StringComparer.Ordinal);

    public CodingWorkspace? Get(string conversationId)
        => Find(conversationId)?.Workspace;

    /// <summary>True while the conversation runs a plan-mode turn (host state from the request, never model output).</summary>
    public bool IsPlanTurn(string conversationId)
        => Find(conversationId)?.PlanMode == true;

    /// <summary>Host turn origin retained for tools bridged from a native agent (which has a new ambient context).</summary>
    public bool IsUnattendedTurn(string conversationId)
        => Find(conversationId)?.Unattended == true;

    /// <summary>Exact host-owned active inbox goal; absent for ordinary chat turns.</summary>
    public Guid? GoalMessageGoalId(string conversationId)
        => Find(conversationId)?.GoalId;

    /// <summary>Exact current worker lease; runtime only, never native process input.</summary>
    public string? GoalMessageLeaseToken(string conversationId)
        => Find(conversationId)?.GoalLeaseToken;

    /// <summary>The turn's tool allow list (an agent role's); empty admits every tool. See <see cref="Agents.CodingAgentTools"/>.</summary>
    public IReadOnlyList<string> AllowedTools(string conversationId)
        => Find(conversationId)?.AllowedTools ?? [];

    /// <summary>True when the running turn's allow list admits <paramref name="toolId"/> (always true without a list).</summary>
    public bool AdmitsTool(string conversationId, string toolId) => Agents.CodingAgentTools.Admits(AllowedTools(conversationId), toolId);

    /// <summary>Records the workspace (and whether the turn is a plan turn, and its tool allow list) until the returned handle is disposed.</summary>
    public IDisposable Enter(string conversationId, CodingWorkspace workspace, bool planMode = false, IReadOnlyList<string>? allowedTools = null, bool unattended = false, Guid? goalId = null, string? goalMessageLeaseToken = null, Permissions.CodingPermissionPolicy? permissions = null)
    {
        var entry = new Entry(workspace, planMode, allowedTools?.ToArray() ?? [], unattended, goalId, goalId is null ? null : goalMessageLeaseToken, permissions is null ? null : permissions with { Allow = permissions.Allow.ToArray(), Deny = permissions.Deny.ToArray() });
        _active.AddOrUpdate(conversationId, entry, (_, old) => { old.Lifetime.Cancel(); return entry; });
        return new Exit(this, conversationId, entry);
    }

    private sealed class Exit(ActiveCodingWorkspaces owner, string conversationId, Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            owner._active.TryRemove(new KeyValuePair<string, Entry>(conversationId, entry));
            entry.Lifetime.Cancel();
        }
    }
}
