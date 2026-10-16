using SwarmCore.Agent.Runtime;
using SwarmCore.Missions;

namespace SwarmCore.Coding.Tools;

/// <summary>One file's diff for a Changes panel; <see cref="Truncated"/> when <c>git.diff</c> cut it at its byte limit.</summary>
public sealed record WorkingTreeFileDiff(string Diff, bool Truncated);

/// <summary>
/// Read-only working-tree view for a host's Changes panel: branch, changed files and a per-file diff.
/// It runs the published <c>git.status</c>/<c>git.diff</c> tools (the same sandboxed git helper and
/// mission fence the agent uses), never a separate git invocation, so a UI cannot read more than the
/// tools can. With a <c>workspaceRoot</c> the call runs inside an ambient turn context carrying only
/// that root, exactly as tools do during a coding turn (and as host acceptance does after one); without
/// it the host's configured sandbox root applies. The context lives only in this call's async flow.
/// <para>
/// The listing stays inside the workspace: a workspace may be a subfolder of a repository, and
/// <c>git status</c> reports paths from the repository root, so entries outside the workspace folder are
/// dropped and the rest are rewritten relative to it. Diffs run from the same folder with those relative
/// paths, which is how git resolves pathspecs.
/// </para>
/// </summary>
public sealed class WorkingTreeChangesReader(GitStatusTool status, GitDiffTool diff, ISandboxPathResolver sandbox)
{
    /// <summary>Conversation id recorded on the ambient context of a panel read (no turn, no authority).</summary>
    public const string PanelContextId = "changes-panel";

    public async Task<GitStatusTool.Output> GetStatusAsync(string? workspaceRoot, CancellationToken ct = default)
    {
        EnterWorkspace(workspaceRoot);
        var result = await status.Descriptor.Invoke(new GitStatusTool.Input(), ct).ConfigureAwait(false) as GitStatusTool.Output
                     ?? new GitStatusTool.Output { Error = "git.status returned no result." };
        if (result.Error is not null) return result;

        var prefix = await WorkspacePrefixAsync(ct).ConfigureAwait(false);
        if (prefix is null) return new GitStatusTool.Output { Error = "Could not locate the workspace inside its repository." };
        if (prefix.Length == 0) return result;
        var inside = result.Entries
            .Where(e => e.Path.StartsWith(prefix, StringComparison.Ordinal) && e.Path.Length > prefix.Length)
            .Select(e => new GitStatusTool.Entry { Status = e.Status, Path = e.Path[prefix.Length..] })
            .ToList();
        return new GitStatusTool.Output
        {
            Branch = result.Branch,
            Upstream = result.Upstream,
            Ahead = result.Ahead,
            Behind = result.Behind,
            Entries = inside,
            IsClean = inside.Count == 0,
            LocalProcessCompleted = result.LocalProcessCompleted,
        };
    }

    /// <summary>
    /// The unified diff for one changed file (a path from <see cref="GetStatusAsync"/>, relative to the
    /// workspace): unstaged changes first, then staged ones. Empty for an untracked or binary file.
    /// <paramref name="statusCode"/> is the two-character porcelain code.
    /// </summary>
    public async Task<WorkingTreeFileDiff> GetFileDiffAsync(string? workspaceRoot, string path, string statusCode, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        EnterWorkspace(workspaceRoot);
        var code = statusCode ?? "";
        var staged = code.Length > 0 && code[0] != ' ' && code[0] != '?';
        var unstaged = code.Length > 1 && code[1] != ' ' && code != "??";
        var text = new System.Text.StringBuilder();
        var truncated = false;
        if (unstaged
            && await diff.Descriptor.Invoke(new GitDiffTool.Input { Paths = [path] }, ct).ConfigureAwait(false) is GitDiffTool.Output working
            && working.Error is null)
        {
            text.Append(working.Diff);
            truncated |= working.Truncated;
        }
        if (staged
            && await diff.Descriptor.Invoke(new GitDiffTool.Input { Paths = [path], Staged = true }, ct).ConfigureAwait(false) is GitDiffTool.Output index
            && index.Error is null)
        {
            text.Append(index.Diff);
            truncated |= index.Truncated;
        }
        return new WorkingTreeFileDiff(text.ToString(), truncated);
    }

    /// <summary>The workspace folder relative to its repository root ("" at the root, "sub/dir/" below it), or null when git cannot say.</summary>
    private async Task<string?> WorkspacePrefixAsync(CancellationToken ct)
    {
        var dir = sandbox.Resolve(".", SwarmCore.Agent.Continual.MissionEffect.Inspect);
        await MissionPrimitiveFence.ValidateAsync(ct).ConfigureAwait(false);
        var (exit, stdout, _) = await GitProcess.RunAsync(dir, ["rev-parse", "--show-prefix"], TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        return exit == 0 ? stdout.Trim() : null;
    }

    private static void EnterWorkspace(string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return;
        // Set inside the calling async method: the value flows to the tool calls and is gone when it returns.
        new AsyncLocalCurrentTurnContextAccessor().Current = new CurrentTurnContext(PanelContextId, null, null)
        {
            WorkspaceRoot = workspaceRoot,
        };
    }
}
