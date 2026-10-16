using SwarmCore.Agent.Git;
using SwarmCore.Coding.Tools;

namespace SwarmCore.Coding.Tests;

/// <summary>
/// Every harness git call starts the shared resolved git with the shared hardening (resolver rules are covered in
/// <c>SwarmCore.Agent.Tests/GitExecutableResolverTests</c>).
/// </summary>
public sealed class GitExecutableTests
{
    [Fact]
    public void The_real_resolver_finds_this_machines_git_and_builds_hardened_arguments()
    {
        var start = GitProcess.CreateStartInfo(Path.GetTempPath(), ["status"]);
        Assert.Equal(GitExecutable.Resolve(), start.FileName);
        var args = start.ArgumentList.ToList();
        Assert.Equal("--no-pager", args[0]);
        foreach (var setting in new[]
                 {
                     "core.hooksPath=" + GitInvocation.NoHooksDirectory, "core.fsmonitor=false", "commit.gpgSign=false", "tag.gpgSign=false", "log.showSignature=false",
                     "core.attributesFile=", "core.excludesFile=", "protocol.allow=never", "credential.helper=",
                 })
            Assert.Equal("-c", args[args.IndexOf(setting) - 1]);
        Assert.Equal(["status", "--ignore-submodules=all"], args[^2..]);
        Assert.Equal("0", start.Environment["GIT_TERMINAL_PROMPT"]);
        Assert.DoesNotContain(start.Environment.Keys, k => k.StartsWith("GIT_CONFIG", StringComparison.OrdinalIgnoreCase)
                                                           || k is "GIT_DIR" or "GIT_WORK_TREE" or "GIT_INDEX_FILE");
    }
}
