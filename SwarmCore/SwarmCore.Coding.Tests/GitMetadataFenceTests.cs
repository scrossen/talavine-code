using System.Diagnostics;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Tools;
using SwarmCore.Missions;

namespace SwarmCore.Coding.Tests;

/// <summary>
/// A coding turn can write files in its workspace, so the repository's own git machinery must not turn a file
/// write into command execution: a hook written into <c>.git/hooks</c> never runs from <c>git.commit</c>, a
/// <c>core.fsmonitor</c> command written into <c>.git/config</c> never runs from <c>git.status</c>, and a coding
/// turn cannot write <c>.git/</c>, <c>.gitmodules</c> or <c>.gitattributes</c> at all.
/// </summary>
public sealed class GitMetadataFenceTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("git-fence-");
    private readonly string _repo;
    private readonly AsyncLocalCurrentTurnContextAccessor _accessor = new();

    public GitMetadataFenceTests()
    {
        _repo = Directory.CreateDirectory(Path.Combine(_root.FullName, "repo")).FullName;
        Git(_repo, "init", "-q");
        Git(_repo, "config", "user.name", "Test");
        Git(_repo, "config", "user.email", "t@example.com");
        Git(_repo, "config", "commit.gpgsign", "false");
    }

    public void Dispose()
    {
        _accessor.Current = null;
        foreach (var file in _root.EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
        _root.Delete(recursive: true);
    }

    private SandboxPathResolver Sandbox(bool allowGitMetadata = false) => new(Options.Create(new SandboxOptions
    {
        Root = Path.Combine(_root.FullName, "sandbox"),
        AllowGitMetadataWritesInTurnWorkspace = allowGitMetadata,
    }));

    private void WorkspaceTurn(bool unattended = true) => _accessor.Current = new CurrentTurnContext(
        "chat", null, Guid.NewGuid(), CommunicationScopeKind: AgentCommunicationScopeKind.TrustedProfileWide,
        CommunicationScopeAuthority: "user", CommunicationScopeUserInitiated: true, UnattendedAgentTurn: unattended)
    { WorkspaceRoot = _repo };

    private static string Sh(string path) => path.Replace('\\', '/');

    [Fact]
    public async Task A_hook_written_into_the_repository_never_runs_from_git_commit()
    {
        var marker = Path.Combine(_root.FullName, "hook-ran.txt");
        var hooks = Directory.CreateDirectory(Path.Combine(_repo, ".git", "hooks")).FullName;
        var hook = Path.Combine(hooks, "pre-commit");
        await File.WriteAllTextAsync(hook, $"#!/bin/sh\necho ran > \"{Sh(marker)}\"\nexit 0\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "a");

        WorkspaceTurn();
        var commit = (GitCommitTool.Output)(await new GitCommitTool(Sandbox()).Descriptor.Invoke(
            new GitCommitTool.Input { Message = "harness commit", Paths = ["a.txt"] }, default))!;
        Assert.True(commit.Succeeded, commit.Error);
        Assert.False(File.Exists(marker), "the repository's pre-commit hook ran from git.commit");

        // Control: plain git in the same repository does run the hook, so the check above is meaningful.
        await File.WriteAllTextAsync(Path.Combine(_repo, "b.txt"), "b");
        Git(_repo, "add", "b.txt");
        Git(_repo, "commit", "-q", "-m", "plain commit");
        Assert.True(File.Exists(marker), "control: plain git did not run the hook, so this machine cannot prove the fence");
    }

    [Fact]
    public async Task An_fsmonitor_command_in_the_repository_config_never_runs_from_git_status_or_diff()
    {
        var marker = Path.Combine(_root.FullName, "fsmonitor-ran.txt");
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "a");
        Git(_repo, "add", "a.txt");
        Git(_repo, "commit", "-q", "-m", "base");
        Git(_repo, "config", "core.fsmonitor", $"echo ran > \"{Sh(marker)}\" ; true");
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "changed");

        WorkspaceTurn();
        var sandbox = Sandbox();
        var status = (GitStatusTool.Output)(await new GitStatusTool(sandbox).Descriptor.Invoke(new GitStatusTool.Input(), default))!;
        Assert.True(status.Succeeded, status.Error);
        var diff = await new GitDiffTool(sandbox).Descriptor.Invoke(new GitDiffTool.Input(), default);
        Assert.NotNull(diff);
        Assert.False(File.Exists(marker), "core.fsmonitor from the repository config ran from a harness git tool");

        // Control: plain git status runs it.
        Git(_repo, "status", "--porcelain");
        Assert.True(File.Exists(marker), "control: plain git did not run core.fsmonitor, so this machine cannot prove the fence");
    }

    [Theory]
    [InlineData(".git/config")]
    [InlineData(".git/hooks/pre-commit")]
    [InlineData(".GIT/hooks/post-checkout")]
    [InlineData("sub/.git/config")]
    [InlineData(".gitmodules")]
    [InlineData(".gitattributes")]
    [InlineData("src/.gitattributes")]
    public async Task A_coding_turn_cannot_write_or_edit_git_metadata(string relative)
    {
        var target = Path.Combine(_repo, relative.Replace('/', Path.DirectorySeparatorChar));
        var existed = File.Exists(target);
        if (!existed)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, "[core]\n");
        }
        var before = await File.ReadAllTextAsync(target);

        foreach (var unattended in new[] { true, false })
        {
            WorkspaceTurn(unattended);
            var sandbox = Sandbox();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new EditFileTool(sandbox).Descriptor.Invoke(
                new EditFileTool.Input { Path = relative, OldText = before.Split('\n')[0], NewText = "[core]\n\tfsmonitor = evil" }, default));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new WriteFileTool(sandbox).Descriptor.Invoke(
                new WriteFileTool.Input { Path = relative, Content = "evil" }, default));
            // Absolute spelling is refused the same way.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new WriteFileTool(sandbox).Descriptor.Invoke(
                new WriteFileTool.Input { Path = target, Content = "evil" }, default));
        }
        Assert.Equal(before, await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task Git_metadata_stays_readable_and_ordinary_files_stay_writable()
    {
        WorkspaceTurn();
        var sandbox = Sandbox();
        Assert.NotNull(sandbox.Resolve(".git/config"));
        Assert.NotNull(sandbox.Resolve(".gitattributes"));
        await new WriteFileTool(sandbox).Descriptor.Invoke(new WriteFileTool.Input { Path = ".github/workflows/ci.yml", Content = "on: push" }, default);
        await new WriteFileTool(sandbox).Descriptor.Invoke(new WriteFileTool.Input { Path = ".gitignore", Content = "bin/" }, default);
        await new WriteFileTool(sandbox).Descriptor.Invoke(new WriteFileTool.Input { Path = "src/legit.git.txt", Content = "x" }, default);
        Assert.True(File.Exists(Path.Combine(_repo, ".github", "workflows", "ci.yml")));
    }

    [Fact]
    public async Task An_explicit_host_option_lets_a_workspace_turn_write_git_metadata()
    {
        WorkspaceTurn(unattended: false);
        await new WriteFileTool(Sandbox(allowGitMetadata: true)).Descriptor.Invoke(
            new WriteFileTool.Input { Path = ".gitattributes", Content = "* text=auto" }, default);
        Assert.Equal("* text=auto", await File.ReadAllTextAsync(Path.Combine(_repo, ".gitattributes")));
    }

    private static void Git(string dir, params string[] args) => SwarmCore.Testing.TestGit.Run(dir, args);
}
