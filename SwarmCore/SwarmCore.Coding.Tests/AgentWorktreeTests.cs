using SwarmCore.Coding.Agents;
using SwarmCore.Testing;

namespace SwarmCore.Coding.Tests;

/// <summary>Isolated agents' git worktrees against real temporary repositories: creation, reporting and safe cleanup.</summary>
public sealed class AgentWorktreeTests : IDisposable
{
    private readonly string _tmp = SwarmCore.Missions.SandboxPathResolver.ResolvePhysicalPath(Directory.CreateTempSubdirectory("agent-worktrees-").FullName);
    private string Repo => Path.Combine(_tmp, "repo");
    private string Worktrees => Path.Combine(_tmp, "data", "worktrees");

    public void Dispose()
    {
        // Worktrees hold files git marks read-only; clear attributes so the temp tree can go.
        try
        {
            foreach (var file in Directory.EnumerateFiles(_tmp, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_tmp, recursive: true);
        }
        catch { /* best effort */ }
    }

    private static void Git(string dir, params string[] args)
    {
        var result = TestGit.Run(dir, args);
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', args)}: {result.Error}{result.Output}");
    }

    private static string GitOut(string dir, params string[] args) => TestGit.Run(dir, args).Output.Trim();

    private void InitRepo(bool commit = true)
    {
        Directory.CreateDirectory(Path.Combine(Repo, "src"));
        Git(Repo, "init", "-q");
        Git(Repo, "config", "user.name", "Test");
        Git(Repo, "config", "user.email", "test@example.invalid");
        Git(Repo, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(Repo, "src", "a.txt"), "base");
        if (!commit) return;
        Git(Repo, "add", ".");
        Git(Repo, "commit", "-qm", "base");
    }

    [GitFact]
    public async Task Creates_a_worktree_on_a_new_branch_from_HEAD_outside_the_repository()
    {
        InitRepo();
        var head = GitOut(Repo, "rev-parse", "HEAD");

        var wt = await AgentWorktrees.CreateAsync(Repo, Worktrees, "abc123");

        Assert.StartsWith(AgentWorktrees.BranchPrefix, wt.Branch);
        Assert.Equal(head, wt.BaseCommit);
        Assert.StartsWith(Path.GetFullPath(Worktrees), wt.Path, StringComparison.OrdinalIgnoreCase);
        Assert.False(wt.Path.StartsWith(Repo + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(wt.Path, wt.WorkingRoot);
        Assert.True(File.Exists(Path.Combine(wt.Path, "src", "a.txt")));
        Assert.Equal(wt.Branch, GitOut(wt.Path, "rev-parse", "--abbrev-ref", "HEAD"));
        Assert.Equal(head, GitOut(wt.Path, "rev-parse", "HEAD"));

        // A second agent gets its own branch and folder.
        var other = await AgentWorktrees.CreateAsync(Repo, Worktrees, "abc123");
        Assert.NotEqual(wt.Branch, other.Branch);
        Assert.NotEqual(wt.Path, other.Path);
    }

    [GitFact]
    public async Task Working_root_follows_the_parents_subfolder()
    {
        InitRepo();
        var wt = await AgentWorktrees.CreateAsync(Path.Combine(Repo, "src"), Worktrees, "sub");
        Assert.Equal(Path.Combine(wt.Path, "src"), wt.WorkingRoot, ignoreCase: true);
    }

    [GitFact]
    public async Task Non_git_folder_and_repository_without_commits_fail_clearly()
    {
        var plain = Path.Combine(_tmp, "plain");
        Directory.CreateDirectory(plain);
        var notGit = await Assert.ThrowsAsync<InvalidOperationException>(() => AgentWorktrees.CreateAsync(plain, Worktrees, "x"));
        Assert.Contains("needs a git repository", notGit.Message);

        InitRepo(commit: false);
        var empty = await Assert.ThrowsAsync<InvalidOperationException>(() => AgentWorktrees.CreateAsync(Repo, Worktrees, "x"));
        Assert.Contains("has none yet", empty.Message);
        Assert.False(Directory.Exists(Worktrees)); // nothing was created
    }

    [GitFact]
    public async Task Cleanup_keeps_uncommitted_work()
    {
        InitRepo();
        var wt = await AgentWorktrees.CreateAsync(Repo, Worktrees, "dirty");
        File.WriteAllText(Path.Combine(wt.Path, "new.txt"), "agent work"); // untracked counts

        var status = await AgentWorktrees.InspectAsync(wt);
        Assert.True(status.HasUncommittedChanges);
        var cleanup = await AgentWorktrees.TryRemoveAsync(wt);

        Assert.False(cleanup.Removed);
        Assert.Contains("uncommitted changes", cleanup.Message);
        Assert.True(File.Exists(Path.Combine(wt.Path, "new.txt")));
        Assert.Equal(0, TestGit.Run(Repo, "rev-parse", "--verify", "refs/heads/" + wt.Branch).ExitCode);
    }

    [GitFact]
    public async Task Cleanup_keeps_an_unmerged_branch_and_removes_it_once_merged()
    {
        InitRepo();
        var wt = await AgentWorktrees.CreateAsync(Repo, Worktrees, "work");
        File.WriteAllText(Path.Combine(wt.Path, "src", "a.txt"), "agent change");
        Git(wt.Path, "commit", "-qam", "agent change");

        var status = await AgentWorktrees.InspectAsync(wt);
        Assert.False(status.HasUncommittedChanges);
        Assert.Equal(1, status.CommitsAhead);
        Assert.False(status.MergedIntoParent);
        var kept = await AgentWorktrees.TryRemoveAsync(wt);
        Assert.False(kept.Removed);
        Assert.Contains("not merged", kept.Message);
        Assert.True(Directory.Exists(wt.Path));

        // The parent reviews and merges with git itself; then cleanup removes worktree and branch.
        Git(Repo, "merge", "-q", "--no-edit", wt.Branch);
        var removed = await AgentWorktrees.TryRemoveAsync(wt);
        Assert.True(removed.Removed, removed.Message);
        Assert.False(Directory.Exists(wt.Path));
        Assert.NotEqual(0, TestGit.Run(Repo, "rev-parse", "--verify", "--quiet", "refs/heads/" + wt.Branch).ExitCode);
        Assert.Equal("agent change", File.ReadAllText(Path.Combine(Repo, "src", "a.txt")));
    }

    [GitFact]
    public async Task Cleanup_removes_an_untouched_worktree()
    {
        InitRepo();
        var wt = await AgentWorktrees.CreateAsync(Repo, Worktrees, "idle");
        var removed = await AgentWorktrees.TryRemoveAsync(wt);
        Assert.True(removed.Removed, removed.Message);
        Assert.False(Directory.Exists(wt.Path));
    }

    /// <summary>A host that creates real worktrees for isolated agents, like SwarmCode's.</summary>
    private sealed class WorktreeHost(string repo, string worktrees) : IAgentTeamHost
    {
        private int _next;
        public Dictionary<string, bool> Busy { get; } = new();
        public List<(string Id, string Message)> Started { get; } = [];
        public List<string> Delivered { get; } = [];

        public Task<string> CreateConversationAsync(string parentConversationId, string title, string? lane, string? model, CancellationToken ct)
            => Task.FromResult($"agent{Interlocked.Increment(ref _next)}");

        public async Task<AgentConversation> CreateAgentConversationAsync(AgentConversationRequest request, CancellationToken ct)
        {
            var id = await CreateConversationAsync(request.ParentConversationId, request.Title, request.Lane, request.Model, ct);
            return new AgentConversation(id, request.Isolation is null ? null : await AgentWorktrees.CreateAsync(repo, worktrees, id, ct));
        }

        public Task<bool> StartTurnAsync(string conversationId, string message, string? lane, string? model, CancellationToken ct)
        {
            lock (Started) Started.Add((conversationId, message));
            Busy[conversationId] = true;
            return Task.FromResult(true);
        }

        public void Cancel(string conversationId) { }
        public bool IsBusy(string conversationId) => Busy.GetValueOrDefault(conversationId);
        public Task<bool> DeliverAsync(string conversationId, string message, CancellationToken ct) { Delivered.Add(message); return Task.FromResult(true); }
    }

    [GitFact]
    public async Task Team_reports_branch_path_and_uncommitted_changes_and_keeps_dirty_work_on_forget()
    {
        InitRepo();
        var options = new AgentTeamOptions();
        var team = new AgentTeam(new InMemoryAgentTeamStore(), () => options);
        var host = new WorktreeHost(Repo, Worktrees);
        team.AttachHost(host);

        var agent = await team.SpawnAsync("root", new AgentSpawnRequest("Change a.txt", "Edit", Isolation: "worktree"));
        var wt = Assert.IsType<AgentWorktree>(agent.Worktree);
        var brief = Assert.Single(host.Started).Message;
        Assert.Contains(wt.Branch, brief);
        Assert.Contains(wt.WorkingRoot, brief);

        // The agent leaves an uncommitted edit; its finished turn reports it.
        File.WriteAllText(Path.Combine(wt.Path, "src", "a.txt"), "uncommitted");
        host.Busy[agent.Id] = false;
        await team.ReportTurnFinishedAsync(agent.Id, new AgentTurnOutcome(true, false, null, "done", 1));
        var view = AgentView.From(team.Get(agent.Id)!, DateTime.UtcNow);
        Assert.Equal(wt.Branch, view.Branch);
        Assert.Equal(wt.Path, view.WorktreePath);
        Assert.True(view.UncommittedChanges);
        var handBack = Assert.Single(host.Delivered);
        Assert.Contains("branch " + wt.Branch, handBack);
        Assert.Contains("uncommitted changes", handBack);
        Assert.Contains("git diff HEAD..." + wt.Branch, handBack);

        var forgotten = await team.ForgetWithCleanupAsync(agent.Id);
        Assert.True(forgotten.Forgotten);
        Assert.False(forgotten.Worktree!.Removed);
        Assert.Equal("uncommitted", File.ReadAllText(Path.Combine(wt.Path, "src", "a.txt")));
        await team.DisposeAsync();
    }

    [Fact]
    public async Task Team_refuses_isolation_from_a_host_that_cannot_isolate_and_unknown_values()
    {
        var options = new AgentTeamOptions();
        var team = new AgentTeam(new InMemoryAgentTeamStore(), () => options);
        team.AttachHost(new SharedOnlyHost());
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => team.SpawnAsync("root", new AgentSpawnRequest("t", Isolation: "worktree")));
        Assert.Contains("cannot start agents with isolation", refused.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => team.SpawnAsync("root", new AgentSpawnRequest("t", Isolation: "container")));
        Assert.Empty(team.Snapshot());
        // The tool surface turns it into a message the model reads.
        var spawn = AgentTeamTools.Create(team, () => "root").Single(t => t.ToolId == AgentTeamTools.SpawnToolId);
        var result = (AgentsResult)(await spawn.Invoke(new AgentsSpawnInput("t", Isolation: "worktree"), default))!;
        Assert.False(result.Success);
        Assert.Contains("isolation", result.Message);
        await team.DisposeAsync();
    }

    private sealed class SharedOnlyHost : IAgentTeamHost
    {
        public Task<string> CreateConversationAsync(string parentConversationId, string title, string? lane, string? model, CancellationToken ct) => Task.FromResult("agent1");
        public Task<bool> StartTurnAsync(string conversationId, string message, string? lane, string? model, CancellationToken ct) => Task.FromResult(true);
        public void Cancel(string conversationId) { }
        public bool IsBusy(string conversationId) => false;
        public Task<bool> DeliverAsync(string conversationId, string message, CancellationToken ct) => Task.FromResult(true);
    }
}
