using System.Collections.Concurrent;
using SwarmCore.Coding;

namespace SwarmCode.Hosting.Improvement;

/// <summary>
/// Per-conversation workspace overrides a host sets for sessions it runs itself: a self-improvement cycle's session
/// works in its worktree with that cycle's build and test checks as acceptance. In memory only; an override lives as
/// long as the host keeps it (a restart never resumes a cycle).
/// </summary>
public sealed class SessionWorkspaceOverrides
{
    private readonly ConcurrentDictionary<string, CodingWorkspace> _overrides = new(StringComparer.Ordinal);

    public void Set(string conversationId, CodingWorkspace workspace) => _overrides[conversationId] = workspace;

    public bool Remove(string conversationId) => _overrides.TryRemove(conversationId, out _);

    public CodingWorkspace? TryGet(string conversationId) => _overrides.GetValueOrDefault(conversationId);

    public int Count => _overrides.Count;
}
