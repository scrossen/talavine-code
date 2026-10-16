using System.Security.Cryptography;
using System.Text;
using SwarmCore.Coding.Tools;

namespace SwarmCore.Coding.Agents;

/// <summary>How an agent's files are separated from the session that started it.</summary>
public static class AgentIsolation
{
    /// <summary>The agent works in its own git worktree on a new branch from the parent's current HEAD.</summary>
    public const string Worktree = "worktree";

    /// <summary>Null (shared workspace) or <see cref="Worktree"/>; anything else is refused.</summary>
    public static string? Normalize(string? isolation)
    {
        if (string.IsNullOrWhiteSpace(isolation)) return null;
        var value = isolation.Trim().ToLowerInvariant();
        return value switch
        {
            "none" or "shared" => null,
            Worktree => Worktree,
            _ => throw new ArgumentException($"Unknown isolation \"{isolation.Trim()}\". Use \"worktree\", or leave it out to share this workspace."),
        };
    }
}

/// <summary>An agent's own git worktree. Paths are absolute; the branch lives in the parent's repository.</summary>
public sealed record AgentWorktree
{
    /// <summary>The worktree's top folder (outside the repository, under the host's data folder).</summary>
    public required string Path { get; init; }
    /// <summary>Where the agent works: <see cref="Path"/>, or the same subfolder the parent worked in.</summary>
    public required string WorkingRoot { get; init; }
    public required string Branch { get; init; }
    /// <summary>The work tree the branch was made from (the parent's repository or its own worktree).</summary>
    public required string RepositoryRoot { get; init; }
    public required string BaseCommit { get; init; }
    /// <summary>Last inspection (the host refreshes it when a turn ends). Null: not checked yet or unreadable.</summary>
    public bool? HasUncommittedChanges { get; init; }
    /// <summary>Commits on <see cref="Branch"/> since <see cref="BaseCommit"/> at the last inspection.</summary>
    public int? CommitsAhead { get; init; }
}

public sealed record AgentWorktreeStatus(bool Exists, bool? HasUncommittedChanges, int? CommitsAhead, bool? MergedIntoParent);

/// <summary>What happened to a worktree when its agent was removed.</summary>
public sealed record AgentWorktreeCleanup(bool Removed, string Message);

/// <summary>
/// Git worktrees for isolated agents, host-neutral. Creation adds a worktree on a new branch from the parent's HEAD
/// in a folder the host chooses (outside the repository). Removal never discards work: it uses git's own safe
/// operations (<c>git worktree remove</c> without force, <c>git branch -d</c>) and only after checking that the
/// worktree has no uncommitted or untracked changes and that its branch is merged into the parent's current HEAD;
/// otherwise the worktree and branch are kept and the reason is returned.
/// </summary>
public static class AgentWorktrees
{
    public const string BranchPrefix = "swarmcode/agent-";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <param name="branchPrefix">Branch name prefix; null is the agent prefix. A cycle host passes its own (for example <c>swarmcode/</c>).</param>
    public static async Task<AgentWorktree> CreateAsync(string sourceRoot, string worktreesRoot, string name, CancellationToken ct = default, string? branchPrefix = null)
    {
        var effectivePrefix = string.IsNullOrWhiteSpace(branchPrefix) ? BranchPrefix : branchPrefix;
        if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
            throw new InvalidOperationException("The session starting this agent has no workspace folder to isolate.");
        var (exit, top, stderr) = await GitProcess.RunAsync(sourceRoot, ["rev-parse", "--show-toplevel"], Timeout, ct).ConfigureAwait(false);
        if (exit != 0 || string.IsNullOrWhiteSpace(top))
            throw new InvalidOperationException(
                $"isolation \"worktree\" needs a git repository, and '{sourceRoot}' is not in one"
                + (exit == -1 ? " (git could not run: " + GitProcess.ErrorText(exit, top, stderr) + ")" : "")
                + ". Start the agent without isolation, or put the folder under git and commit first.");
        var repository = System.IO.Path.GetFullPath(GitProcess.FirstLine(top));
        var (_, prefix, _) = await GitProcess.RunAsync(sourceRoot, ["rev-parse", "--show-prefix"], Timeout, ct).ConfigureAwait(false);
        var (headExit, head, _) = await GitProcess.RunAsync(repository, ["rev-parse", "--verify", "HEAD"], Timeout, ct).ConfigureAwait(false);
        if (headExit != 0 || string.IsNullOrWhiteSpace(head))
            throw new InvalidOperationException($"isolation \"worktree\" needs a commit to branch from, and '{repository}' has none yet. Commit once, then start the agent.");
        var baseCommit = GitProcess.FirstLine(head);

        var safeName = SafeSegment(name);
        var branch = effectivePrefix + safeName;
        for (var i = 2; await BranchExistsAsync(repository, branch, ct).ConfigureAwait(false); i++)
        {
            if (i > 50) throw new InvalidOperationException("Could not find a free branch name for the agent.");
            branch = $"{effectivePrefix}{safeName}-{i}";
        }
        var parent = System.IO.Path.Combine(System.IO.Path.GetFullPath(worktreesRoot), RepositoryFolder(repository));
        var path = System.IO.Path.Combine(parent, safeName);
        for (var i = 2; Directory.Exists(path) || File.Exists(path); i++)
        {
            if (i > 50) throw new InvalidOperationException("Could not find a free folder for the agent's worktree.");
            path = System.IO.Path.Combine(parent, $"{safeName}-{i}");
        }
        Directory.CreateDirectory(parent);

        var (addExit, addOut, addErr) = await GitProcess.RunAsync(repository, ["worktree", "add", "-b", branch, path, baseCommit], Timeout, ct).ConfigureAwait(false);
        if (addExit != 0)
            throw new InvalidOperationException("git could not create the agent's worktree: " + GitProcess.ErrorText(addExit, addOut, addErr));
        var relative = GitProcess.FirstLine(prefix).Trim('/', '\\');
        var working = relative.Length == 0 ? path : System.IO.Path.GetFullPath(System.IO.Path.Combine(path, relative));
        if (!Directory.Exists(working)) working = path;
        return new AgentWorktree
        {
            Path = path, WorkingRoot = working, Branch = branch, RepositoryRoot = repository, BaseCommit = baseCommit,
            HasUncommittedChanges = false, CommitsAhead = 0,
        };
    }

    public static async Task<AgentWorktreeStatus> InspectAsync(AgentWorktree worktree, CancellationToken ct = default)
    {
        var exists = Directory.Exists(worktree.Path);
        bool? dirty = null;
        if (exists)
        {
            var (exit, stdout, _) = await GitProcess.RunAsync(worktree.Path, ["status", "--porcelain", "--untracked-files=all"], Timeout, ct).ConfigureAwait(false);
            if (exit == 0) dirty = stdout.Trim().Length > 0;
        }
        int? ahead = null;
        bool? merged = null;
        if (Directory.Exists(worktree.RepositoryRoot))
        {
            var (countExit, count, _) = await GitProcess.RunAsync(worktree.RepositoryRoot, ["rev-list", "--count", $"{worktree.BaseCommit}..refs/heads/{worktree.Branch}"], Timeout, ct).ConfigureAwait(false);
            if (countExit == 0 && int.TryParse(count.Trim(), out var n)) ahead = n;
            var (mergedExit, _, _) = await GitProcess.RunAsync(worktree.RepositoryRoot, ["merge-base", "--is-ancestor", $"refs/heads/{worktree.Branch}", "HEAD"], Timeout, ct).ConfigureAwait(false);
            merged = mergedExit switch { 0 => true, 1 => false, _ => null };
        }
        return new AgentWorktreeStatus(exists, dirty, ahead, merged);
    }

    /// <summary>Removes the worktree and its branch only when nothing would be lost; otherwise keeps both and says why.</summary>
    public static async Task<AgentWorktreeCleanup> TryRemoveAsync(AgentWorktree worktree, CancellationToken ct = default)
    {
        var status = await InspectAsync(worktree, ct).ConfigureAwait(false);
        var where = $"{worktree.Path} (branch {worktree.Branch})";
        if (status.Exists && status.HasUncommittedChanges != false)
            return new(false, status.HasUncommittedChanges == true
                ? $"Kept the agent's worktree {where}: it has uncommitted changes."
                : $"Kept the agent's worktree {where}: its changes could not be checked.");
        if (status.MergedIntoParent != true)
            return new(false, status.MergedIntoParent == false
                ? $"Kept the agent's worktree {where}: {Plural(status.CommitsAhead)} not merged into {worktree.RepositoryRoot}. Merge or delete the branch with git, then remove the worktree."
                : $"Kept the agent's worktree {where}: could not check whether its branch is merged.");

        if (status.Exists)
        {
            var (exit, stdout, stderr) = await GitProcess.RunAsync(worktree.RepositoryRoot, ["worktree", "remove", worktree.Path], Timeout, ct).ConfigureAwait(false);
            if (exit != 0) return new(false, $"Kept the agent's worktree {where}: " + GitProcess.ErrorText(exit, stdout, stderr));
        }
        else await GitProcess.RunAsync(worktree.RepositoryRoot, ["worktree", "prune"], Timeout, ct).ConfigureAwait(false);

        var (branchExit, branchOut, branchErr) = await GitProcess.RunAsync(worktree.RepositoryRoot, ["branch", "-d", worktree.Branch], Timeout, ct).ConfigureAwait(false);
        return branchExit == 0
            ? new(true, $"Removed the agent's worktree {where}; its branch was merged.")
            : new(true, $"Removed the agent's worktree {worktree.Path}; kept branch {worktree.Branch}: " + GitProcess.ErrorText(branchExit, branchOut, branchErr));
    }

    private static string Plural(int? commits) => commits switch
    {
        null => "its commits are",
        1 => "1 commit is",
        _ => $"{commits} commits are",
    };

    private static async Task<bool> BranchExistsAsync(string repository, string branch, CancellationToken ct)
        => (await GitProcess.RunAsync(repository, ["rev-parse", "--verify", "--quiet", "refs/heads/" + branch], Timeout, ct).ConfigureAwait(false)).ExitCode == 0;

    private static string RepositoryFolder(string repository)
    {
        var name = SafeSegment(System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(repository)));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(repository.ToLowerInvariant())))[..8].ToLowerInvariant();
        return $"{name}-{hash}";
    }

    private static string SafeSegment(string? text)
    {
        var chars = (text ?? "").Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        var value = new string(chars).Trim('-');
        if (value.Length > 40) value = value[..40].TrimEnd('-');
        return value.Length == 0 ? "agent" : value;
    }
}
