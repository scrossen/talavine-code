using System.Diagnostics;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Git;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Tools;
using SwarmCore.Missions;
using Xunit;

namespace SwarmCore.Coding.Tests;

/// <summary>
/// A repository whose <c>.git/config</c> includes a tracked work-tree file (<c>include.path = ../shared.gitconfig</c>)
/// lets a coding turn rewrite that file with <c>fs.write_file</c> (it is not git metadata). Whatever it writes there,
/// the harness git tools must not run a command: a diff <c>textconv</c>, a <c>clean</c> filter or a signing
/// <c>gpg.program</c>. Each case has a plain-git control proving the configuration really does fire.
/// </summary>
public sealed class GitIncludedConfigFenceTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("git-include-fence-");
    private readonly string _repo;
    private readonly AsyncLocalCurrentTurnContextAccessor _accessor = new();

    public GitIncludedConfigFenceTests()
    {
        _repo = Directory.CreateDirectory(Path.Combine(_root.FullName, "repo")).FullName;
        Git(_repo, "init", "-q");
        Git(_repo, "config", "user.name", "Test");
        Git(_repo, "config", "user.email", "t@example.com");
        Git(_repo, "config", "commit.gpgsign", "false");
        // The repository's own config includes a tracked work-tree file; the include comes after the local values.
        Git(_repo, "config", "include.path", "../shared.gitconfig");
        File.WriteAllText(Path.Combine(_repo, "shared.gitconfig"), "# shared settings\n");
        File.WriteAllText(Path.Combine(_repo, ".gitattributes"), "a.txt diff=conv\nb.txt filter=evil\n");
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "a\n");
        File.WriteAllText(Path.Combine(_repo, "b.txt"), "b\n");
        Git(_repo, "add", ".");
        Git(_repo, "commit", "-q", "-m", "base");
    }

    public void Dispose()
    {
        _accessor.Current = null;
        foreach (var file in _root.EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
        _root.Delete(recursive: true);
    }

    private SandboxPathResolver Sandbox() => new(Options.Create(new SandboxOptions { Root = Path.Combine(_root.FullName, "sandbox") }));

    private void WorkspaceTurn() => _accessor.Current = new CurrentTurnContext(
        "chat", null, Guid.NewGuid(), CommunicationScopeKind: AgentCommunicationScopeKind.TrustedProfileWide,
        CommunicationScopeAuthority: "user", CommunicationScopeUserInitiated: true, UnattendedAgentTurn: true)
    { WorkspaceRoot = _repo };

    private static string Sh(string path) => path.Replace('\\', '/');

    /// <summary>The model rewrites the included file through the sandboxed file tool: it is not git metadata.</summary>
    private async Task ModelWritesSharedConfig(string content)
    {
        WorkspaceTurn();
        await new WriteFileTool(Sandbox()).Descriptor.Invoke(new WriteFileTool.Input { Path = "shared.gitconfig", Content = content }, default);
        Assert.Equal(content, await File.ReadAllTextAsync(Path.Combine(_repo, "shared.gitconfig")));
    }

    [Fact]
    public async Task A_diff_textconv_in_an_included_work_tree_file_never_runs_from_git_diff()
    {
        var marker = Path.Combine(_root.FullName, "textconv-ran.txt");
        await ModelWritesSharedConfig($"[diff \"conv\"]\n\ttextconv = echo ran > '{Sh(marker)}'; cat\n");
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "changed\n");

        WorkspaceTurn();
        var diff = (GitDiffTool.Output)(await new GitDiffTool(Sandbox()).Descriptor.Invoke(new GitDiffTool.Input(), default))!;
        Assert.True(diff.Succeeded, diff.Error);
        Assert.Contains("changed", diff.Diff);
        var log = (GitLogTool.Output)(await new GitLogTool(Sandbox()).Descriptor.Invoke(new GitLogTool.Input(), default))!;
        Assert.True(log.Succeeded, log.Error);
        Assert.False(File.Exists(marker), "a textconv from an included work-tree config file ran from a harness git tool");

        // Control: plain git diff runs it.
        Git(_repo, "diff");
        Assert.True(File.Exists(marker), "control: plain git did not run the textconv, so this machine cannot prove the fence");
    }

    [Fact]
    public async Task A_clean_filter_in_an_included_work_tree_file_never_runs_from_git_status_or_git_commit()
    {
        var marker = Path.Combine(_root.FullName, "clean-ran.txt");
        await ModelWritesSharedConfig($"[filter \"evil\"]\n\tclean = echo ran > '{Sh(marker)}'; cat\n\trequired = true\n");
        await File.WriteAllTextAsync(Path.Combine(_repo, "b.txt"), "changed b\n");

        WorkspaceTurn();
        var sandbox = Sandbox();
        var status = (GitStatusTool.Output)(await new GitStatusTool(sandbox).Descriptor.Invoke(new GitStatusTool.Input(), default))!;
        Assert.True(status.Succeeded, status.Error);
        var commit = (GitCommitTool.Output)(await new GitCommitTool(sandbox).Descriptor.Invoke(
            new GitCommitTool.Input { Message = "harness commit", Paths = ["b.txt"] }, default))!;
        Assert.True(commit.Succeeded, commit.Error);
        Assert.False(File.Exists(marker), "a clean filter from an included work-tree config file ran from a harness git tool");

        // Control: plain git add runs it.
        await File.WriteAllTextAsync(Path.Combine(_repo, "b.txt"), "changed again\n");
        Git(_repo, "add", "b.txt");
        Assert.True(File.Exists(marker), "control: plain git did not run the clean filter, so this machine cannot prove the fence");
    }

    [Fact]
    public async Task A_gpg_program_in_an_included_work_tree_file_never_runs_from_git_commit()
    {
        var marker = Path.Combine(_root.FullName, "gpg-ran.txt");
        var program = Path.Combine(_root.FullName, "fake-gpg.sh");
        await File.WriteAllTextAsync(program, $"#!/bin/sh\necho ran > '{Sh(marker)}'\nexit 1\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await ModelWritesSharedConfig($"[commit]\n\tgpgSign = true\n[tag]\n\tgpgSign = true\n[log]\n\tshowSignature = true\n[gpg]\n\tprogram = {Sh(program)}\n");
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "signed?\n");

        WorkspaceTurn();
        var sandbox = Sandbox();
        var commit = (GitCommitTool.Output)(await new GitCommitTool(sandbox).Descriptor.Invoke(
            new GitCommitTool.Input { Message = "harness commit", Paths = ["a.txt"] }, default))!;
        Assert.True(commit.Succeeded, commit.Error);
        var log = (GitLogTool.Output)(await new GitLogTool(sandbox).Descriptor.Invoke(new GitLogTool.Input(), default))!;
        Assert.True(log.Succeeded, log.Error);
        Assert.False(File.Exists(marker), "gpg.program from an included work-tree config file ran from a harness git tool");

        // Control: plain git commit runs it (and fails because the fake signer exits 1).
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "plain\n");
        Git(_repo, "commit", "-q", "-am", "plain commit");
        Assert.True(File.Exists(marker), "control: plain git did not run gpg.program, so this machine cannot prove the fence");
    }

    [Fact]
    public async Task The_driver_query_blanks_repository_drivers_and_keeps_the_users_own()
    {
        await ModelWritesSharedConfig("[filter \"evil\"]\n\tclean = x\n\trequired = true\n[diff \"conv\"]\n\ttextconv = y\n[filter \"a.b\"]\n\tsmudge = z\n");
        var query = await GitProcess.RunAsync(_repo, [.. GitInvocation.RepositoryDriverQuery], TimeSpan.FromSeconds(30), default);
        Assert.True(query.ExitCode == 0, query.Stderr);
        var overrides = GitInvocation.RepositoryDriverOverrides(query.Stdout, _repo);
        Assert.Contains("filter.evil.clean=", overrides);
        Assert.Contains("filter.evil.required=false", overrides);
        Assert.Contains("diff.conv.textconv=", overrides);
        Assert.Contains("filter.a.b.smudge=", overrides);

        // A global/system driver outside the work tree (git-lfs, Git for Windows' astextplain) is the user's own and kept;
        // a global one whose file lies inside the work tree is blanked; a name that cannot be overridden refuses the call.
        var outside = Path.Combine(_root.FullName, "home", ".gitconfig");
        var inside = Path.Combine(_repo, "dotfiles.gitconfig");
        var listing = string.Join('\0',
            "system", "file:" + Sh(outside), "filter.lfs.clean",
            "global", "file:" + Sh(inside), "diff.mine.textconv",
            "local", "file:.git/config", "filter.evil.process") + "\0";
        Assert.Equal(["diff.mine.textconv=", "filter.evil.process="], GitInvocation.RepositoryDriverOverrides(listing, _repo));
        Assert.Throws<GitConfigurationRefusedException>(() => GitInvocation.RepositoryDriverOverrides(
            "local\0file:.git/config\0filter.x=y.clean\0", _repo));
    }

    private static void Git(string dir, params string[] args) => SwarmCore.Testing.TestGit.Run(dir, args);
}
