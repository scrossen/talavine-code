using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Runtime;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.ToolUseEngine;
using SwarmCore.Coding.Routing;
using SwarmCore.Coding.Tools;
using SwarmCore.Missions;
using SwarmCore.Testing;

namespace SwarmCore.Coding.Tests;

public sealed class NativeCommitFinishingTests : IDisposable
{
    // Governed workspace admission resolves ancestor aliases (including macOS /var -> /private/var)
    // before the snapshot, sandbox and turn context receive their original repository identity.
    private readonly string root = SandboxPathResolver.ResolvePhysicalPath(
        Path.Combine(Path.GetTempPath(), "native-commit-" + Guid.NewGuid().ToString("N")));
    private readonly SandboxPathResolver sandbox;
    public NativeCommitFinishingTests()
    {
        Directory.CreateDirectory(root);
        sandbox = new(Options.Create(new SandboxOptions { Root = root }));
        Git("init"); Git("config", "user.name", "Test"); Git("config", "user.email", "test@example.invalid");
        File.WriteAllText(Path.Combine(root, "clean.txt"), "base");
        File.WriteAllText(Path.Combine(root, "dirty.txt"), "base");
        File.WriteAllText(Path.Combine(root, "staged.txt"), "base");
        Git("add", "."); Git("commit", "-m", "baseline");
    }
    private string Git(params string[] args)
    {
        var r = TestGit.Run(root, args); Assert.True(r.ExitCode == 0, r.Error); return r.Output;
    }
    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }
    private static ToolCallStarted Write(string path, string id = "write") => new("native", id, "Write", new JsonObject { ["file_path"] = path });
    private static ToolCallCompleted Result(string id = "write", bool failed = false) => new("native", id, "Write", failed ? "failed" : "saved", failed, TimeSpan.Zero);

    [Fact]
    public async Task Finisher_commits_only_successful_clean_at_start_files_and_preserves_dirty_and_staged_changes()
    {
        File.WriteAllText(Path.Combine(root, "dirty.txt"), "unrelated dirty");
        File.WriteAllText(Path.Combine(root, "staged.txt"), "unrelated staged"); Git("add", "staged.txt");
        var beforeIndex = Git("diff", "--cached", "--raw");
        var snapshot = await NativeCommitSnapshot.CaptureAsync(root, default);
        await snapshot.ObserveAsync(Write(Path.Combine(root, "clean.txt")), default); File.WriteAllText(Path.Combine(root, "clean.txt"), "native edit"); await snapshot.ObserveAsync(Result(), default);
        await snapshot.ObserveAsync(Write(Path.Combine(root, "dirty.txt"), "dirty"), default); await snapshot.ObserveAsync(Result("dirty"), default);
        var commitScope = await snapshot.FinishAsync("conversation", default);
        Assert.Equal(["clean.txt"], commitScope.Paths);
        using var active = commitScope.Enter();
        var accessor = new AsyncLocalCurrentTurnContextAccessor();
        var old = accessor.Current;
        accessor.Current = new CurrentTurnContext("conversation", null, Guid.NewGuid(), CommunicationScopeUserInitiated: true)
        { WorkspaceRoot = root, AdmittedToolIds = new HashSet<string> { GitCommitTool.ToolId } };
        try
        {
            var denied = (GitCommitTool.Output)(await new GitCommitTool(sandbox).Descriptor.Invoke(new GitCommitTool.Input
                { Message = "must not include unrelated file", Paths = ["staged.txt"] }, default))!;
            Assert.False(denied.Succeeded);
            var committed = (GitCommitTool.Output)(await new GitCommitTool(sandbox).Descriptor.Invoke(new GitCommitTool.Input
                { Message = "native edit", Paths = ["clean.txt"] }, default))!;
            Assert.True(committed.Succeeded, committed.Error);
            Assert.Equal("clean.txt", Git("show", "--format=", "--name-only", "HEAD").Trim());
            Assert.Equal(beforeIndex, Git("diff", "--cached", "--raw"));
            Assert.Equal("unrelated dirty", File.ReadAllText(Path.Combine(root, "dirty.txt")));
            var retry = (GitCommitTool.Output)(await new GitCommitTool(sandbox).Descriptor.Invoke(new GitCommitTool.Input
                { Message = "retry", Paths = ["clean.txt"] }, default))!;
            Assert.False(retry.Succeeded);
        }
        finally { accessor.Current = old; }
    }

    [Fact]
    public async Task Failed_or_unknown_writes_and_paths_outside_the_repository_are_not_commit_candidates()
    {
        var snapshot = await NativeCommitSnapshot.CaptureAsync(root, default);
        await snapshot.ObserveAsync(Write("clean.txt"), default); File.WriteAllText(Path.Combine(root, "clean.txt"), "changed"); await snapshot.ObserveAsync(Result(failed: true), default);
        await snapshot.ObserveAsync(Write("../outside.txt", "outside"), default); await snapshot.ObserveAsync(Result("outside"), default);
        await snapshot.ObserveAsync(Write("unknown.txt", "unknown"), default); File.WriteAllText(Path.Combine(root, "unknown.txt"), "unknown outcome");
        var commitScope = await snapshot.FinishAsync("conversation", default);
        Assert.Empty(commitScope.Paths);
    }

    [Fact]
    public async Task A_different_selected_repository_cannot_use_proven_original_repository_files()
    {
        var snapshot = await NativeCommitSnapshot.CaptureAsync(root, default);
        await snapshot.ObserveAsync(Write("clean.txt"), default);
        File.WriteAllText(Path.Combine(root, "clean.txt"), "native edit");
        await snapshot.ObserveAsync(Result(), default);
        var scope = await snapshot.FinishAsync("conversation", default);
        var head = Git("rev-parse", "HEAD");
        var index = Git("ls-files", "--stage");
        var selected = Path.Combine(root, "different-repository");
        Directory.CreateDirectory(selected);
        File.WriteAllText(Path.Combine(selected, "clean.txt"), "unrelated same-name file");

        var error = await scope.ValidateAsync(new GitCommitTool.Input
            { Message = "native edit", Paths = ["clean.txt"] }, selected, default);

        Assert.Contains("original repository", error);
        Assert.Equal(head, Git("rev-parse", "HEAD"));
        Assert.Equal(index, Git("ls-files", "--stage"));
        Assert.Equal("unrelated same-name file", File.ReadAllText(Path.Combine(selected, "clean.txt")));
    }

    [PosixFact]
    public async Task A_root_alias_does_not_replace_the_admitted_physical_repository_identity()
    {
        var alias = root + "-alias";
        Directory.CreateSymbolicLink(alias, root);
        try
        {
            Assert.Equal(root, SandboxPathResolver.ResolvePhysicalPath(alias));
            var snapshot = await NativeCommitSnapshot.CaptureAsync(root, default);
            await snapshot.ObserveAsync(Write("clean.txt"), default);
            File.WriteAllText(Path.Combine(root, "clean.txt"), "native edit");
            await snapshot.ObserveAsync(Result(), default);
            var scope = await snapshot.FinishAsync("conversation", default);

            var error = await scope.ValidateAsync(new GitCommitTool.Input
                { Message = "native edit", Paths = ["clean.txt"] }, alias, default);

            Assert.Contains("original repository", error);
            Assert.Empty(Git("diff", "--cached", "--name-only"));
        }
        finally { Directory.Delete(alias); }
    }

    [PosixFact]
    public async Task Linked_descendant_files_and_directories_are_never_native_commit_candidates()
    {
        var directory = Path.Combine(root, "actual");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "value.txt"), "base");
        Git("add", "actual/value.txt"); Git("commit", "-m", "clean linked target baseline");
        Assert.Empty(Git("status", "--porcelain"));
        var snapshot = await NativeCommitSnapshot.CaptureAsync(root, default);
        var linkedFile = Path.Combine(root, "linked.txt");
        var linkedDirectory = Path.Combine(root, "linked-directory");
        File.CreateSymbolicLink(linkedFile, Path.Combine(directory, "value.txt"));
        Directory.CreateSymbolicLink(linkedDirectory, directory);
        try
        {
            await snapshot.ObserveAsync(Write(linkedFile, "file-link"), default);
            await snapshot.ObserveAsync(Write(Path.Combine(linkedDirectory, "value.txt"), "directory-link"), default);
            File.WriteAllText(Path.Combine(directory, "value.txt"), "native edit through links");
            await snapshot.ObserveAsync(Result("file-link"), default);
            await snapshot.ObserveAsync(Result("directory-link"), default);

            Assert.Empty((await snapshot.FinishAsync("conversation", default)).Paths);
            Assert.Empty(Git("diff", "--cached", "--name-only"));
        }
        finally { File.Delete(linkedFile); Directory.Delete(linkedDirectory); }
    }

    [Fact]
    public async Task A_file_changed_after_the_native_receipt_fails_closed_before_staging()
    {
        var snapshot = await NativeCommitSnapshot.CaptureAsync(root, default);
        await snapshot.ObserveAsync(Write("clean.txt"), default); File.WriteAllText(Path.Combine(root, "clean.txt"), "native edit"); await snapshot.ObserveAsync(Result(), default);
        var commitScope = await snapshot.FinishAsync("conversation", default);
        File.WriteAllText(Path.Combine(root, "clean.txt"), "later user edit");
        var error = await commitScope.ValidateAsync(new GitCommitTool.Input { Message = "native edit", Paths = ["clean.txt"] }, root, default);
        Assert.Contains("changed", error);
        Assert.Empty(Git("diff", "--cached", "--name-only"));
    }

    [Fact]
    public async Task The_finishing_scope_stays_visible_after_every_yield_and_inside_thread_pool_tool_dispatch()
    {
        var snapshot = await NativeCommitSnapshot.CaptureAsync(root, default);
        var commitScope = await snapshot.FinishAsync("conversation", default);
        var runner = new YieldingFinisher();
        var request = new AgentTurnRequest
        {
            ConversationId = "conversation", UserMessage = "commit", CommunicationScope = AgentCommunicationScope.UserRequested("person")
        };
        var events = 0;
        // The consumer's own ExecutionContext never holds the scope, like the orchestrator's.
        await foreach (var _ in NativeCommitFinishing.RunAsync(runner, request, commitScope, default)) events++;

        Assert.Equal(3, events);
        Assert.Equal(6, runner.Observed.Count);
        Assert.All(runner.Observed, seen => Assert.Same(commitScope, seen));
        Assert.Null(NativeCommitScope.Current);
    }

    [Theory]
    [InlineData("Create CLEAN.txt and commit only that file.")]
    [InlineData("Create CLEAN.txt. Do not commit; the quoted word `commit` is just file content.")]
    [InlineData("Create CLEAN.txt and commit only that file.", true)]
    public async Task Confined_claude_finishes_through_the_admitted_engine_with_original_request_and_acceptance_afterward(string original, bool refused = false)
    {
        var seen = new List<AgentTurnRequest>();
        var services = new ServiceCollection();
        services.AddSingleton<IAgentTurnRunner>(new Finisher(seen));
        services.AddScoped<INativeLaneRunner>(_ => new Native(root, refused));
        var rewriter = new Rewriter();
        services.AddSingleton<ITranscriptRewriter>(rewriter);
        services.AddSingleton<ICodingAcceptanceVerifier>(new Verifier(seen));
        var lanes = new CodingLaneOptions { Lanes = [new("claude", "ClaudeCodeNative", "claude"), new("engine", "api", "model")] };
        services.AddSingleton<ICodingLaneSource>(new OptionsCodingLaneSource(Options.Create(lanes)));
        using var composed = services.BuildServiceProvider();
        var orchestrator = new CodingTurnOrchestrator(composed.GetRequiredService<IServiceScopeFactory>(), Options.Create(new CodingOptions()), NullLogger<CodingTurnOrchestrator>.Instance);
        var events = new List<TurnEvent>();
        await foreach (var e in orchestrator.RunAsync(new CodingTurnRequest
        {
            ConversationId = "conversation", UserMessage = original, Workspace = new() { RepositoryRoot = root },
            CommunicationScope = AgentCommunicationScope.UserRequested("person"), NativeContainment = NativeLaneContainment.WorkspaceConfined,
            Metadata = new Dictionary<string, string> { ["objective"] = "same-objective" }
        })) events.Add(e);
        var engine = Assert.Single(seen);
        Assert.Contains(original, engine.UserMessage);
        Assert.Equal("same-objective", engine.Metadata!["objective"]);
        Assert.True(engine.CommunicationScopeUserInitiated);
        Assert.True(engine.IsContinuation);
        Assert.True(engine.Metadata.ContainsKey(NativeCommitToolFilter.MetadataKey));
        Assert.Equal(refused, rewriter.Replaced);
        if (refused) Assert.DoesNotContain("policy", engine.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Single(events.OfType<TurnCompleted>()); // native answer is not prematurely accepted
        Assert.Contains(events, e => e is CodingAcceptanceEvaluated);
    }

    [Theory]
    [InlineData("Create CLEAN.txt and commit only that file.", 1)]
    [InlineData("Create CLEAN.txt and commit only that file.", 2)]
    [InlineData("Create CLEAN.txt. Do not commit; the quoted word `commit` is just file content.", 1)]
    [InlineData("Create CLEAN.txt. Do not commit; the quoted word `commit` is just file content.", 2)]
    public async Task Unavailable_finishing_lanes_preserve_completed_native_evidence_and_original_authority(string original, int unavailableLanes)
    {
        var seen = new List<AgentTurnRequest>();
        var services = new ServiceCollection();
        services.AddSingleton<IAgentTurnRunner>(new FallbackFinisher(seen, unavailableLanes));
        services.AddScoped<INativeLaneRunner>(_ => new Native(root));
        services.AddSingleton<ICodingAcceptanceVerifier>(new FallbackVerifier(seen, unavailableLanes + 1));
        var options = new CodingLaneOptions
        {
            MaxLanesPerRequest = unavailableLanes + 2,
            Lanes = [new("claude", "ClaudeCodeNative", "claude"),
                .. Enumerable.Range(0, unavailableLanes).Select(i => new CodingLane("unavailable-" + i, "api", "missing-" + i)),
                new("finisher", "api", "model")]
        };
        services.AddSingleton<ICodingLaneSource>(new OptionsCodingLaneSource(Options.Create(options)));
        using var composed = services.BuildServiceProvider();
        var orchestrator = new CodingTurnOrchestrator(composed.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new CodingOptions()), NullLogger<CodingTurnOrchestrator>.Instance);
        var events = new List<TurnEvent>();
        await foreach (var e in orchestrator.RunAsync(new CodingTurnRequest
        {
            ConversationId = "conversation", UserMessage = original, Workspace = new() { RepositoryRoot = root },
            CommunicationScope = AgentCommunicationScope.UserRequested("person"), NativeContainment = NativeLaneContainment.WorkspaceConfined,
            Metadata = new Dictionary<string, string> { ["objective"] = "same-objective" }
        })) events.Add(e);

        Assert.Equal(unavailableLanes + 1, seen.Count);
        Assert.Contains("Saved file; no command tool.", seen[0].UserMessage);
        Assert.Contains("CLEAN.txt", seen[0].UserMessage);
        foreach (var request in seen)
        {
            Assert.Equal(seen[0].UserMessage, request.UserMessage);
            Assert.Contains(original, request.UserMessage);
            Assert.DoesNotContain("No workspace changes were made yet", request.UserMessage);
            Assert.Equal("same-objective", request.Metadata!["objective"]);
            Assert.True(request.Metadata.ContainsKey(NativeCommitToolFilter.MetadataKey));
            Assert.True(request.CommunicationScopeUserInitiated);
            Assert.True(request.IsContinuation);
        }
        Assert.Single(events.OfType<ToolCallCompleted>().Where(e => e.ToolName == "Write"));
        Assert.Equal("native edit", File.ReadAllText(Path.Combine(root, "CLEAN.txt")));
        Assert.Single(events.OfType<TurnCompleted>());
        Assert.Single(events.OfType<CodingAcceptanceEvaluated>());
        Assert.Empty(Git("diff", "--cached", "--name-only"));
    }

    [Fact]
    public async Task A_budget_stop_after_native_writes_ends_with_a_limit_event_instead_of_a_cancellation_exception()
    {
        var seen = new List<AgentTurnRequest>();
        var services = new ServiceCollection();
        services.AddSingleton<IAgentTurnRunner>(new Finisher(seen));
        services.AddScoped<INativeLaneRunner>(_ => new Native(root));
        var lanes = new CodingLaneOptions { Lanes = [new("claude", "ClaudeCodeNative", "claude"), new("engine", "api", "model")] };
        services.AddSingleton<ICodingLaneSource>(new OptionsCodingLaneSource(Options.Create(lanes)));
        using var composed = services.BuildServiceProvider();
        var orchestrator = new CodingTurnOrchestrator(composed.GetRequiredService<IServiceScopeFactory>(), Options.Create(new CodingOptions()), NullLogger<CodingTurnOrchestrator>.Instance);
        var events = new List<TurnEvent>();
        // The completed native write reaches the tool budget, which cancels the attempt token before the
        // native answer; the finishing evidence must not be read on that cancelled token.
        await foreach (var e in orchestrator.RunAsync(new CodingTurnRequest
        {
            ConversationId = "conversation", UserMessage = "Create CLEAN.txt and commit only that file.", Workspace = new() { RepositoryRoot = root },
            CommunicationScope = AgentCommunicationScope.UserRequested("person"), NativeContainment = NativeLaneContainment.WorkspaceConfined,
            ExecutionLimits = new CodingExecutionLimits { MaxToolCalls = 1 }
        })) events.Add(e);

        Assert.Contains(events, e => e is CodingTurnLimitReached);
        Assert.Empty(seen); // no finishing turn after a budget stop
        Assert.Empty(Git("diff", "--cached", "--name-only"));
    }

    [Fact]
    public async Task Finishing_evidence_that_cannot_be_reread_authorizes_no_commit_instead_of_throwing()
    {
        var snapshot = await NativeCommitSnapshot.CaptureAsync(root, default);
        await snapshot.ObserveAsync(Write("clean.txt"), default); File.WriteAllText(Path.Combine(root, "clean.txt"), "native edit");
        await snapshot.ObserveAsync(Result(), default);
        // The repository breaks under the finisher: a gitfile naming a missing directory makes every git read fail
        // (git does not continue discovery into parent folders past an invalid gitfile).
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, ".git"), "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Path.Combine(root, ".git"), recursive: true);
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: missing-git-directory\n");

        var scope = await snapshot.FinishAsync("conversation", default);

        Assert.Empty(scope.Paths);
        Assert.False(string.IsNullOrWhiteSpace(scope.UnavailableEvidence));
    }

    [Theory]
    [InlineData(false, false, true, "conversation")]
    [InlineData(true, true, true, "conversation")]
    [InlineData(true, false, false, "conversation")]
    [InlineData(true, false, true, "different-conversation")]
    public async Task Finishing_commit_does_not_create_authority_or_admission(bool direct, bool unattended, bool admitted, string conversation)
    {
        var snapshot = await NativeCommitSnapshot.CaptureAsync(root, default);
        await snapshot.ObserveAsync(Write("clean.txt"), default); File.WriteAllText(Path.Combine(root, "clean.txt"), "native edit");
        await snapshot.ObserveAsync(Result(), default);
        var scope = await snapshot.FinishAsync("conversation", default);
        var accessor = new AsyncLocalCurrentTurnContextAccessor(); var old = accessor.Current;
        accessor.Current = new CurrentTurnContext(conversation, null, Guid.NewGuid(), CommunicationScopeUserInitiated: direct, UnattendedAgentTurn: unattended)
        { WorkspaceRoot = root, AdmittedToolIds = admitted ? new HashSet<string> { GitCommitTool.ToolId } : new HashSet<string>() };
        try
        {
            using var active = scope.Enter();
            var result = (GitCommitTool.Output)(await new GitCommitTool(sandbox).Descriptor.Invoke(
                new GitCommitTool.Input { Message = "attempt", Paths = ["clean.txt"] }, default))!;
            Assert.False(result.Succeeded); Assert.Empty(Git("diff", "--cached", "--name-only"));
        }
        finally { accessor.Current = old; }
    }

    [Fact]
    public async Task Finishing_catalog_only_narrows_the_profiles_admitted_tools()
    {
        var filter = new NativeCommitToolFilter();
        ToolDescriptor[] tools = [new GitStatusTool(sandbox).Descriptor,
            ToolDescriptor.Create<object, object>("shell.exec", "test", "test shell", ToolCostClass.Cheap, ToolSideEffect.Exec, (_, _) => Task.FromResult<object>(new()))];
        var request = new AgentTurnRequest { ConversationId = "conversation", UserMessage = "commit", CommunicationScope = AgentCommunicationScope.UserRequested("test"),
            Metadata = new Dictionary<string, string> { [NativeCommitToolFilter.MetadataKey] = "1" } };
        var kept = await filter.FilterAsync(request, new AgentProfileSnapshot { Id = Guid.NewGuid(), Name = "test", SystemPrompt = "test", EnabledToolIds = [] }, tools);
        Assert.Equal([GitStatusTool.ToolId], kept.Select(tool => tool.ToolId));
        Assert.DoesNotContain(kept, tool => tool.ToolId == GitCommitTool.ToolId);
    }

    [Fact]
    public async Task A_write_changed_after_its_success_before_native_completion_is_not_a_candidate()
    {
        var snapshot = await NativeCommitSnapshot.CaptureAsync(root, default);
        await snapshot.ObserveAsync(Write("clean.txt"), default); File.WriteAllText(Path.Combine(root, "clean.txt"), "native edit");
        await snapshot.ObserveAsync(Result(), default);
        File.WriteAllText(Path.Combine(root, "clean.txt"), "user edit after receipt");
        Assert.Empty((await snapshot.FinishAsync("conversation", default)).Paths);
    }

    [Fact]
    public async Task An_unborn_repository_can_have_a_scoped_first_commit()
    {
        var unborn = Path.Combine(root, "unborn"); Directory.CreateDirectory(unborn);
        Assert.Equal(0, TestGit.Run(unborn, "init").ExitCode);
        var snapshot = await NativeCommitSnapshot.CaptureAsync(unborn, default);
        await snapshot.ObserveAsync(Write("new.txt"), default); File.WriteAllText(Path.Combine(unborn, "new.txt"), "native edit");
        await snapshot.ObserveAsync(Result(), default);
        Assert.Equal(["new.txt"], (await snapshot.FinishAsync("conversation", default)).Paths);
    }

    [WindowsOrMacOSFact]
    public async Task A_case_alias_cannot_turn_a_preexisting_dirty_file_into_a_native_commit_candidate()
    {
        File.WriteAllText(Path.Combine(root, "dirty.txt"), "existing user change");
        var snapshot = await NativeCommitSnapshot.CaptureAsync(root, default);
        await snapshot.ObserveAsync(Write("DIRTY.txt"), default);
        File.WriteAllText(Path.Combine(root, "dirty.txt"), "native edit on top of existing user change");
        await snapshot.ObserveAsync(Result(), default);
        Assert.Empty((await snapshot.FinishAsync("conversation", default)).Paths);
    }

    [Theory]
    [InlineData("acting-agent")]
    [InlineData("repair-grant")]
    public async Task Acting_agent_or_repair_grant_cannot_use_the_direct_user_native_commit_step(string kind)
    {
        var snapshot = await NativeCommitSnapshot.CaptureAsync(root, default);
        await snapshot.ObserveAsync(Write("clean.txt"), default); File.WriteAllText(Path.Combine(root, "clean.txt"), "native edit");
        await snapshot.ObserveAsync(Result(), default);
        var scope = await snapshot.FinishAsync("conversation", default);
        var accessor = new AsyncLocalCurrentTurnContextAccessor(); var old = accessor.Current;
        accessor.Current = new CurrentTurnContext("conversation", null, Guid.NewGuid(), CommunicationScopeUserInitiated: true,
            ActingAgentId: kind == "acting-agent" ? "worker" : null)
        {
            WorkspaceRoot = root, AdmittedToolIds = new HashSet<string> { GitCommitTool.ToolId },
            CodingRepairGrant = kind == "repair-grant" ? CodingRepairGrant.ForConfiguredRepository(
                AgentRepairAuthority.SystemRepair("test", "test"), root, [GitCommitTool.ToolId]) : null
        };
        try
        {
            using var active = scope.Enter();
            var output = (GitCommitTool.Output)(await new GitCommitTool(sandbox).Descriptor.Invoke(
                new GitCommitTool.Input { Message = "attempt", Paths = ["clean.txt"] }, default))!;
            Assert.False(output.Succeeded); Assert.Empty(Git("diff", "--cached", "--name-only"));
        }
        finally { accessor.Current = old; }
    }

    [Fact]
    public async Task Live_worktree_mutation_after_owned_snapshot_does_not_enter_the_commit()
    {
        File.WriteAllText(Path.Combine(root, "staged.txt"), "unrelated staged"); Git("add", "staged.txt");
        var beforeIndex = Git("diff", "--cached", "--raw");
        var snapshot = await NativeCommitSnapshot.CaptureAsync(root, default);
        await snapshot.ObserveAsync(Write("clean.txt"), default); File.WriteAllText(Path.Combine(root, "clean.txt"), "native receipt bytes");
        await snapshot.ObserveAsync(Result(), default);
        var scope = await snapshot.FinishAsync("conversation", default);
        string temporary;
        using (var files = await scope.PrepareFilesAsync([Path.Combine(root, "clean.txt")], default))
        {
            temporary = files.Root;
            File.WriteAllText(Path.Combine(root, "clean.txt"), "later user bytes");
            var add = await GitProcess.RunAsync(root, files.Arguments(["--literal-pathspecs", "add", "--", .. files.Paths]), TimeSpan.FromSeconds(30), default);
            Assert.Equal(0, add.ExitCode);
            var commit = await GitProcess.RunAsync(root, files.Arguments(["--literal-pathspecs", "commit", "--only", "-m", "native snapshot", "--", .. files.Paths]), TimeSpan.FromSeconds(30), default);
            Assert.Equal(0, commit.ExitCode);
            Assert.Equal("native receipt bytes", Git("show", "HEAD:clean.txt"));
            Assert.Equal("later user bytes", File.ReadAllText(Path.Combine(root, "clean.txt")));
            Assert.Equal(beforeIndex, Git("diff", "--cached", "--raw"));
        }
        Assert.False(Directory.Exists(temporary));
    }

    [Fact]
    public async Task A_changed_index_after_snapshot_is_rejected_before_staging_native_files()
    {
        var snapshot = await NativeCommitSnapshot.CaptureAsync(root, default);
        await snapshot.ObserveAsync(Write("clean.txt"), default); File.WriteAllText(Path.Combine(root, "clean.txt"), "native edit");
        await snapshot.ObserveAsync(Result(), default);
        var scope = await snapshot.FinishAsync("conversation", default);
        using var files = await scope.PrepareFilesAsync(["clean.txt"], default);
        File.WriteAllText(Path.Combine(root, "staged.txt"), "later user stage"); Git("add", "staged.txt");
        Assert.Contains("index changed", await scope.ValidateRepositoryAsync(default));
        Assert.Equal("staged.txt", Git("diff", "--cached", "--name-only").Trim());
    }

    private sealed class Rewriter : ITranscriptRewriter
    {
        public bool Replaced { get; private set; }
        public Task<bool> ReplaceLastAssistantTextAsync(string conversationId, string oldText, string replacementText, CancellationToken ct = default)
        {
            Assert.Contains("policy", oldText); Replaced = true; return Task.FromResult(true);
        }
    }
    private sealed class Native(string root, bool refused = false) : INativeLaneRunner
    {
        public bool Supports(CodingLane lane) => lane.IsNative;
        public async IAsyncEnumerable<TurnEvent> RunAsync(NativeLaneTurn turn, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield(); yield return Write("CLEAN.txt");
            await File.WriteAllTextAsync(Path.Combine(root, "CLEAN.txt"), "native edit", ct);
            yield return Result(); yield return new TurnCompleted("native", refused ? "I cannot help with that because it is against my policy." : "Saved file; no command tool.", TimeSpan.Zero);
        }
    }
    private sealed class Finisher(List<AgentTurnRequest> seen) : IAgentTurnRunner
    {
        public async IAsyncEnumerable<TurnEvent> RunAsync(AgentTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            seen.Add(request); await Task.Yield();
            Assert.NotNull(NativeCommitScope.Current);
            var scope = NativeCommitScope.Current;
            await Task.Run(() => Assert.Same(scope, NativeCommitScope.Current), ct);
            yield return new TurnCompleted("engine", "Finished original request through host tools.", TimeSpan.Zero);
        }
    }
    private sealed class YieldingFinisher : IAgentTurnRunner
    {
        public List<NativeCommitScope?> Observed { get; } = [];
        public async IAsyncEnumerable<TurnEvent> RunAsync(AgentTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 0; i < 3; i++)
            {
                await Task.Yield();
                Observed.Add(NativeCommitScope.Current);
                // The engine dispatches tools on the thread pool; the scope must flow there too.
                Observed.Add(await Task.Run(() => NativeCommitScope.Current, ct));
                yield return new TurnStarted("engine-" + i, Guid.Empty, "finisher");
            }
        }
    }
    private sealed class FallbackFinisher(List<AgentTurnRequest> seen, int unavailableLanes) : IAgentTurnRunner
    {
        private NativeCommitScope? firstScope;
        public async IAsyncEnumerable<TurnEvent> RunAsync(AgentTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            seen.Add(request); await Task.Yield();
            Assert.NotNull(NativeCommitScope.Current);
            firstScope ??= NativeCommitScope.Current;
            Assert.Same(firstScope, NativeCommitScope.Current);
            if (seen.Count <= unavailableLanes)
                yield return new TurnErrored("unavailable", "The model is not installed.")
                { FailureCategory = SwarmCore.Abstractions.LlmRouting.LlmFailureCategory.ModelNotFound };
            else
                yield return new TurnCompleted("finisher", "Reported the saved native work; no repeated write or implicit commit.", TimeSpan.Zero);
        }
    }
    private sealed class FallbackVerifier(List<AgentTurnRequest> seen, int expected) : ICodingAcceptanceVerifier
    {
        public Task<CodingAcceptanceResult> VerifyAsync(CodingAcceptanceContext context, CancellationToken ct = default)
        {
            Assert.Equal(expected, seen.Count);
            return Task.FromResult(new CodingAcceptanceResult(CodingAcceptanceStatus.Passed, "checked after fallback", []));
        }
    }
    private sealed class Verifier(List<AgentTurnRequest> seen) : ICodingAcceptanceVerifier
    {
        public Task<CodingAcceptanceResult> VerifyAsync(CodingAcceptanceContext context, CancellationToken ct = default)
        {
            Assert.Single(seen); return Task.FromResult(new CodingAcceptanceResult(CodingAcceptanceStatus.Passed, "checked", []));
        }
    }
}
