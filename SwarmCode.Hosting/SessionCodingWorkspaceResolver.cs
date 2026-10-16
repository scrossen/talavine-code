using Microsoft.Extensions.Options;
using SwarmCore.Coding;

namespace SwarmCode.Hosting;

/// <summary>
/// A session's turns work in its own root (an isolated agent's worktree) or the workspace's repository. A host-level
/// override (<see cref="Improvement.SessionWorkspaceOverrides"/>, set for a self-improvement cycle's session) wins and
/// may carry acceptance checks for that conversation alone.
/// </summary>
public sealed class SessionCodingWorkspaceResolver(WorkspaceSessionCatalog sessions, IOptions<CodingOptions> options,
    Improvement.SessionWorkspaceOverrides? overrides = null) : ICodingWorkspaceResolver
{
    public async Task<CodingWorkspace?> ResolveAsync(string conversationId, CancellationToken ct = default)
    {
        if (overrides?.TryGet(conversationId) is { } overridden) return overridden;
        var session = await sessions.GetAsync(conversationId, ct).ConfigureAwait(false);
        return new CodingWorkspace { RepositoryRoot = session?.WorkingRoot ?? options.Value.RepositoryRoot };
    }
}
