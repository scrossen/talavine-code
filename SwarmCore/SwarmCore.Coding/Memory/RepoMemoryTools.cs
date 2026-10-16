using SwarmCore.Agent.Catalog;

namespace SwarmCore.Coding.Memory;

public sealed record RepoMemorySearchInput
{
    /// <summary>Words to look for (an empty query lists the topic files).</summary>
    public string Query { get; init; } = string.Empty;
    public int? Limit { get; init; }
}

public sealed record RepoMemorySearchOutput(IReadOnlyList<RepoMemorySearchHit> Hits, IReadOnlyList<RepoMemoryTopic> Topics, string Message);

public sealed record RepoMemoryReadInput
{
    /// <summary>MEMORY.md or memory/&lt;topic&gt;.md, relative to the repository root.</summary>
    public string Path { get; init; } = string.Empty;
    public int? StartLine { get; init; }
    public int? MaxLines { get; init; }
}

public sealed record RepoMemoryProposeInput
{
    /// <summary>MEMORY.md or memory/&lt;topic&gt;.md, relative to the repository root.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Exact existing passage to replace (unique in the file). Empty appends to the file, or creates a new topic file.</summary>
    public string? OldText { get; init; }

    /// <summary>The replacement passage, or the text to append / the new file's content.</summary>
    public string NewText { get; init; } = string.Empty;

    /// <summary>Which durable convention this turn established and why it belongs in repository memory.</summary>
    public string Rationale { get; init; } = string.Empty;
}

public sealed record RepoMemoryProposeOutput(bool Ok, string Message, string? ProposalId, string? Diff);

/// <summary>
/// Tool descriptors over <see cref="RepoMemoryFiles"/> and <see cref="RepoMemoryProposals"/>. search/read are read-only;
/// propose writes only to the host's proposal store (never the repository, never git). The repository is resolved by the
/// host on every call so one descriptor set can follow the turn's workspace.
/// </summary>
public static class RepoMemoryTools
{
    public const string Prefix = "repo-memory";
    public const string SearchToolId = Prefix + ".search";
    public const string ReadToolId = Prefix + ".read";
    public const string ProposeToolId = Prefix + ".propose";
    public static IReadOnlyList<string> ToolIds { get; } = [SearchToolId, ReadToolId, ProposeToolId];

    /// <param name="conversationId">
    /// The conversation a proposal comes from, resolved on every call like <paramref name="repositoryRoot"/>, so the review
    /// surface can link it to its session. Null reads the turn the engine made ambient for the tool call.
    /// </param>
    public static IReadOnlyList<ToolDescriptor> Create(
        Func<string?> repositoryRoot,
        Func<RepoMemoryOptions> options,
        IRepoMemoryProposalStore proposals,
        string ownerAgentId = "coding",
        Func<string?>? conversationId = null)
    {
        ArgumentNullException.ThrowIfNull(repositoryRoot);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(proposals);
        conversationId ??= static () => SwarmCore.Agent.Runtime.AsyncLocalCurrentTurnContextAccessor.Ambient?.ConversationId;

        var search = ToolDescriptor.Create<RepoMemorySearchInput, RepoMemorySearchOutput>(
            SearchToolId, ownerAgentId,
            "Search the repository's own memory (MEMORY.md and memory/*.md topic files kept in git) for conventions, decisions and " +
            "project knowledge. Returns matching lines with file and line number; read the topic file for detail. An empty query lists the topic files. " +
            "Input: { query: string, limit?: int }.",
            ToolCostClass.Cheap, ToolSideEffect.ReadOnly,
            (input, ct) => Task.Run(() =>
            {
                var o = options();
                if (!o.Enabled || repositoryRoot() is not { } root) return new RepoMemorySearchOutput([], [], "Repository memory is not available.");
                if (string.IsNullOrWhiteSpace(input.Query))
                {
                    var topics = RepoMemoryFiles.ListTopics(root, o);
                    return new RepoMemorySearchOutput([], topics, topics.Count == 0 ? "This repository has no memory topic files." : $"{topics.Count} topic file(s).");
                }
                var hits = RepoMemoryFiles.Search(root, input.Query, o, input.Limit);
                return new RepoMemorySearchOutput(hits, [], hits.Count == 0 ? "No repository memory matches." : $"{hits.Count} match(es); read a file for context.");
            }, ct),
            keywords: ["memory", "convention", "decision", "how do we", "repository knowledge", "MEMORY.md"]);

        var read = ToolDescriptor.Create<RepoMemoryReadInput, RepoMemoryReadResult>(
            ReadToolId, ownerAgentId,
            "Read a repository memory file (MEMORY.md or memory/<topic>.md) by line range. Use after repo-memory.search. " +
            "Input: { path: string, startLine?: int (1-based), maxLines?: int }.",
            ToolCostClass.Cheap, ToolSideEffect.ReadOnly,
            (input, ct) => Task.Run(() =>
            {
                var o = options();
                if (!o.Enabled || repositoryRoot() is not { } root) throw new InvalidOperationException("Repository memory is not available.");
                return RepoMemoryFiles.Read(root, input.Path, o, input.StartLine, input.MaxLines);
            }, ct),
            keywords: ["memory", "read memory", "topic"]);

        var propose = ToolDescriptor.Create<RepoMemoryProposeInput, RepoMemoryProposeOutput>(
            ProposeToolId, ownerAgentId,
            "Propose an edit to repository memory (MEMORY.md or memory/<topic>.md) when this turn established a durable convention, decision or " +
            "gotcha future work needs. The edit is stored as a reviewable diff for the person; it is NOT written to the repository and NOT committed. " +
            "Update the existing canonical passage (oldText = exact unique passage, newText = replacement) rather than appending a competing note; " +
            "leave oldText empty to append, or to create a new topic file. Never include secrets or transient status. " +
            "Input: { path: string, oldText?: string, newText: string, rationale: string }.",
            ToolCostClass.Cheap, ToolSideEffect.WritesLocal,
            async (input, ct) =>
            {
                var o = options();
                if (!o.Enabled || repositoryRoot() is not { } root) return new RepoMemoryProposeOutput(false, "Repository memory is not available.", null, null);
                var result = await RepoMemoryProposals.ProposeAsync(proposals, root, o, input.Path, input.OldText, input.NewText, input.Rationale,
                    conversationId(), ct).ConfigureAwait(false);
                return new RepoMemoryProposeOutput(result.Ok, result.Message, result.Proposal?.Id.ToString("N"), result.Proposal?.Diff);
            },
            keywords: ["memory", "remember convention", "update MEMORY.md", "propose memory"],
            formatResultForUser: r => r.Ok ? "Proposed a repository memory edit for your review (not written or committed):\n```diff\n" + r.Diff + "```" : r.Message);

        return [search, read, propose];
    }
}
