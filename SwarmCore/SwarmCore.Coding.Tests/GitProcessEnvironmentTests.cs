using System.Diagnostics;
using SwarmCore.Coding.Tools;
using Xunit;

namespace SwarmCore.Coding.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GitProcessEnvironmentCollection
{
    public const string Name = "Git process environment";
}

/// <summary>
/// Harness git calls never inherit git redirection from the host process (<c>GIT_DIR</c>, <c>GIT_WORK_TREE</c>,
/// <c>GIT_INDEX_FILE</c>, injected <c>GIT_CONFIG_*</c>). These tests change the process environment, so they run alone.
/// </summary>
[Collection(GitProcessEnvironmentCollection.Name)]
public sealed class GitProcessEnvironmentTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("git-env-");

    public void Dispose()
    {
        foreach (var file in _root.EnumerateFiles("*", SearchOption.AllDirectories)) file.Attributes = FileAttributes.Normal;
        _root.Delete(recursive: true);
    }

    [Fact]
    public async Task Inherited_git_redirection_never_reaches_a_harness_git_call()
    {
        var a = Directory.CreateDirectory(Path.Combine(_root.FullName, "a")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(_root.FullName, "b")).FullName;
        foreach (var repo in new[] { a, b })
        {
            Git(repo, "init", "-q");
            File.WriteAllText(Path.Combine(repo, "f.txt"), repo);
            Git(repo, "add", ".");
            Git(repo, "-c", "user.name=T", "-c", "user.email=t@example.com", "-c", "commit.gpgsign=false", "commit", "-q", "-m", Path.GetFileName(repo));
        }
        var marker = Path.Combine(_root.FullName, "fsmonitor-ran.txt").Replace('\\', '/');

        var names = new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_CONFIG_COUNT", "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0" };
        Environment.SetEnvironmentVariable("GIT_DIR", Path.Combine(b, ".git"));
        Environment.SetEnvironmentVariable("GIT_WORK_TREE", b);
        Environment.SetEnvironmentVariable("GIT_INDEX_FILE", Path.Combine(b, ".git", "index"));
        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "core.fsmonitor");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", $"echo ran > '{marker}'; true");
        (int ExitCode, string Stdout, string Stderr) log, status;
        try
        {
            log = await GitProcess.RunAsync(a, ["log", "-1", "--format=%s"], TimeSpan.FromSeconds(30), default);
            status = await GitProcess.RunAsync(a, ["status", "--porcelain"], TimeSpan.FromSeconds(30), default);
        }
        finally
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, null);
        }
        Assert.True(log.ExitCode == 0, log.Stderr);
        Assert.Equal("a", log.Stdout.Trim());
        Assert.True(status.ExitCode == 0, status.Stderr);
        Assert.False(File.Exists(marker), "an inherited GIT_CONFIG_* command ran from a harness git call");
    }

    private static void Git(string dir, params string[] args) => SwarmCore.Testing.TestGit.Run(dir, args);
}
