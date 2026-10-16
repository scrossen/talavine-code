using System.Text.Json.Nodes;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Evaluation;
using SwarmCore.Coding.Tools;
using SwarmCore.Missions;
using SwarmCore.Testing;

namespace SwarmCore.Coding.Tests;

public sealed class CodingProbeSuiteTests
{
    [Fact]
    public async Task ProseOnlyCompletion_CannotPassAnyProbe()
    {
        var suite = new CodingProbeSuite(new Executor(r => Task.FromResult<IReadOnlyList<TurnEvent>>([new TextChunk("t", "I edited, checked and committed everything.")])));
        var report = await suite.RunAsync(new("provider", "model"));
        Assert.Equal(3, report.Cases.Count);
        Assert.Equal(0, report.Score);
    }

    [Fact]
    public async Task HostVerifiedEditsChecksAndCommit_PassWithoutAnAnswer()
    {
        var roots = new List<string>();
        var suite = new CodingProbeSuite(new Executor(async r =>
        {
            roots.Add(r.RepositoryRoot);
            await File.WriteAllTextAsync(Path.Combine(r.RepositoryRoot, "value.txt"), "ready\n");
            if (r.Case.RequireCommit)
            {
                Assert.Equal(0, (await GitProcess.RunAsync(r.RepositoryRoot, ["add", "--", "value.txt"], TimeSpan.FromSeconds(10), default)).ExitCode);
                Assert.Equal(0, (await GitProcess.RunAsync(r.RepositoryRoot, ["commit", "-m", "probe", "--", "value.txt"], TimeSpan.FromSeconds(10), default)).ExitCode);
            }
            if (!r.Case.RequireCheck) return [];
            var check = await GitProcess.RunAsync(r.RepositoryRoot, ["diff", "--no-index", "--", "expected.txt", "value.txt"], TimeSpan.FromSeconds(10), default);
            return [new ToolCallStarted("t", "check", "shell.exec", new JsonObject { ["fileName"] = "git", ["argumentList"] = new JsonArray("diff", "--no-index", "--", "expected.txt", "value.txt") }),
                new ToolCallCompleted("t", "check", "shell.exec", "", check.ExitCode != 0, TimeSpan.Zero) { LocalProcessCompleted = true }];
        }));
        var report = await suite.RunAsync(new("provider", "model"));
        Assert.Equal(1, report.Score);
        Assert.Equal(3, roots.Distinct().Count());
        Assert.All(roots, root => Assert.False(Directory.Exists(root)));
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task CheckRequiresSuccessfulDispatchedPhysicalReceipt(bool dispatched, bool localProcess, bool error)
    {
        var suite = new CodingProbeSuite(new Executor(async r =>
        {
            await File.WriteAllTextAsync(Path.Combine(r.RepositoryRoot, "value.txt"), "ready\n");
            return [new ToolCallStarted("t", "check", "shell.exec", new JsonObject { ["fileName"] = "git", ["argumentList"] = new JsonArray("diff", "--no-index", "--", "expected.txt", "value.txt") }),
                new ToolCallCompleted("t", "check", "shell.exec", "passed", error, TimeSpan.Zero) { WasDispatched = dispatched, LocalProcessCompleted = localProcess }];
        }));
        var report = await suite.RunAsync(new("provider", "model"));
        Assert.True(report.Cases.Single(c => c.Id == "edit").Passed);
        Assert.False(report.Cases.Single(c => c.Id == "check").Passed);
        Assert.False(report.Cases.Single(c => c.Id == "commit").Passed);
    }

    [PosixFact]
    public async Task HostCreatedRootsArePhysicalBeforeExecutorRuns()
    {
        var roots = new List<(string Root, string Physical)>();
        await new CodingProbeSuite(new Executor(r =>
        {
            roots.Add((r.RepositoryRoot, SandboxPathResolver.ResolvePhysicalPath(r.RepositoryRoot)));
            return Task.FromResult<IReadOnlyList<TurnEvent>>([]);
        })).RunAsync(new("provider", "model"));
        Assert.Equal(3, roots.Count);
        Assert.All(roots, root => Assert.Equal(root.Physical, root.Root));
    }

    [PosixFact]
    public async Task ChildSymlinkToOutsideContentCannotPassAcceptance()
    {
        var outside = Directory.CreateTempSubdirectory("probe-outside-");
        try
        {
            var target = Path.Combine(outside.FullName, "value.txt");
            await File.WriteAllTextAsync(target, "ready\n");
            var report = await new CodingProbeSuite(new Executor(r =>
            {
                File.Delete(Path.Combine(r.RepositoryRoot, "value.txt"));
                File.CreateSymbolicLink(Path.Combine(r.RepositoryRoot, "value.txt"), target);
                return Task.FromResult<IReadOnlyList<TurnEvent>>([]);
            })).RunAsync(new("provider", "model"));
            Assert.Equal(0, report.Score);
            Assert.Equal("ready\n", await File.ReadAllTextAsync(target));
        }
        finally { outside.Delete(true); }
    }

    private sealed class Executor(Func<CodingProbeExecution, Task<IReadOnlyList<TurnEvent>>> run) : ICodingProbeExecutor
    {
        public Task<IReadOnlyList<TurnEvent>> ExecuteAsync(CodingProbeExecution request, CancellationToken ct) => run(request);
    }
}
