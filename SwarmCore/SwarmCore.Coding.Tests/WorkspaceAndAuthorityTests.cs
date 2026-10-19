using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Prompting;
using SwarmCore.Coding.Routing;
using SwarmCore.Coding.Tools;
using SwarmCore.Missions;
using Xunit;

namespace SwarmCore.Coding.Tests;

/// <summary>
/// One process, many conversations: each turn works in, and is fenced to, the workspace the
/// host chose for it; authority always comes from the host.
/// </summary>
public sealed class WorkspaceAndAuthorityTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "coding-workspaces-" + Guid.NewGuid().ToString("N"));
    private readonly string _repoA;
    private readonly string _repoB;
    private readonly string _configured;

    public WorkspaceAndAuthorityTests()
    {
        _repoA = Directory.CreateDirectory(Path.Combine(_base, "repo-a")).FullName;
        _repoB = Directory.CreateDirectory(Path.Combine(_base, "repo-b")).FullName;
        _configured = Directory.CreateDirectory(Path.Combine(_base, "profile-sandbox")).FullName;
    }

    public void Dispose()
    {
        new AsyncLocalCurrentTurnContextAccessor().Current = null;
        try
        {
            foreach (var f in Directory.EnumerateFiles(_base, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_base, true);
        }
        catch { /* best effort */ }
    }

    private static void EnterTurn(string? workspace, AgentCommunicationScope? scope = null)
    {
        var s = scope ?? AgentCommunicationScope.None;
        new AsyncLocalCurrentTurnContextAccessor().Current = new CurrentTurnContext("conversation", Guid.NewGuid(), Guid.NewGuid(),
            CommunicationScopeKind: s.Kind, CommunicationScopeAuthority: s.Authority, CommunicationScopeUserInitiated: s.UserInitiated)
        { WorkspaceRoot = workspace };
    }

    private SandboxPathResolver Sandbox() => new(Options.Create(new SandboxOptions { Root = _configured }));

    [Fact]
    public void Sandbox_root_follows_the_turn_workspace_and_fences_relative_paths()
    {
        var sandbox = Sandbox();
        try
        {
            EnterTurn(_repoA);
            Assert.Equal(SandboxPathResolver.ResolvePhysicalPath(_repoA), sandbox.Root);
            Assert.Equal(Path.Combine(sandbox.Root, "src", "a.cs"), sandbox.Resolve("src/a.cs"));
            Assert.Throws<UnauthorizedAccessException>(() => sandbox.Resolve("../repo-b/secret.txt"));
            Assert.Throws<UnauthorizedAccessException>(() => sandbox.Resolve(Path.Combine(_repoB, "x.txt")));

            EnterTurn(_repoB);
            Assert.Equal(SandboxPathResolver.ResolvePhysicalPath(_repoB), sandbox.Root);
            Assert.Throws<UnauthorizedAccessException>(() => sandbox.Resolve("../repo-a/x.txt"));

            EnterTurn(null);
            Assert.Equal(SandboxPathResolver.ResolvePhysicalPath(_configured), sandbox.Root);
        }
        finally { new AsyncLocalCurrentTurnContextAccessor().Current = null; }
        Assert.Equal(sandbox.ConfiguredRoot, sandbox.Root);
    }

    [Fact]
    public async Task Coding_tools_edit_and_commit_in_the_turns_own_repository()
    {
        foreach (var repo in new[] { _repoA, _repoB })
        {
            await Git(repo, "init");
            await Git(repo, "config", "user.name", "Fixture");
            await Git(repo, "config", "user.email", "fixture@example.invalid");
            await Git(repo, "config", "commit.gpgsign", "false");
            await File.WriteAllTextAsync(Path.Combine(repo, "f.txt"), "base " + Path.GetFileName(repo));
            await Git(repo, "add", "--", "f.txt");
            await Git(repo, "commit", "-m", "baseline");
        }
        var sandbox = Sandbox();
        try
        {
            EnterTurn(_repoA);
            var edit = (EditFileTool.Output)(await new EditFileTool(sandbox).Descriptor.Invoke(
                new EditFileTool.Input { Path = "f.txt", OldText = "base", NewText = "changed" }, default))!;
            Assert.True(edit.Succeeded, edit.Error);
            var commit = (GitCommitTool.Output)(await new GitCommitTool(sandbox).Descriptor.Invoke(
                new GitCommitTool.Input { Message = "change a", Paths = ["f.txt"] }, default))!;
            Assert.True(commit.Succeeded, commit.Error);
            var glob = (GlobTool.Output)(await new GlobTool(sandbox).Descriptor.Invoke(new GlobTool.Input { Pattern = "*.txt" }, default))!;
            Assert.Equal(["f.txt"], glob.Files);

            EnterTurn(_repoB);
            var status = (GitStatusTool.Output)(await new GitStatusTool(sandbox).Descriptor.Invoke(new GitStatusTool.Input(), default))!;
            Assert.True(status.IsClean, status.Error);
        }
        finally { new AsyncLocalCurrentTurnContextAccessor().Current = null; }

        Assert.Equal("changed repo-a", await File.ReadAllTextAsync(Path.Combine(_repoA, "f.txt")));
        Assert.Equal("base repo-b", await File.ReadAllTextAsync(Path.Combine(_repoB, "f.txt")));
        Assert.Equal("change a", (await GitProcess.RunAsync(_repoA, ["log", "-1", "--format=%s"], TimeSpan.FromSeconds(30), default)).Stdout.Trim());
        Assert.Equal("baseline", (await GitProcess.RunAsync(_repoB, ["log", "-1", "--format=%s"], TimeSpan.FromSeconds(30), default)).Stdout.Trim());
    }

    private static async Task Git(string repo, params string[] args)
    {
        var result = await GitProcess.RunAsync(repo, args, TimeSpan.FromSeconds(30), default);
        Assert.True(result.ExitCode == 0, result.Stderr);
    }

    // ---- orchestrator ----

    private sealed class Runner(List<AgentTurnRequest> seen) : IAgentTurnRunner
    {
        public async IAsyncEnumerable<TurnEvent> RunAsync(AgentTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            seen.Add(request);
            await Task.Yield();
            yield return new TurnCompleted("t", "done", TimeSpan.Zero);
        }
    }

    private sealed class NativeRunner(List<NativeLaneTurn> seen) : INativeLaneRunner
    {
        public bool Supports(CodingLane lane) => lane.IsNative;

        public async IAsyncEnumerable<TurnEvent> RunAsync(NativeLaneTurn turn, [EnumeratorCancellation] CancellationToken ct = default)
        {
            seen.Add(turn);
            await Task.Yield();
            yield return new TurnCompleted("n", "native done", TimeSpan.Zero);
        }
    }

    private sealed class CapturingVerifier(List<CodingAcceptanceContext> seen) : ICodingAcceptanceVerifier
    {
        public Task<CodingAcceptanceResult> VerifyAsync(CodingAcceptanceContext context, CancellationToken ct = default)
        {
            seen.Add(context);
            return Task.FromResult(new CodingAcceptanceResult(CodingAcceptanceStatus.Passed, "ok", []));
        }
    }

    private sealed class PerConversationLanes : ICodingLaneSource
    {
        public List<string> Asked { get; } = [];
        public CodingLaneSet GetLanes() => new() { Lanes = [new("listed", "P", "m")] };

        public ValueTask<CodingLaneSet> GetLanesAsync(string conversationId, CancellationToken ct = default)
        {
            Asked.Add(conversationId);
            return ValueTask.FromResult(new CodingLaneSet { Lanes = [new("routed-" + conversationId, "Router", "recommended")], HandOffOnRefusal = false });
        }
    }

    private sealed record Rig(CodingTurnOrchestrator Orchestrator, List<AgentTurnRequest> Api, List<NativeLaneTurn> Native, List<CodingAcceptanceContext> Verified);

    private Rig Build(CodingWorkspace? resolved, ICodingLaneSource? lanes = null)
    {
        var api = new List<AgentTurnRequest>();
        var native = new List<NativeLaneTurn>();
        var verified = new List<CodingAcceptanceContext>();
        var services = new ServiceCollection();
        services.AddScoped<IAgentTurnRunner>(_ => new Runner(api));
        services.AddScoped<INativeLaneRunner>(_ => new NativeRunner(native));
        services.AddSingleton<ICodingAcceptanceVerifier>(new CapturingVerifier(verified));
        services.AddSingleton(lanes ?? new OptionsCodingLaneSource(Options.Create(new CodingLaneOptions
        {
            Lanes =
            [
                new("claude", "ClaudeCodeNative", "claude-code:default") { Priority = 0 },
                new("api", "OpenRouter", "some/model") { Priority = 1 },
                new("codex", "CodexNative", "codex-cli:default") { Priority = 2 },
            ],
        })));
        services.AddSingleton<ICodingWorkspaceResolver>(new TestOrchestrator.FixedWorkspace(resolved));
        var provider = services.BuildServiceProvider();
        return new(new CodingTurnOrchestrator(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new CodingOptions()),
            NullLogger<CodingTurnOrchestrator>.Instance), api, native, verified);
    }

    private static async Task<List<TurnEvent>> Run(ICodingTurnOrchestrator o, CodingTurnRequest request)
    {
        var events = new List<TurnEvent>();
        await foreach (var e in o.RunAsync(request)) events.Add(e);
        return events;
    }

    [Fact]
    public async Task Request_workspace_overrides_the_resolver_and_reaches_the_turn()
    {
        var rig = Build(new CodingWorkspace { RepositoryRoot = _repoA });
        var profile = Guid.NewGuid();
        await Run(rig.Orchestrator, new CodingTurnRequest
        {
            ConversationId = "c", UserMessage = "x", LaneName = "api", ProfileId = profile,
            Workspace = new CodingWorkspace { RepositoryRoot = _repoB, AcceptanceChecks = [new() { Name = "build", FileName = "dotnet" }] },
        });

        var turn = Assert.Single(rig.Api);
        Assert.Equal(_repoB, turn.WorkspaceRoot);
        Assert.Equal(profile, turn.ProfileIdOverride);
        var verified = Assert.Single(rig.Verified);
        Assert.Equal(_repoB, verified.RepositoryRoot);
        Assert.Equal("build", Assert.Single(verified.Commands!).Name);
    }

    [Fact]
    public async Task Resolver_workspace_and_its_lane_preference_are_used_when_the_request_has_none()
    {
        var rig = Build(new CodingWorkspace { RepositoryRoot = _repoA, LaneName = "api" });
        await Run(rig.Orchestrator, new CodingTurnRequest { ConversationId = "c", UserMessage = "x" });
        var turn = Assert.Single(rig.Api);
        Assert.Equal(_repoA, turn.WorkspaceRoot);
        Assert.Equal("OpenRouter", turn.ProviderOverride);
        Assert.Null(Assert.Single(rig.Verified).Commands);
    }

    [Fact]
    public async Task No_workspace_means_no_turn()
    {
        var rig = Build(null);
        var events = await Run(rig.Orchestrator, new CodingTurnRequest { ConversationId = "c", UserMessage = "x" });
        Assert.Contains("No repository is selected", Assert.Single(events.OfType<TurnErrored>()).Message);
        Assert.Empty(rig.Api);
        Assert.Empty(rig.Native);

        var missing = Build(new CodingWorkspace { RepositoryRoot = Path.Combine(_base, "missing") });
        Assert.Single((await Run(missing.Orchestrator, new CodingTurnRequest { ConversationId = "c", UserMessage = "x" })).OfType<TurnErrored>());
        Assert.Empty(missing.Api);
    }

    [Fact]
    public async Task Harness_grants_no_authority_of_its_own()
    {
        var rig = Build(new CodingWorkspace { RepositoryRoot = _repoA });
        var events = await Run(rig.Orchestrator, new CodingTurnRequest { ConversationId = "c", UserMessage = "x" });

        // No host scope: the turn runs with none, native lanes (which bypass the broker) are skipped.
        Assert.Contains(events.OfType<CodingLaneSkipped>(), s => s.Lane.Name == "claude" && s.Reason == CodingTurnOrchestrator.NativeLaneNeedsUserAuthority);
        Assert.Empty(rig.Native);
        var turn = Assert.Single(rig.Api);
        Assert.Equal(AgentCommunicationScopeKind.None, turn.CommunicationScope.Kind);
        Assert.False(turn.CommunicationScope.UserInitiated);
        Assert.Null(turn.ValidateWorkingContextSourceCallsAsync);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Improvement_repairs_use_the_isolated_brokered_workspace_even_with_persisted_user_approval(bool persistedApproval)
    {
        var rig = Build(new CodingWorkspace { RepositoryRoot = _repoA });
        var request = SwarmCore.Coding.Improvement.ImprovementCodingRequest.Create("repair", "Fix the reported defect.", _repoB, "codex", 60);
        if (persistedApproval)
            request = request with { CommunicationScope = AgentCommunicationScope.UserRequested("persisted-repair-approval") };
        var events = await Run(rig.Orchestrator, request);
        Assert.Empty(rig.Native);
        Assert.Contains(events.OfType<CodingLaneSkipped>(), e => e.Lane.Name == "codex");
        Assert.Equal("api", Assert.Single(events.OfType<CodingLaneSelected>()).Lane.Name);
        var turn = Assert.Single(rig.Api);
        Assert.Equal(_repoB, turn.WorkspaceRoot);
        if (!persistedApproval) Assert.Equal(AgentCommunicationScopeKind.None, turn.CommunicationScope.Kind);
        Assert.Empty(Assert.Single(rig.Verified).Commands!);
    }

    [Fact]
    public async Task Named_native_lane_without_user_authority_falls_back_to_a_non_native_lane_with_a_notice()
    {
        var rig = Build(new CodingWorkspace { RepositoryRoot = _repoA });
        var events = await Run(rig.Orchestrator, new CodingTurnRequest
        {
            ConversationId = "c", UserMessage = "x", LaneName = "codex", ModelOverride = "codex-only-model",
        });

        var skipped = events.OfType<CodingLaneSkipped>().ToList();
        Assert.Contains(skipped, s => s.Lane.Name == "codex" && s.Reason.Contains("native lanes bypass approval prompts", StringComparison.Ordinal));
        Assert.Contains(skipped, s => s.Lane.Name == "claude");
        Assert.Equal(1, skipped.Count(s => s.Lane.Name == "codex"));
        Assert.Equal("api", Assert.Single(events.OfType<CodingLaneSelected>()).Lane.Name);
        Assert.Empty(events.OfType<CodingLanesExhausted>());
        Assert.Empty(rig.Native);
        var turn = Assert.Single(rig.Api);
        Assert.Equal("some/model", turn.ModelOverride); // the skipped lane's model choice does not carry over
    }

    [Fact]
    public async Task Native_only_lanes_without_user_authority_end_with_an_explanation()
    {
        var rig = Build(new CodingWorkspace { RepositoryRoot = _repoA }, new OptionsCodingLaneSource(Options.Create(new CodingLaneOptions
        {
            Lanes = [new("claude", "ClaudeCodeNative", "claude-code:default"), new("codex", "CodexNative", "codex-cli:default")],
        })));
        var events = await Run(rig.Orchestrator, new CodingTurnRequest { ConversationId = "c", UserMessage = "x", LaneName = "claude" });
        Assert.Contains("native lanes bypass approval prompts", Assert.Single(events.OfType<CodingLanesExhausted>()).Reason);
        Assert.Empty(rig.Native);
        Assert.Empty(rig.Api);
    }

    [Fact]
    public async Task Real_command_acceptance_runs_in_the_workspace_when_it_is_not_the_configured_sandbox_root()
    {
        Assert.NotEqual(_configured, _repoB);
        var api = new List<AgentTurnRequest>();
        var services = new ServiceCollection();
        services.AddScoped<IAgentTurnRunner>(_ => new Runner(api));
        services.AddSingleton<ISandboxPathResolver>(new SandboxPathResolver(Options.Create(new SandboxOptions { Root = _configured })));
        services.AddSingleton(sp => new ShellExecTool(sp.GetRequiredService<ISandboxPathResolver>(), sp));
        var coding = new CodingOptions { Acceptance = new() { MaxRepairAttempts = 0 } };
        services.AddSingleton<IOptions<CodingOptions>>(Options.Create(coding));
        services.AddScoped<ICodingAcceptanceVerifier, CodingCommandAcceptanceVerifier>();
        services.AddSingleton<ICodingLaneSource>(new OptionsCodingLaneSource(Options.Create(new CodingLaneOptions { Lanes = [new("api", "P", "m")] })));
        using var provider = services.BuildServiceProvider();
        var orchestrator = new CodingTurnOrchestrator(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(coding),
            NullLogger<CodingTurnOrchestrator>.Instance);

        var check = OperatingSystem.IsWindows()
            ? new CodingAcceptanceCommand { Name = "echo", FileName = "cmd", Arguments = ["/c", "echo ok"], RequiredOutput = "ok" }
            : new CodingAcceptanceCommand { Name = "echo", FileName = "/bin/sh", Arguments = ["-c", "echo ok"], RequiredOutput = "ok" };
        var events = await Run(orchestrator, new CodingTurnRequest
        {
            ConversationId = "c", UserMessage = "x",
            Workspace = new CodingWorkspace { RepositoryRoot = _repoB, AcceptanceChecks = [check] },
        });

        var acceptance = Assert.Single(events.OfType<CodingAcceptanceEvaluated>()).Result;
        Assert.True(acceptance.Status == CodingAcceptanceStatus.Passed, acceptance.Summary + " " + string.Join(" | ", acceptance.Checks.Select(c => c.Evidence)));
        Assert.True(Assert.Single(acceptance.Checks).Passed);
        Assert.Empty(events.OfType<TurnErrored>());
        Assert.Single(api); // no repair turn
        Assert.Null(new AsyncLocalCurrentTurnContextAccessor().Current); // the verification context does not leak
    }

    [Fact]
    public async Task Host_user_authority_runs_native_lane_in_the_workspace()
    {
        var rig = Build(new CodingWorkspace { RepositoryRoot = _repoB });
        await Run(rig.Orchestrator, new CodingTurnRequest
        {
            ConversationId = "c", UserMessage = "x", CommunicationScope = AgentCommunicationScope.UserRequested("host-ui"),
        });
        var native = Assert.Single(rig.Native);
        Assert.Equal(_repoB, native.RepositoryRoot);
        Assert.Equal("host-ui", native.Authority);
        Assert.Equal(new CodingOptions().ProfileId, native.ProfileId);
        Assert.Empty(rig.Api);
    }

    [Fact]
    public async Task Lanes_come_from_the_host_source_per_conversation()
    {
        var source = new PerConversationLanes();
        var rig = Build(new CodingWorkspace { RepositoryRoot = _repoA }, source);
        Assert.Equal("listed", Assert.Single(rig.Orchestrator.Lanes).Name);

        var events = await Run(rig.Orchestrator, new CodingTurnRequest { ConversationId = "conv-7", UserMessage = "x" });
        Assert.Equal(["conv-7"], source.Asked);
        Assert.Equal("routed-conv-7", Assert.Single(events.OfType<CodingLaneSelected>()).Lane.Name);
        Assert.Equal("Router", Assert.Single(rig.Api).ProviderOverride);
    }

    // ---- instructions ----

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Access_context_matches_real_outside_workspace_file_access(bool userInitiated, bool allowOutside)
    {
        var options = Options.Create(new SandboxOptions { Root = _configured, AllowAbsolutePathsOutsideTurnWorkspace = allowOutside });
        var injector = new RepoInstructionsInjector(Options.Create(new CodingOptions()), sandboxOptions: options);
        var block = await injector.BuildAsync(new ContextInjectionRequest("c", "Inspect a local file", Guid.NewGuid(),
            CommunicationScopeUserInitiated: userInitiated, WorkspaceRoot: _repoA));
        var file = Path.Combine(_repoB, "outside.txt");
        await File.WriteAllTextAsync(file, "outside evidence");
        EnterTurn(_repoA, userInitiated ? AgentCommunicationScope.UserRequested("fixture") : AgentCommunicationScope.None);
        var resolver = new SandboxPathResolver(options);
        var tool = new ReadFileTool(resolver).Descriptor;
        if (userInitiated && allowOutside)
        {
            Assert.Contains("explicit absolute paths outside the workspace are allowed", block);
            var result = await tool.Invoke(new ReadFileTool.Input { Path = file }, default);
            Assert.Contains("outside evidence", System.Text.Json.JsonSerializer.Serialize(result));
            // Relative traversal still fails, even when the explicit absolute path succeeds.
            Assert.Throws<UnauthorizedAccessException>(() => resolver.Resolve("../repo-b/outside.txt"));
        }
        else
        {
            Assert.Contains("limited to this workspace", block);
            Assert.DoesNotContain("explicit absolute paths outside the workspace are allowed", block);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await tool.Invoke(new ReadFileTool.Input { Path = file }, default));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Access_context_does_not_grant_general_access_to_missions_or_tool_free_turns(bool toolsWithheld)
    {
        var injector = new RepoInstructionsInjector(Options.Create(new CodingOptions()),
            sandboxOptions: Options.Create(new SandboxOptions()));
        var block = await injector.BuildAsync(new ContextInjectionRequest("c", "Inspect files", Guid.NewGuid(),
            CommunicationScopeUserInitiated: true, WorkspaceRoot: _repoA)
        {
            ToolsWithheldByUser = toolsWithheld,
            MissionObjectiveId = toolsWithheld ? null : Guid.NewGuid(),
        });
        Assert.DoesNotContain("explicit absolute paths outside the workspace are allowed", block);
        Assert.Contains(toolsWithheld ? "withheld tools" : "current resource grants", block);
    }

    [Fact]
    public async Task Access_context_without_host_policy_does_not_invent_a_boundary()
    {
        var injector = new RepoInstructionsInjector(Options.Create(new CodingOptions()));
        var block = await injector.BuildAsync(new ContextInjectionRequest("c", "Inspect files", Guid.NewGuid(),
            CommunicationScopeUserInitiated: true, WorkspaceRoot: _repoA));
        Assert.Contains("No outside-workspace path policy was supplied", block);
        Assert.DoesNotContain("explicit absolute paths outside the workspace are allowed", block);
        Assert.DoesNotContain("File tools are limited to this workspace", block);
    }

    [Fact]
    public async Task Instructions_come_from_the_turns_workspace()
    {
        await File.WriteAllTextAsync(Path.Combine(_repoA, "AGENTS.md"), "Rules for A");
        await File.WriteAllTextAsync(Path.Combine(_repoB, "AGENTS.md"), "Rules for B");
        var docs = Directory.CreateDirectory(Path.Combine(_repoB, "docs")).FullName;
        await File.WriteAllTextAsync(Path.Combine(docs, "CLAUDE.md"), "Docs rules");
        var active = new ActiveCodingWorkspaces();
        var injector = new RepoInstructionsInjector(Options.Create(new CodingOptions { RepositoryRoot = _configured }), null, active);

        Assert.Null(await injector.BuildAsync(new ContextInjectionRequest("c", "hi", Guid.NewGuid())));

        var fromTurn = await injector.BuildAsync(new ContextInjectionRequest("c", "hi", Guid.NewGuid(), WorkspaceRoot: _repoA));
        Assert.Contains("Rules for A", fromTurn);
        Assert.DoesNotContain("Rules for B", fromTurn);

        using (active.Enter("c", new CodingWorkspace { RepositoryRoot = _repoB, InstructionRoots = [_repoB, "docs"] }))
        {
            var block = await injector.BuildAsync(new ContextInjectionRequest("c", "hi", Guid.NewGuid(), WorkspaceRoot: _repoA));
            Assert.Contains("Rules for B", block);
            Assert.Contains("Docs rules", block);
            Assert.DoesNotContain("Rules for A", block);
            Assert.True(block!.IndexOf("Rules for B", StringComparison.Ordinal) < block.IndexOf("Docs rules", StringComparison.Ordinal));
        }
        Assert.Null(active.Get("c"));
    }

    [Fact]
    public async Task A_large_outer_instruction_file_cannot_crowd_out_the_repositorys_own()
    {
        await File.WriteAllTextAsync(Path.Combine(_repoB, "AGENTS.md"), "Rules for B");
        var outer = Directory.CreateDirectory(Path.Combine(_repoB, "outer-home")).FullName;
        await File.WriteAllTextAsync(Path.Combine(outer, "CLAUDE.md"), new string('x', 5000));
        var active = new ActiveCodingWorkspaces();
        var injector = new RepoInstructionsInjector(Options.Create(new CodingOptions { RepositoryRoot = _configured, MaxInstructionCharacters = 1000 }), null, active);

        using (active.Enter("c", new CodingWorkspace { RepositoryRoot = _repoB, InstructionRoots = [outer, _repoB] }))
        {
            var block = (await injector.BuildAsync(new ContextInjectionRequest("c", "hi", Guid.NewGuid())))!;
            Assert.Contains("Rules for B", block); // the repository's own file keeps its budget
            Assert.Contains("[truncated]", block);  // the outer file gets what is left
            Assert.True(block.IndexOf("outer-home", StringComparison.Ordinal) < block.IndexOf("Rules for B", StringComparison.Ordinal));
        }
    }
}
