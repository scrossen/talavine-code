using Microsoft.Extensions.DependencyInjection;
using SwarmCode.Hosting;
using SwarmCore.Coding.Tools;

namespace SwarmCode.Hosting.Tests;

/// <summary>
/// The data behind the desktop app's Changes panel (now the shared ChangesPanel): a workspace host's
/// <see cref="WorkingTreeChangesReader"/> over its own git tools, with the panel's former staged/unstaged
/// diff rules.
/// </summary>
public class ChangesPanelSourceTests : TempDirTest
{
    private string GitRepo()
    {
        var root = Path.Combine(Tmp, "repo");
        Directory.CreateDirectory(root);
        SwarmCore.Testing.TestGit.InitRepository(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "one\n");
        File.WriteAllText(Path.Combine(root, "b.txt"), "keep\n");
        Git(root, "add", "-A");
        Git(root, "commit", "-q", "-m", "init");
        return root;
    }

    [SwarmCore.Testing.GitFact]
    public async Task Workspace_host_reads_status_and_per_file_diffs_of_its_repository()
    {
        var root = GitRepo();
        // a.txt: staged change plus a further unstaged change; b.txt deleted; c.txt untracked.
        File.WriteAllText(Path.Combine(root, "a.txt"), "two\n");
        Git(root, "add", "a.txt");
        File.WriteAllText(Path.Combine(root, "a.txt"), "three\n");
        File.Delete(Path.Combine(root, "b.txt"));
        File.WriteAllText(Path.Combine(root, "c.txt"), "new\n");

        await using var host = await WorkspaceHost.CreateAsync(new SwarmCodeHostOptions { RepositoryRoot = root, DataPaths = Paths });
        var reader = host.Services.GetRequiredService<WorkingTreeChangesReader>();

        var status = await reader.GetStatusAsync(workspaceRoot: null);
        Assert.Null(status.Error);
        Assert.False(string.IsNullOrEmpty(status.Branch));
        var a = Assert.Single(status.Entries, e => e.Path == "a.txt");
        Assert.Equal("MM", a.Status);
        Assert.Contains(status.Entries, e => e.Path == "b.txt" && e.Status == " D");
        Assert.Contains(status.Entries, e => e.Path == "c.txt" && e.Status == "??");

        var aDiff = (await reader.GetFileDiffAsync(null, a.Path, a.Status)).Diff;
        Assert.Contains("+three", aDiff); // unstaged first
        Assert.Contains("+two", aDiff);   // then staged
        Assert.True(aDiff.IndexOf("+three", StringComparison.Ordinal) < aDiff.IndexOf("+two", StringComparison.Ordinal));
        Assert.Contains("-keep", (await reader.GetFileDiffAsync(null, "b.txt", " D")).Diff);
        Assert.Equal(new WorkingTreeFileDiff("", false), await reader.GetFileDiffAsync(null, "c.txt", "??"));
    }

    [SwarmCore.Testing.GitFact]
    public async Task An_explicit_workspace_root_is_read_instead_of_the_configured_root()
    {
        var root = GitRepo();
        var other = Path.Combine(Tmp, "plain");
        Directory.CreateDirectory(other);
        await using var host = await WorkspaceHost.CreateAsync(new SwarmCodeHostOptions { RepositoryRoot = other, DataPaths = Paths });
        var reader = host.Services.GetRequiredService<WorkingTreeChangesReader>();

        Assert.NotNull((await reader.GetStatusAsync(null)).Error); // the configured root is not a git repository
        File.WriteAllText(Path.Combine(root, "a.txt"), "changed\n");
        var status = await reader.GetStatusAsync(root);
        Assert.Null(status.Error);
        Assert.Contains(status.Entries, e => e.Path == "a.txt");
        Assert.Null(SwarmCore.Agent.Runtime.AsyncLocalCurrentTurnContextAccessor.Ambient); // the workspace context does not leak
    }

    [SwarmCore.Testing.GitFact]
    public async Task A_subfolder_workspace_lists_only_its_own_changes_relative_to_itself_and_diffs_them()
    {
        var root = GitRepo();
        var sub = Path.Combine(root, "pkg", "lib");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "inside.txt"), "one\n");
        Git(root, "add", "-A");
        Git(root, "commit", "-q", "-m", "sub");
        File.WriteAllText(Path.Combine(sub, "inside.txt"), "two\n");
        File.WriteAllText(Path.Combine(sub, "fresh.txt"), "new\n");
        File.WriteAllText(Path.Combine(root, "a.txt"), "outside change\n"); // outside the workspace folder

        await using var host = await WorkspaceHost.CreateAsync(new SwarmCodeHostOptions { RepositoryRoot = root, DataPaths = Paths });
        var reader = host.Services.GetRequiredService<WorkingTreeChangesReader>();

        var status = await reader.GetStatusAsync(sub);
        Assert.Null(status.Error);
        Assert.Equal(["fresh.txt", "inside.txt"], status.Entries.Select(e => e.Path).OrderBy(p => p, StringComparer.Ordinal));
        var inside = status.Entries.Single(e => e.Path == "inside.txt");
        var diff = await reader.GetFileDiffAsync(sub, inside.Path, inside.Status);
        Assert.Contains("-one", diff.Diff);
        Assert.Contains("+two", diff.Diff);
        Assert.DoesNotContain("outside change", diff.Diff);
        Assert.False(diff.Truncated);
    }

    [SwarmCore.Testing.GitFact]
    public async Task A_diff_cut_at_the_tools_byte_limit_reports_truncation()
    {
        var root = GitRepo();
        File.WriteAllText(Path.Combine(root, "a.txt"), string.Join("\n", Enumerable.Range(0, 20000).Select(i => "line number " + i)));
        await using var host = await WorkspaceHost.CreateAsync(new SwarmCodeHostOptions { RepositoryRoot = root, DataPaths = Paths });
        var reader = host.Services.GetRequiredService<WorkingTreeChangesReader>();

        var diff = await reader.GetFileDiffAsync(null, "a.txt", " M");
        Assert.True(diff.Truncated);
        Assert.NotEmpty(diff.Diff);
    }

    private static void Git(string dir, params string[] args) => SwarmCore.Testing.TestGit.RunChecked(dir, args);
}
