using SwarmCore.Coding.Tools;

namespace SwarmCore.Coding.SelfImprovement;

/// <summary>
/// The git steps a cycle host needs around the worktree the harness created: has the agent changed anything, commit
/// everything on the cycle's branch, fast-forward the source checkout when that is safe, and remove a worktree whose
/// branch produced nothing. All calls run through the hardened <see cref="GitProcess"/> (drivers blanked, bounded time).
/// </summary>
public static class SelfImprovementGit
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    public static async Task<bool> HasChangesAsync(string worktreePath, CancellationToken ct = default)
    {
        var (exit, stdout, stderr) = await GitProcess.RunAsync(worktreePath, ["status", "--porcelain", "--untracked-files=all"], Timeout, ct).ConfigureAwait(false);
        if (exit != 0) throw new InvalidOperationException("git status failed: " + GitProcess.ErrorText(exit, stdout, stderr));
        return stdout.Trim().Length > 0;
    }

    /// <summary>Stages every change in the worktree and commits it; returns the commit hash.</summary>
    public static async Task<string> CommitAllAsync(string worktreePath, string message, CancellationToken ct = default)
    {
        var (addExit, addOut, addErr) = await GitProcess.RunAsync(worktreePath, ["add", "-A"], Timeout, ct).ConfigureAwait(false);
        if (addExit != 0) throw new InvalidOperationException("git add failed: " + GitProcess.ErrorText(addExit, addOut, addErr));
        var args = new List<string>();
        // The person's own identity when git has one; otherwise the commit says who made it.
        if (!await HasConfigAsync(worktreePath, "user.name", ct).ConfigureAwait(false)) args.AddRange(["-c", "user.name=Talavine Code"]);
        if (!await HasConfigAsync(worktreePath, "user.email", ct).ConfigureAwait(false)) args.AddRange(["-c", "user.email=swarmcode@localhost"]);
        args.AddRange(["commit", "--no-verify", "-q", "-m", message]);
        var (commitExit, commitOut, commitErr) = await GitProcess.RunAsync(worktreePath, args.ToArray(), Timeout, ct).ConfigureAwait(false);
        if (commitExit != 0) throw new InvalidOperationException("git commit failed: " + GitProcess.ErrorText(commitExit, commitOut, commitErr));
        var (headExit, head, headErr) = await GitProcess.RunAsync(worktreePath, ["rev-parse", "--verify", "HEAD"], Timeout, ct).ConfigureAwait(false);
        if (headExit != 0) throw new InvalidOperationException("git rev-parse failed: " + GitProcess.ErrorText(headExit, head, headErr));
        return GitProcess.FirstLine(head);
    }

    /// <summary>
    /// Fast-forwards the checkout to the branch, only when the checkout has no uncommitted changes and its HEAD is still
    /// the commit the branch started from. Anything else is left for the person; the message says why.
    /// </summary>
    public static async Task<(bool Merged, string Message)> TryFastForwardAsync(string repositoryRoot, string branch, string baseCommit, CancellationToken ct = default)
    {
        var (statusExit, status, statusErr) = await GitProcess.RunAsync(repositoryRoot, ["status", "--porcelain"], Timeout, ct).ConfigureAwait(false);
        if (statusExit != 0) return (false, "Not merged: git status failed in the source checkout (" + GitProcess.ErrorText(statusExit, status, statusErr) + ").");
        if (status.Trim().Length > 0) return (false, "Not merged: the source checkout has uncommitted changes. Merge the branch yourself when ready.");
        var (headExit, head, _) = await GitProcess.RunAsync(repositoryRoot, ["rev-parse", "--verify", "HEAD"], Timeout, ct).ConfigureAwait(false);
        if (headExit != 0 || !string.Equals(GitProcess.FirstLine(head), baseCommit, StringComparison.OrdinalIgnoreCase))
            return (false, "Not merged: the source checkout moved on since this cycle started. Merge the branch yourself when ready.");
        var (mergeExit, mergeOut, mergeErr) = await GitProcess.RunAsync(repositoryRoot, ["merge", "--ff-only", "-q", "refs/heads/" + branch], Timeout, ct).ConfigureAwait(false);
        return mergeExit == 0
            ? (true, "Merged into the source checkout (fast-forward).")
            : (false, "Not merged: " + GitProcess.ErrorText(mergeExit, mergeOut, mergeErr));
    }

    /// <summary>The checkout's current commit.</summary>
    public static async Task<string?> HeadAsync(string repositoryRoot, CancellationToken ct = default)
    {
        var (exit, head, _) = await GitProcess.RunAsync(repositoryRoot, ["rev-parse", "--verify", "HEAD"], Timeout, ct).ConfigureAwait(false);
        return exit == 0 ? GitProcess.FirstLine(head) : null;
    }

    public static async Task<bool> ContainsCommitAsync(string repositoryRoot, string commit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(commit)) return false;
        var (exit, _, _) = await GitProcess.RunAsync(repositoryRoot, ["merge-base", "--is-ancestor", commit, "HEAD"], Timeout, ct).ConfigureAwait(false);
        return exit == 0;
    }

    /// <summary>Install only the exact independently verified commit; never create an untested merge tree.</summary>
    public static async Task<(bool Merged, string Message)> TryApplyVerifiedAsync(string repositoryRoot, string branch, string commit, CancellationToken ct = default)
    {
        var (branchExit, head, _) = await GitProcess.RunAsync(repositoryRoot, ["rev-parse", "--verify", "refs/heads/" + branch], Timeout, ct).ConfigureAwait(false);
        if (branchExit != 0 || !GitProcess.FirstLine(head).Equals(commit, StringComparison.OrdinalIgnoreCase))
            return (false, "Installation postponed: the repair branch changed after verification.");
        var (parentExit, parent, _) = await GitProcess.RunAsync(repositoryRoot, ["rev-parse", "--verify", commit + "^"], Timeout, ct).ConfigureAwait(false);
        if (parentExit != 0) return (false, "Installation postponed: the verified repair's parent could not be checked.");
        if (await HasChangesAsync(repositoryRoot, ct).ConfigureAwait(false)
            || !string.Equals(await HeadAsync(repositoryRoot, ct).ConfigureAwait(false), GitProcess.FirstLine(parent), StringComparison.OrdinalIgnoreCase))
            return (false, "Installation postponed: the source checkout changed after verification.");
        var (mergeExit, stdout, stderr) = await GitProcess.RunAsync(repositoryRoot, ["merge", "--ff-only", "-q", commit], Timeout, ct).ConfigureAwait(false);
        if (mergeExit != 0) return (false, "Installation postponed: " + GitProcess.ErrorText(mergeExit, stdout, stderr));
        return string.Equals(await HeadAsync(repositoryRoot, ct).ConfigureAwait(false), commit, StringComparison.OrdinalIgnoreCase)
            ? (true, "Merged exact verified repair commit (fast-forward).")
            : (false, "Installation postponed: source HEAD does not match the verified commit.");
    }

    /// <summary>
    /// Merges the branch into the checkout (fast-forward when possible, a merge commit otherwise). git itself refuses
    /// when uncommitted changes would be overwritten; a conflicting merge is aborted so the checkout is left as it was.
    /// </summary>
    public static async Task<(bool Merged, string Message)> MergeAsync(string repositoryRoot, string branch, string? message = null, CancellationToken ct = default)
    {
        var args = new List<string> { "merge", "--no-edit", "-q" };
        if (!string.IsNullOrWhiteSpace(message)) args.AddRange(["-m", message]);
        args.Add("refs/heads/" + branch);
        var (exit, stdout, stderr) = await GitProcess.RunAsync(repositoryRoot, args.ToArray(), Timeout, ct).ConfigureAwait(false);
        if (exit == 0) return (true, "Merged.");
        var (statusExit, status, _) = await GitProcess.RunAsync(repositoryRoot, ["status", "--porcelain"], Timeout, ct).ConfigureAwait(false);
        if (statusExit == 0 && status.Split('\n').Any(l => l.StartsWith("UU", StringComparison.Ordinal) || l.StartsWith("AA", StringComparison.Ordinal) || l.StartsWith("DU", StringComparison.Ordinal) || l.StartsWith("UD", StringComparison.Ordinal)))
            await GitProcess.RunAsync(repositoryRoot, ["merge", "--abort"], Timeout, CancellationToken.None).ConfigureAwait(false);
        else if (Directory.Exists(Path.Combine(repositoryRoot, ".git")) && File.Exists(Path.Combine(repositoryRoot, ".git", "MERGE_HEAD")))
            await GitProcess.RunAsync(repositoryRoot, ["merge", "--abort"], Timeout, CancellationToken.None).ConfigureAwait(false);
        return (false, "Not merged: " + GitProcess.ErrorText(exit, stdout, stderr));
    }

    /// <summary>Removes a worktree and deletes its branch; with <paramref name="force"/> even when it has changes.</summary>
    public static async Task<string?> RemoveWorktreeAsync(string repositoryRoot, string worktreePath, string branch, bool force, CancellationToken ct = default)
    {
        var problems = new List<string>();
        if (Directory.Exists(worktreePath))
        {
            var args = force ? new[] { "worktree", "remove", "--force", worktreePath } : ["worktree", "remove", worktreePath];
            var (exit, stdout, stderr) = await GitProcess.RunAsync(repositoryRoot, args, Timeout, ct).ConfigureAwait(false);
            if (exit != 0) problems.Add(GitProcess.ErrorText(exit, stdout, stderr));
        }
        else await GitProcess.RunAsync(repositoryRoot, ["worktree", "prune"], Timeout, ct).ConfigureAwait(false);
        if (problems.Count == 0)
        {
            var (branchExit, branchOut, branchErr) = await GitProcess.RunAsync(repositoryRoot, [ "branch", force ? "-D" : "-d", branch ], Timeout, ct).ConfigureAwait(false);
            if (branchExit != 0) problems.Add(GitProcess.ErrorText(branchExit, branchOut, branchErr));
        }
        return problems.Count == 0 ? null : string.Join(" ", problems);
    }

    private static async Task<bool> HasConfigAsync(string path, string key, CancellationToken ct)
    {
        var (exit, stdout, _) = await GitProcess.RunAsync(path, ["config", "--get", key], Timeout, ct).ConfigureAwait(false);
        return exit == 0 && stdout.Trim().Length > 0;
    }
}
