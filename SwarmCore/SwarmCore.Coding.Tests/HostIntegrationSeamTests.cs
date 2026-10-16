using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.CodingTasks;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.CodingTasks;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Native;
using SwarmCore.Coding.Routing;
using SwarmCore.Coding.Secrets;
using SwarmCore.Coding.Tools;
using SwarmCore.Secrets;
using Xunit;

namespace SwarmCore.Coding.Tests;

/// <summary>Seams a multi-profile host (Talavine) composes the harness through.</summary>
public sealed class HostIntegrationSeamTests
{
    // ── lane health scope ───────────────────────────────────────

    [Fact]
    public void Lane_health_is_scoped_so_one_profiles_failure_does_not_cool_another()
    {
        var tracker = new LaneHealthTracker();
        tracker.MarkUnavailable("openai", "quota", TimeSpan.FromMinutes(5), scope: "profile:a");

        Assert.True(tracker.IsUnavailable("openai", out var why, scope: "profile:a"));
        Assert.Contains("quota", why);
        Assert.False(tracker.IsUnavailable("openai", out _, scope: "profile:b"));
        Assert.False(tracker.IsUnavailable("openai", out _));

        // The null scope keeps the single-profile (SwarmCode) behavior.
        tracker.MarkUnavailable("openai", "auth", TimeSpan.FromMinutes(5));
        Assert.True(tracker.IsUnavailable("openai", out _));
        tracker.Clear("openai", scope: "profile:a");
        Assert.False(tracker.IsUnavailable("openai", out _, scope: "profile:a"));
        Assert.True(tracker.IsUnavailable("openai", out _));
    }

    private sealed class ScopedSource(string scope) : ICodingLaneSource
    {
        public CodingLaneSet GetLanes() => new()
        {
            Lanes = [new("dead", "Dead", "m") { Priority = 0 }, new("ok", "Ok", "m") { Priority = 1 }],
            HealthScope = scope,
        };
    }

    private sealed class ScriptRunner(List<AgentTurnRequest> seen) : IAgentTurnRunner
    {
        public async IAsyncEnumerable<TurnEvent> RunAsync(AgentTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            seen.Add(request);
            await Task.Yield();
            if (request.ProviderOverride == "Dead")
                yield return new TurnErrored("t", "HTTP 401 unauthorized: invalid api key") { FailureCategory = LlmFailureCategory.AuthInvalid };
            else
                yield return new TurnCompleted("t", "done", TimeSpan.Zero);
        }
    }

    private static async Task<List<TurnEvent>> RunInScope(LaneHealthTracker health, string scope, List<AgentTurnRequest> seen,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<IAgentTurnRunner>(_ => new ScriptRunner(seen));
        services.AddSingleton<ICodingLaneSource>(new ScopedSource(scope));
        services.AddSingleton<ICodingWorkspaceResolver>(new TestOrchestrator.FixedWorkspace(new CodingWorkspace { RepositoryRoot = TestOrchestrator.Workspace }));
        var provider = services.BuildServiceProvider();
        var orchestrator = new CodingTurnOrchestrator(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new CodingOptions()), NullLogger<CodingTurnOrchestrator>.Instance, health);
        var events = new List<TurnEvent>();
        await foreach (var e in orchestrator.RunAsync(new CodingTurnRequest
        {
            ConversationId = "c-" + scope, UserMessage = "fix", Metadata = metadata,
            CommunicationScope = AgentCommunicationScope.UserRequested("test"),
        }))
            events.Add(e);
        return events;
    }

    [Fact]
    public async Task Orchestrator_cools_a_failed_route_only_in_the_lane_sets_health_scope()
    {
        var health = new LaneHealthTracker();
        var seen = new List<AgentTurnRequest>();
        await RunInScope(health, "profile:a", seen);
        Assert.Equal(["Dead", "Ok"], seen.Select(r => r.ProviderOverride));

        seen.Clear();
        var again = await RunInScope(health, "profile:a", seen);
        Assert.Contains(again, e => e is CodingLaneSkipped { Lane.Name: "dead" });
        Assert.Equal(["Ok"], seen.Select(r => r.ProviderOverride));

        seen.Clear();
        await RunInScope(health, "profile:b", seen);
        Assert.Equal(["Dead", "Ok"], seen.Select(r => r.ProviderOverride));
    }

    [Fact]
    public async Task Host_metadata_reaches_every_lane_attempt()
    {
        var seen = new List<AgentTurnRequest>();
        var metadata = new Dictionary<string, string> { ["continualObjectiveId"] = Guid.NewGuid().ToString("D") };
        await RunInScope(new LaneHealthTracker(), "profile:m", seen, metadata);
        Assert.Equal(2, seen.Count);
        Assert.All(seen, r => Assert.Same(metadata, r.Metadata));
    }

    // ── shared lane discovery ───────────────────────────────────

    [Fact]
    public void Discovery_offers_native_lanes_first_and_tool_loop_fallbacks_last()
    {
        var native = CodingLaneDiscovery.NativeLanes(_ => true);
        Assert.Equal(["claude", "codex", "gemini-cli"], native.Select(l => l.Name));
        Assert.All(native, l => Assert.True(l.IsNative));
        Assert.Equal(ClaudeCodeNativeLaneRunner.ProviderId, CodingLaneDiscovery.NativeProviderFor(SubscriptionCliCatalog.ClaudeCodeProvider));

        var fallback = CodingLaneDiscovery.ToolLoopFallbackLanes(p => p == SubscriptionCliCatalog.CodexCliProvider);
        Assert.Equal(["codex-tools"], fallback.Select(l => l.Name));
        Assert.Empty(CodingLaneDiscovery.NativeLanes(_ => throw new IOException("probe failed")));
    }

    // ── shared secret intake ────────────────────────────────────

    private sealed class MemoryVault : ISecretVault
    {
        public readonly Dictionary<string, string> Values = new(StringComparer.OrdinalIgnoreCase);
        public Task<SecretEntry> SetAsync(SecretWriteRequest request, CancellationToken ct = default)
        {
            var name = SecretNames.Normalize(request.Name);
            Values[name] = request.Value;
            return Task.FromResult(new SecretEntry(name, request.Kind, null, null, false, DateTime.UtcNow, DateTime.UtcNow, null, true));
        }
        public Task<string?> GetValueAsync(string name, CancellationToken ct = default) => Task.FromResult(Values.GetValueOrDefault(name));
        public Task<SecretLogin?> GetLoginAsync(string name, CancellationToken ct = default) => Task.FromResult<SecretLogin?>(null);
        public Task<SecretEntry?> GetEntryAsync(string name, CancellationToken ct = default) => Task.FromResult<SecretEntry?>(null);
        public Task<bool> HasAsync(string name, CancellationToken ct = default) => Task.FromResult(Values.ContainsKey(name));
        public Task<bool> DeleteAsync(string name, CancellationToken ct = default) => Task.FromResult(Values.Remove(name));
        public Task<IReadOnlyList<SecretEntry>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SecretEntry>>([]);
    }

    [Fact]
    public async Task Shared_scrubber_vaults_typed_credentials_and_leaves_code_alone()
    {
        var vault = new MemoryVault();
        const string token = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";
        var result = await SecretMessageScrubber.ScrubAsync(vault, $"my github token is {token}, use it to push");

        Assert.True(result.Changed);
        Assert.DoesNotContain(token, result.Text);
        Assert.Contains("{{secret:", result.Text);
        Assert.Equal(token, Assert.Single(result.StoredValues));
        Assert.Equal(token, vault.Values[Assert.Single(result.StoredNames)]);

        var code = await SecretMessageScrubber.ScrubAsync(vault, "set token = parser.Next(); and password = form[\"password\"]");
        Assert.False(code.Changed);
    }

    // ── verified missions admit harness tools ───────────────────

    [Fact]
    public void Admission_ids_match_the_harness_tools_and_follow_existing_grants()
    {
        Assert.Equal(
            [EditFileTool.ToolId, GrepTool.ToolId, GlobTool.ToolId, GitStatusTool.ToolId, GitDiffTool.ToolId, GitLogTool.ToolId],
            CodingHarnessToolAdmission.WorkspaceToolIds);

        IReadOnlyList<string> readOnly = ["fs.read_file"];
        Assert.Equal(["fs.read_file", GlobTool.ToolId, GrepTool.ToolId, GitStatusTool.ToolId, GitDiffTool.ToolId, GitLogTool.ToolId],
            CodingHarnessToolAdmission.Expand(readOnly));
        // Listing alone never grants content search or git inspection.
        Assert.Equal(["fs.list_files", GlobTool.ToolId], CodingHarnessToolAdmission.Expand(["fs.list_files"]));

        var write = CodingHarnessToolAdmission.Expand(["fs.write_file", "shell.exec"]);
        Assert.Contains(EditFileTool.ToolId, write);
        Assert.DoesNotContain(GitCommitTool.ToolId, write);
        Assert.DoesNotContain(CredentialsListTool.ToolId, write);

        IReadOnlyList<string> unrelated = ["http.request"];
        Assert.Same(unrelated, CodingHarnessToolAdmission.Expand(unrelated));
    }

    // ── CompatibilityAttempt through the native lane runner ─────

    private sealed class FakeNative(List<NativeLaneTurn> seen, params TurnEvent[] events) : INativeLaneRunner
    {
        public bool Supports(CodingLane lane) => lane.IsNative;
        public async IAsyncEnumerable<TurnEvent> RunAsync(NativeLaneTurn turn, [EnumeratorCancellation] CancellationToken ct = default)
        {
            seen.Add(turn);
            foreach (var e in events) { await Task.Yield(); yield return e; }
        }
    }

    private sealed class Conversations : IConversationStore
    {
        public readonly List<(string Id, string Title)> Created = [];
        public Task<ChatConversationInfo> GetOrCreateAsync(string conversationId, string title, Guid? profileId = null, CancellationToken ct = default)
        {
            Created.Add((conversationId, title));
            return Task.FromResult(new ChatConversationInfo(conversationId, title, profileId, DateTime.UtcNow, DateTime.UtcNow));
        }
        public Task<ChatConversationInfo> CreateAsync(Guid? profileId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChatConversationInfo?> GetAsync(string conversationId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ChatConversationInfo>> ListRecentAsync(int take = 25, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetLatestIdAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ChatTurnInfo>> LoadTurnsAsync(string conversationId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChatTurnInfo> AppendUserTurnAsync(string conversationId, string userText, IReadOnlyList<TurnImageAttachment>? images = null,
            IReadOnlyList<TurnFileAttachment>? files = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CompleteAssistantTurnAsync(Guid turnId, string assistantText, Guid? profileId, TimeSpan duration, string? reasoningContent = null,
            CancellationToken ct = default, string? metadataJson = null) => throw new NotSupportedException();
        public Task DeleteAsync(string conversationId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RenameAsync(string conversationId, string title, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetPinnedAsync(string conversationId, bool isPinned, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateStateAsync(string conversationId, IReadOnlyDictionary<string, object> stateUpdates, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, object>> GetStateAsync(string conversationId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>Scripted git; any non-git process call is the legacy headless CLI.</summary>
    private sealed class FakeGit(CodingTaskProcessResult? cli = null) : ICodingTaskProcessRunner
    {
        public readonly List<CodingTaskProcessRequest> Requests = [];
        public Task<CodingTaskProcessResult> RunAsync(CodingTaskProcessRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            if (Path.GetFileNameWithoutExtension(request.FileName) != "git")
                return Task.FromResult(cli ?? throw new InvalidOperationException("The legacy CLI path must not run."));
            if (request.Arguments.Contains("--get-regexp")) return Task.FromResult(new CodingTaskProcessResult(1, "", "", false));
            var args = string.Join(' ', request.Arguments);
            var stdout = args.Contains("rev-parse --is-inside-work-tree") ? "true"
                : args.Contains("rev-parse HEAD") ? "abc1234"
                : args.Contains("status --porcelain") ? " M src/OrderService.cs"
                : args.Contains("diff --stat") ? " src/OrderService.cs | 4 ++--\n 1 file changed"
                : "";
            return Task.FromResult(new CodingTaskProcessResult(0, stdout, "", false));
        }
    }

    private sealed class Locator : ICodingTaskCliLocator
    {
        public string? FindExecutable(string command) => @"C:\bin\" + command + ".exe";
    }

    private static CodingTaskService Service(ICodingTaskProcessRunner git, ICodingTaskAgentRunner? agent)
        => new(Options.Create(new CodingTaskOptions()), git, new Locator(), NullLogger<CodingTaskService>.Instance, agentRunner: agent);

    private static (NativeLaneCodingTaskAgentRunner Runner, List<NativeLaneTurn> Seen, Conversations Conversations) Agent(
        AsyncLocalCurrentTurnContextAccessor turns, params TurnEvent[] events)
    {
        var seen = new List<NativeLaneTurn>();
        var conversations = new Conversations();
        var services = new ServiceCollection();
        services.AddSingleton<IConversationStore>(conversations);
        services.AddScoped<INativeLaneRunner>(_ => new FakeNative(seen, events));
        var provider = services.BuildServiceProvider();
        return (new NativeLaneCodingTaskAgentRunner(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new CodingOptions()),
            NullLogger<NativeLaneCodingTaskAgentRunner>.Instance, turns), seen, conversations);
    }

    [Fact]
    public async Task Compatibility_attempt_through_the_native_lane_produces_the_legacy_result_shape()
    {
        var repo = Directory.CreateTempSubdirectory("coding-task-native-").FullName;
        try
        {
            var turns = new AsyncLocalCurrentTurnContextAccessor
            {
                Current = new CurrentTurnContext("chat-1", null, Guid.NewGuid(), null, null,
                    CommunicationScopeKind: AgentCommunicationScopeKind.TrustedProfileWide, CommunicationScopeAuthority: "user",
                    CommunicationScopeUserInitiated: true),
            };
            var (agent, seen, conversations) = Agent(turns,
                new TurnStarted("n", Guid.NewGuid(), "claude"),
                new TextChunk("n", "Looking at OrderService."),
                new ToolCallCompleted("n", "1", "Edit", "ok", false, TimeSpan.Zero),
                new TurnCompleted("n", "Fixed the null ref in OrderService.\nDone.", TimeSpan.Zero));
            var request = new CodingTaskRequest { Title = "CRM NullRef", Prompt = "stack trace…", RepoPath = repo, PreferredCli = "claude" };

            var harnessGit = new FakeGit();
            var viaHarness = await Service(harnessGit, agent).RunAsync(request);
            turns.Current = null;
            var legacy = await Service(new FakeGit(new CodingTaskProcessResult(0, "Fixed the null ref in OrderService.\nDone.", "", false)), agent: null)
                .RunAsync(request);

            // Same CodingTaskResult shape as the legacy headless CLI attempt.
            Assert.Equal(legacy.Success, viaHarness.Success);
            Assert.True(viaHarness.Success);
            Assert.Null(viaHarness.Error);
            Assert.Equal(legacy.CliUsed, viaHarness.CliUsed);
            Assert.StartsWith("agents/fix/crm-nullref-", viaHarness.BranchName);
            Assert.Equal(legacy.DiffStat, viaHarness.DiffStat);
            Assert.NotNull(viaHarness.WorktreePath);
            Assert.StartsWith("Fixed the null ref", viaHarness.Summary);
            Assert.Contains("review the diff", viaHarness.Summary);
            Assert.Contains("Fixed the null ref", viaHarness.TranscriptTail);
            Assert.Contains("[native agent claude, WorkspaceConfined:", viaHarness.TranscriptTail);
            Assert.Contains("--permission-mode acceptEdits --tools Read,Edit,MultiEdit,Write,Glob,Grep --disallowedTools Bash,PowerShell,WebFetch,WebSearch,mcp__*", viaHarness.TranscriptTail);
            Assert.Contains("--strict-mcp-config", viaHarness.TranscriptTail);

            // The native lane ran in the isolated worktree, in its own hidden conversation, never merging or pushing.
            var turn = Assert.Single(seen);
            Assert.Equal(ClaudeCodeNativeLaneRunner.ProviderId, turn.Lane.Provider);
            Assert.Equal(NativeLaneContainment.WorkspaceConfined, turn.Containment);
            Assert.Contains("talavine-coding-tasks", turn.RepositoryRoot);
            Assert.NotEqual(repo, turn.RepositoryRoot);
            Assert.StartsWith(CodingTaskConversationIds.Prefix, turn.ConversationId);
            Assert.Contains(NativeLaneCodingTaskAgentRunner.WorktreeInstruction, turn.UserMessage);
            Assert.Equal(turn.ConversationId, Assert.Single(conversations.Created).Id);
            Assert.DoesNotContain(harnessGit.Requests, r => Path.GetFileNameWithoutExtension(r.FileName) != "git");
            Assert.DoesNotContain(harnessGit.Requests, r => r.Arguments.Contains("merge") || r.Arguments.Contains("push"));
            Assert.Contains(harnessGit.Requests, r => r.Arguments.Contains("commit"));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public async Task Native_coding_task_needs_user_initiated_authority()
    {
        var turns = new AsyncLocalCurrentTurnContextAccessor
        {
            Current = new CurrentTurnContext("background", null, Guid.NewGuid(), null, null),
        };
        try
        {
            var (agent, seen, _) = Agent(turns, new TurnCompleted("n", "done", TimeSpan.Zero));
            var result = await agent.RunAsync(new CodingTaskAgentRun("claude", "fix", Path.GetTempPath(), "t", "agents/fix/t", TimeSpan.FromMinutes(1)));
            Assert.Empty(seen);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("user-initiated", result.StandardError);
        }
        finally
        {
            turns.Current = null;
        }
    }

    [Fact]
    public async Task Silent_native_agent_is_stopped_by_the_inactivity_lease()
    {
        var seen = new List<NativeLaneTurn>();
        var services = new ServiceCollection();
        services.AddSingleton<IConversationStore>(new Conversations());
        services.AddScoped<INativeLaneRunner>(_ => new HangingNative(seen));
        var provider = services.BuildServiceProvider();
        var turns = new AsyncLocalCurrentTurnContextAccessor
        {
            Current = new CurrentTurnContext("chat-2", null, Guid.NewGuid(), null, null,
                CommunicationScopeKind: AgentCommunicationScopeKind.TrustedProfileWide, CommunicationScopeAuthority: "user",
                CommunicationScopeUserInitiated: true),
        };
        var agent = new NativeLaneCodingTaskAgentRunner(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new CodingOptions()),
            NullLogger<NativeLaneCodingTaskAgentRunner>.Instance, turns);

        var result = await agent.RunAsync(new CodingTaskAgentRun("codex", "fix", Path.GetTempPath(), "t", "b", TimeSpan.FromMilliseconds(300)));
        turns.Current = null;

        Assert.True(result.TimedOut);
        Assert.Single(seen);
    }

    private sealed class HangingNative(List<NativeLaneTurn> seen) : INativeLaneRunner
    {
        public bool Supports(CodingLane lane) => true;
        public async IAsyncEnumerable<TurnEvent> RunAsync(NativeLaneTurn turn, [EnumeratorCancellation] CancellationToken ct = default)
        {
            seen.Add(turn);
            await Task.Delay(Timeout.Infinite, ct);
            yield break;
        }
    }
}
