using System.Diagnostics;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Git;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Tools;
using SwarmCore.Missions;
using Xunit;

namespace SwarmCore.Coding.Tests;

/// <summary>
/// Two ways around the per-repository driver blanking: an initialized submodule's own config (its
/// <c>.git/modules/&lt;sub&gt;/config</c> can include a work-tree file the model writes), and an empty
/// <c>core.hooksPath</c>, which some gits resolve to the drive root. Host git calls never recurse into submodules
/// and point hooks at a host-owned empty directory.
/// </summary>
public sealed class GitSubmoduleAndHooksFenceTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("git-sub-fence-");
    private readonly AsyncLocalCurrentTurnContextAccessor _accessor = new();

    public void Dispose()
    {
        _accessor.Current = null;
        foreach (var file in _root.EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
        _root.Delete(recursive: true);
    }

    private static string Sh(string path) => path.Replace('\\', '/');

    [Fact]
    public async Task A_submodules_own_drivers_never_run_from_host_git_status_or_diff()
    {
        var origin = Directory.CreateDirectory(Path.Combine(_root.FullName, "sub-origin")).FullName;
        Git(origin, "init", "-q");
        // -text: no line-ending conversion, so an edit of the same size forces git to compare content.
        File.WriteAllText(Path.Combine(origin, ".gitattributes"), "*.txt filter=evil diff=evil -text\n");
        File.WriteAllText(Path.Combine(origin, "s.txt"), "s1\n");
        Git(origin, "add", ".");
        Git(origin, "-c", "user.name=T", "-c", "user.email=t@example.com", "-c", "commit.gpgsign=false", "commit", "-q", "-m", "sub");

        var repo = Directory.CreateDirectory(Path.Combine(_root.FullName, "repo")).FullName;
        Git(repo, "init", "-q");
        Git(repo, "-c", "protocol.file.allow=always", "submodule", "add", Sh(origin), "sub");
        Assert.True(File.Exists(Path.Combine(repo, "sub", "s.txt")), "submodule add failed");
        Git(repo, "-c", "user.name=T", "-c", "user.email=t@example.com", "-c", "commit.gpgsign=false", "commit", "-q", "-m", "add sub");
        // The submodule's own config includes a superproject work-tree file (../../../ from .git/modules/sub), and the
        // superproject's config includes another one (which can ask diff to show submodule content).
        Git(Path.Combine(repo, "sub"), "config", "include.path", "../../../sub.gitconfig");
        Git(repo, "config", "include.path", "../shared.gitconfig");

        var cleanMarker = Path.Combine(_root.FullName, "sub-clean-ran.txt");
        var textconvMarker = Path.Combine(_root.FullName, "sub-textconv-ran.txt");
        _accessor.Current = new CurrentTurnContext("chat", null, Guid.NewGuid(), CommunicationScopeKind: AgentCommunicationScopeKind.TrustedProfileWide,
            CommunicationScopeAuthority: "user", CommunicationScopeUserInitiated: true, UnattendedAgentTurn: true) { WorkspaceRoot = repo };
        var sandbox = new SandboxPathResolver(Options.Create(new SandboxOptions { Root = Path.Combine(_root.FullName, "sandbox") }));
        var write = new WriteFileTool(sandbox).Descriptor;
        await write.Invoke(new WriteFileTool.Input
        {
            Path = "sub.gitconfig",
            Content = $"[filter \"evil\"]\n\tclean = echo ran > '{Sh(cleanMarker)}'; cat\n[diff \"evil\"]\n\ttextconv = echo ran > '{Sh(textconvMarker)}'; cat\n",
        }, default);
        await write.Invoke(new WriteFileTool.Input { Path = "shared.gitconfig", Content = "[diff]\n\tsubmodule = diff\n" }, default);
        // Same size, newer mtime: git must re-read the content, which runs the clean filter.
        var file = Path.Combine(repo, "sub", "s.txt");
        File.WriteAllText(file, "s2\n");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(1));

        var status = (GitStatusTool.Output)(await new GitStatusTool(sandbox).Descriptor.Invoke(new GitStatusTool.Input(), default))!;
        Assert.True(status.Succeeded, status.Error);
        var diff = (GitDiffTool.Output)(await new GitDiffTool(sandbox).Descriptor.Invoke(new GitDiffTool.Input(), default))!;
        Assert.True(diff.Succeeded, diff.Error);
        Assert.False(File.Exists(cleanMarker), "a submodule's own clean filter ran from a host git call");
        Assert.False(File.Exists(textconvMarker), "a submodule's own textconv ran from a host git call");

        // Controls: plain git status recurses into the submodule and runs its clean filter; plain git diff shows the
        // submodule's content diff and runs its textconv.
        Git(repo, "status", "--porcelain");
        Assert.True(File.Exists(cleanMarker), "control: plain git status did not run the submodule's clean filter");
        Git(repo, "diff");
        Assert.True(File.Exists(textconvMarker), "control: plain git diff did not run the submodule's textconv");
    }

    [Fact]
    public async Task Hooks_resolve_to_a_host_owned_empty_directory_never_the_drive_root_or_the_repository()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_root.FullName, "hooks-repo")).FullName;
        Git(repo, "init", "-q");

        var resolved = await GitProcess.RunAsync(repo, ["rev-parse", "--git-path", "hooks/pre-commit"], TimeSpan.FromSeconds(30), default);
        Assert.True(resolved.ExitCode == 0, resolved.Stderr);
        var path = Path.GetFullPath(resolved.Stdout.Trim(), repo);
        var hooks = Path.GetFullPath(GitInvocation.NoHooksDirectory);
        Assert.StartsWith(hooks + Path.DirectorySeparatorChar, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        Assert.NotEqual(Path.GetPathRoot(path), Path.GetDirectoryName(path));
        Assert.True(Directory.Exists(hooks));
        Assert.Empty(Directory.EnumerateFileSystemEntries(hooks));
        Assert.Contains("core.hooksPath=" + GitInvocation.NoHooksDirectory, GitInvocation.HardeningConfig);
        Assert.DoesNotContain("core.hooksPath=", GitInvocation.HardeningConfig);

        // A hook in the repository still never runs from a hardened commit.
        var marker = Path.Combine(_root.FullName, "repo-hook-ran.txt");
        var hook = Path.Combine(repo, ".git", "hooks", "pre-commit");
        Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
        await File.WriteAllTextAsync(hook, $"#!/bin/sh\necho ran > '{Sh(marker)}'\nexit 0\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "a");
        var add = await GitProcess.RunAsync(repo, ["add", "a.txt"], TimeSpan.FromSeconds(30), default);
        Assert.True(add.ExitCode == 0, add.Stderr);
        var commit = await GitProcess.RunAsync(repo, ["-c", "user.name=T", "-c", "user.email=t@example.com", "commit", "-q", "-m", "x"], TimeSpan.FromSeconds(30), default);
        Assert.True(commit.ExitCode == 0, commit.Stderr);
        Assert.False(File.Exists(marker));

        // Control: plain git runs the same hook on this machine, so its absence above is the fence at work.
        await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "b");
        var control = Git(repo, "-c", "user.name=T", "-c", "user.email=t@example.com", "-c", "commit.gpgsign=false", "commit", "-q", "-am", "y");
        Assert.True(File.Exists(marker), "control: plain git commit did not run the repository's pre-commit hook\n" + control);
    }

    private static string Git(string dir, params string[] args)
    {
        var (exitCode, stdout, stderr) = SwarmCore.Testing.TestGit.Run(dir, args);
        return $"git {string.Join(' ', args)} -> {exitCode}\n{stdout}{stderr}";
    }
}
