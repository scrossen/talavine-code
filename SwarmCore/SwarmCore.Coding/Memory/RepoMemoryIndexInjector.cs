using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Runtime;

namespace SwarmCore.Coding.Memory;

/// <summary>
/// Injects the repository's memory index (<see cref="RepoMemoryOptions.IndexFileName"/>) as a bounded block: the first
/// lines up to the line and character budgets, clearly marked when truncated, with a pointer to the on-demand
/// search/read tools and the proposal rule. Contributes nothing when the repository has no memory files, when repository
/// memory is disabled, or when no workspace is known for the turn. Available to any host under <see cref="InjectorName"/>.
/// </summary>
public sealed class RepoMemoryIndexInjector(
    IOptions<CodingOptions> options,
    IServiceScopeFactory? scopes = null,
    ActiveCodingWorkspaces? activeWorkspaces = null) : IContextInjector
{
    public const string InjectorName = "repoMemory";

    public string Name => InjectorName;

    public async Task<string?> BuildAsync(ContextInjectionRequest request, CancellationToken ct = default)
    {
        var o = options.Value;
        if (!o.RepoMemory.Enabled) return null;
        var workspace = await ResolveWorkspaceAsync(request, o, ct).ConfigureAwait(false);
        if (workspace is null || !Directory.Exists(workspace.FullRoot)) return null;
        var index = await Task.Run(() => RepoMemoryFiles.LoadIndex(workspace.FullRoot, o.RepoMemory), ct).ConfigureAwait(false);
        return index is null ? null : Format(index, o.RepoMemory);
    }

    /// <summary>The injected text for an index (also used for native lanes).</summary>
    public static string Format(RepoMemoryIndex index, RepoMemoryOptions options)
    {
        var topicDir = (options.TopicDirectory ?? "memory").Trim().Trim('/', '\\');
        var sb = new StringBuilder();
        sb.Append("## Repository memory\n");
        if (index.TotalLines > 0)
        {
            sb.Append("Index: ").Append(index.RelativePath);
            sb.Append(index.Truncated
                ? $" (TRUNCATED: first {index.IncludedLines} of {index.TotalLines} lines shown)\n"
                : $" ({index.TotalLines} lines)\n");
            sb.Append(index.Content).Append('\n');
            if (index.Truncated) sb.Append("[... index truncated here ...]\n");
        }
        else
        {
            sb.Append("No ").Append(options.IndexFileName).Append(" index; ");
        }
        sb.Append($"{index.TopicFileCount} topic file(s) under {topicDir}/. ");
        sb.Append("Search and read repository memory on demand with repo-memory.search and repo-memory.read (native agents: the bridge's repo_memory_search and repo_memory_read) ");
        sb.Append("before decisions the memory covers. If this turn establishes a durable repository convention worth keeping, propose the edit with ");
        sb.Append("repo-memory.propose (repo_memory_propose); the person reviews it, and it is never written or committed automatically.");
        return sb.ToString();
    }

    private async Task<CodingWorkspace?> ResolveWorkspaceAsync(ContextInjectionRequest request, CodingOptions o, CancellationToken ct)
    {
        if (activeWorkspaces?.Get(request.ConversationId) is { } active) return active;
        if (!string.IsNullOrWhiteSpace(request.WorkspaceRoot)) return new CodingWorkspace { RepositoryRoot = request.WorkspaceRoot };
        if (scopes is null || !o.InstructionsFromWorkspaceResolver) return null;
        using var scope = scopes.CreateScope();
        return scope.ServiceProvider.GetService<ICodingWorkspaceResolver>() is { } resolver
            ? await resolver.ResolveAsync(request.ConversationId, ct).ConfigureAwait(false)
            : null;
    }
}
