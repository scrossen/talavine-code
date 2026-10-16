using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmCode.Hosting.Agents;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding;
using SwarmCore.Coding.Agents;
using SwarmCore.Coding.Permissions;
using SwarmCore.Coding.Routing;
using SwarmCore.Testing;

namespace SwarmCode.Hosting.Tests;

/// <summary>Isolated agent worktrees in the SwarmCode host, and graded permission modes (policy, migration, inheritance, lanes).</summary>
public sealed class IsolationAndPermissionModeTests : TempDirTest
{
    private static readonly CodingLane ApiLane = new("claude-tools", "ClaudeCode", "claude-code:default") { Priority = 90 };
    private static readonly CodingLane NativeLane = new("claude", "ClaudeCodeNative", "claude-code:default") { Priority = 0 };

    public override void Dispose()
    {
        try { foreach (var f in Directory.EnumerateFiles(Tmp, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); } catch { }
        base.Dispose();
    }

    private string GitRepo()
    {
        var repo = Path.Combine(Tmp, "repo");
        Directory.CreateDirectory(repo);
        foreach (var args in new[] { new[] { "init", "-q" }, ["config", "user.name", "Test"], ["config", "user.email", "t@example.invalid"], ["config", "commit.gpgsign", "false"] })
            Assert.Equal(0, TestGit.Run(repo, args).ExitCode);
        File.WriteAllText(Path.Combine(repo, "a.txt"), "base");
        Assert.Equal(0, TestGit.Run(repo, "add", ".").ExitCode);
        Assert.Equal(0, TestGit.Run(repo, "commit", "-qm", "base").ExitCode);
        return repo;
    }

    private sealed class CompletingOrchestrator : ICodingTurnOrchestrator
    {
        public List<CodingTurnRequest> Requests { get; } = [];
        public IReadOnlyList<CodingLane> Lanes { get; } = [ApiLane];
        public CodingLane? ResolveLane(string? name) => ApiLane;
        public async IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            lock (Requests) Requests.Add(request);
            await Task.Yield();
            yield return new TurnCompleted("t", "done", TimeSpan.Zero);
        }
    }

    [GitFact]
    public async Task Isolated_agent_session_works_in_its_own_worktree_and_inherits_the_mode()
    {
        var repo = GitRepo();
        var orchestrator = new CompletingOrchestrator();
        await using var workspace = await WorkspaceHost.CreateAsync(new SwarmCodeHostOptions
        {
            RepositoryRoot = repo,
            DataPaths = Paths,
            ConfigureServices = services => services.AddSingleton<ICodingTurnOrchestrator>(orchestrator),
        });
        var sp = workspace.Services;
        var team = sp.GetRequiredService<AgentTeam>();
        var sessions = sp.GetRequiredService<WorkspaceSessionCatalog>();
        sp.GetRequiredService<AppPreferencesStore>().Update(p => p.SetMode(CodingPermissionMode.AskRisky));
        var parent = await sessions.CreateAsync(repo, "Parent", "claude-tools");

        var agent = await team.SpawnAsync(parent.Id, new AgentSpawnRequest("Edit a.txt", "Editor", Isolation: "worktree"));

        var wt = Assert.IsType<AgentWorktree>(agent.Worktree);
        Assert.StartsWith(Paths.WorktreesDirectory, wt.Path, StringComparison.OrdinalIgnoreCase);
        var session = (await sessions.GetAsync(agent.Id))!;
        Assert.Equal(Path.GetFullPath(repo).TrimEnd('\\'), session.RepositoryRoot, ignoreCase: true); // listed under the parent's repository
        Assert.Equal(wt.WorkingRoot, session.WorkingRoot, ignoreCase: true);
        Assert.Equal("ask-risky", session.PermissionMode);

        // The workspace resolver gives the agent's turns its worktree, and every other conversation the repository.
        var resolver = sp.GetRequiredService<ICodingWorkspaceResolver>();
        Assert.Equal(wt.WorkingRoot, (await resolver.ResolveAsync(agent.Id))!.FullRoot, ignoreCase: true);
        Assert.Equal(Path.GetFullPath(repo), (await resolver.ResolveAsync(parent.Id))!.FullRoot, ignoreCase: true);

        // The agent's turn ran with the inherited mode even after the person switched back to Auto.
        sp.GetRequiredService<AppPreferencesStore>().Update(p => p.SetMode(CodingPermissionMode.Auto));
        await Until(() => { lock (orchestrator.Requests) return orchestrator.Requests.Any(r => r.ConversationId == agent.Id); });
        CodingTurnRequest first;
        lock (orchestrator.Requests) first = orchestrator.Requests.First(r => r.ConversationId == agent.Id);
        Assert.Contains(wt.Branch, first.UserMessage);
        await Until(() => !team.IsActive(agent.Id));
        Assert.True(await sp.GetRequiredService<SwarmCodeAgentHost>().StartTurnAsync(agent.Id, "follow-up", null, null, default));
        await Until(() => { lock (orchestrator.Requests) return orchestrator.Requests.Count(r => r.ConversationId == agent.Id) >= 2; });
        lock (orchestrator.Requests)
            Assert.All(orchestrator.Requests.Where(r => r.ConversationId == agent.Id), r => Assert.Equal(CodingPermissionMode.AskRisky, r.Permissions!.Mode));

        // Deleting the agent keeps nothing that was not saved: the untouched worktree goes.
        await Until(() => !team.IsActive(agent.Id));
        var forgotten = await team.ForgetWithCleanupAsync(agent.Id, force: true);
        Assert.True(forgotten.Worktree!.Removed, forgotten.Worktree.Message);
        Assert.False(Directory.Exists(wt.Path));
    }

    [GitFact]
    public async Task Isolation_in_a_non_git_workspace_fails_with_a_clear_message()
    {
        var repo = Path.Combine(Tmp, "plain");
        Directory.CreateDirectory(repo);
        var orchestrator = new CompletingOrchestrator();
        await using var workspace = await WorkspaceHost.CreateAsync(new SwarmCodeHostOptions
        {
            RepositoryRoot = repo,
            DataPaths = Paths,
            ConfigureServices = services => services.AddSingleton<ICodingTurnOrchestrator>(orchestrator),
        });
        var sp = workspace.Services;
        var parent = await sp.GetRequiredService<WorkspaceSessionCatalog>().CreateAsync(repo, "Parent");
        var spawn = AgentTeamTools.Create(sp.GetRequiredService<AgentTeam>(), () => parent.Id).Single(t => t.ToolId == AgentTeamTools.SpawnToolId);

        var result = (AgentsResult)(await spawn.Invoke(new AgentsSpawnInput("Edit", Isolation: "worktree"), default))!;

        Assert.False(result.Success);
        Assert.Contains("needs a git repository", result.Message);
        Assert.Empty(sp.GetRequiredService<AgentTeam>().Snapshot());
        Assert.DoesNotContain(await sp.GetRequiredService<WorkspaceSessionCatalog>().ListAsync(), s => s.ParentSessionId == parent.Id);
    }

    // ---- the real orchestrator: run roots and native-lane admission ----

    private sealed class RecordingEngine(List<AgentTurnRequest> seen, Func<IServiceProvider>? services = null, List<ToolGuardDecision>? decisions = null) : IAgentTurnRunner
    {
        public async IAsyncEnumerable<TurnEvent> RunAsync(AgentTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            lock (seen) seen.Add(request);
            if (services is not null && decisions is not null)
            {
                // What the engine's guard seam answers while this turn runs.
                var guard = services().GetRequiredService<IToolInvocationGuard>();
                foreach (var (tool, effect) in new[] { ("shell.exec", ToolSideEffect.Exec), ("code.edit_file", ToolSideEffect.WritesLocal), ("git.push", ToolSideEffect.ExternalSend) })
                    decisions.Add(await guard.InspectAsync(new ToolInvocationRequest(tool, effect, new object(), request.ConversationId), ct));
            }
            await Task.Yield();
            yield return new TurnCompleted("t", "done on the api lane", TimeSpan.Zero);
        }
    }

    private sealed class RecordingNative(List<NativeLaneTurn> seen) : INativeLaneRunner
    {
        public bool Supports(CodingLane lane) => lane.IsNative;
        public async IAsyncEnumerable<TurnEvent> RunAsync(NativeLaneTurn turn, [EnumeratorCancellation] CancellationToken ct = default)
        {
            lock (seen) seen.Add(turn);
            await Task.Yield();
            yield return new TurnCompleted("t", "done natively", TimeSpan.Zero);
        }
    }

    private ServiceProvider Services(List<AgentTurnRequest> engine, List<NativeLaneTurn> native, CodingPermissionRules? rules = null, List<ToolGuardDecision>? decisions = null)
    {
        var services = new ServiceCollection();
        ServiceProvider? built = null;
        services.AddLogging();
        services.AddSingleton(Paths);
        services.AddSingleton<IConversationStore>(new FileConversationStore(Paths));
        services.AddSingleton(new FileConversationJobStore(Paths));
        services.AddSingleton<IPermissionBroker>(new UiPermissionBroker());
        services.AddSingleton<WorkspaceSessionCatalog>();
        services.AddSingleton<ICodingWorkspaceResolver, SessionCodingWorkspaceResolver>();
        services.AddSingleton<ActiveCodingPermissions>();
        services.AddSingleton<CodingPermissionGuard>();
        services.AddSingleton<IToolInvocationGuard>(sp => new CompositeToolInvocationGuard(sp.GetRequiredService<CodingPermissionGuard>()));
        services.AddScoped<IAgentTurnRunner>(_ => new RecordingEngine(engine, () => built!, decisions));
        services.AddScoped<INativeLaneRunner>(_ => new RecordingNative(native));
        services.AddSingleton<ICodingLaneSource>(new OptionsCodingLaneSource(Options.Create(new CodingLaneOptions { Lanes = [NativeLane, ApiLane] })));
        var coding = new CodingOptions { RepositoryRoot = Path.Combine(Tmp, "repo"), Permissions = rules ?? new() };
        services.AddSingleton(Options.Create(coding));
        services.AddSingleton<ICodingTurnOrchestrator>(sp => ActivatorUtilities.CreateInstance<CodingTurnOrchestrator>(sp));
        built = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        return built;
    }

    private static async Task<TurnResult> Send(ConversationRunner runner, string lane, CodingPermissionMode mode)
    {
        var finished = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.TurnFinished += r => finished.TrySetResult(r);
        Assert.True(runner.TrySend("change it", lane, mode));
        return await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Engine_and_native_lanes_run_in_the_sessions_working_root()
    {
        var repo = Path.Combine(Tmp, "repo");
        var worktree = Path.Combine(Tmp, "data", "worktrees", "repo-x", "agent");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(worktree);
        var engine = new List<AgentTurnRequest>();
        var native = new List<NativeLaneTurn>();
        using var provider = Services(engine, native);
        var sessions = provider.GetRequiredService<WorkspaceSessionCatalog>();
        var parent = await sessions.CreateAsync(repo, "Parent");
        var agent = await sessions.CreateAsync(repo, "Agent", parentSessionId: parent.Id, workingRoot: worktree);

        await using (var runner = new ConversationRunner(provider, agent.Id))
        {
            await Send(runner, "claude", CodingPermissionMode.Auto);
            await Send(runner, "claude-tools", CodingPermissionMode.Auto);
        }
        Assert.Equal(Path.GetFullPath(worktree), Assert.Single(native).RepositoryRoot, ignoreCase: true);
        Assert.Equal(Path.GetFullPath(worktree), Assert.Single(engine).WorkspaceRoot, ignoreCase: true);

        // The parent conversation still works in the repository, not in its agent's worktree.
        await using (var runner = new ConversationRunner(provider, parent.Id))
            await Send(runner, "claude-tools", CodingPermissionMode.Auto);
        Assert.Equal(Path.GetFullPath(repo), engine[1].WorkspaceRoot, ignoreCase: true);
    }

    [Fact]
    public async Task Ask_risky_escalates_risky_tools_through_the_guard_while_the_turn_runs_and_drops_native_lanes()
    {
        Directory.CreateDirectory(Path.Combine(Tmp, "repo"));
        var engine = new List<AgentTurnRequest>();
        var native = new List<NativeLaneTurn>();
        var decisions = new List<ToolGuardDecision>();
        using var provider = Services(engine, native, new CodingPermissionRules { Deny = ["git.push"] }, decisions);
        await using var runner = new ConversationRunner(provider, "conv");

        var result = await Send(runner, "claude", CodingPermissionMode.AskRisky);

        Assert.Empty(native);
        Assert.Equal(["claude-tools"], result.LanesUsed.ToArray());
        Assert.Contains(runner.Entries.OfType<NoticeEntry>(), n => n.Text.StartsWith("Skipping claude:", StringComparison.Ordinal) && n.Text.Contains("approval prompts"));
        Assert.True(Assert.Single(engine).CommunicationScope.UserInitiated);
        Assert.Collection(decisions,
            shell => Assert.True(shell.Allowed && shell.RequiresApproval),   // asks
            edit => Assert.True(edit.Allowed && !edit.RequiresApproval),     // runs
            push => Assert.False(push.Allowed));                              // denied, whatever the mode
        // After the turn the policy no longer applies to the conversation.
        Assert.True((await provider.GetRequiredService<IToolInvocationGuard>().InspectAsync(new ToolInvocationRequest("shell.exec", ToolSideEffect.Exec, new object(), "conv"))).Allowed);
    }

    [Fact]
    public async Task Auto_mode_keeps_native_lanes_unless_deny_rules_exist()
    {
        Directory.CreateDirectory(Path.Combine(Tmp, "repo"));
        var engine = new List<AgentTurnRequest>();
        var native = new List<NativeLaneTurn>();
        using (var provider = Services(engine, native))
        await using (var runner = new ConversationRunner(provider, "conv"))
            Assert.Equal(["claude"], (await Send(runner, "claude", CodingPermissionMode.Auto)).LanesUsed.ToArray());
        Assert.Single(native);

        using (var provider = Services(engine, native, new CodingPermissionRules { Deny = ["shell.exec"] }))
        await using (var runner = new ConversationRunner(provider, "conv2"))
        {
            Assert.Equal(["claude-tools"], (await Send(runner, "claude", CodingPermissionMode.Auto)).LanesUsed.ToArray());
            Assert.Contains(runner.Entries.OfType<NoticeEntry>(), n => n.Text.Contains("deny rules"));
        }
        Assert.Single(native);
    }

    // ---- preferences ----

    [Theory]
    [InlineData("{\"requireApproval\": true}", CodingPermissionMode.Ask)]
    [InlineData("{\"requireApproval\": false}", CodingPermissionMode.Auto)]
    [InlineData("{}", CodingPermissionMode.Auto)]
    [InlineData("{\"requireApproval\": false, \"permissionMode\": \"ask-risky\"}", CodingPermissionMode.AskRisky)]
    [InlineData("{\"requireApproval\": true, \"permissionMode\": \"auto\"}", CodingPermissionMode.Auto)]
    public void Preferences_migrate_the_old_approval_switch(string json, CodingPermissionMode expected)
    {
        File.WriteAllText(Paths.PreferencesPath, json);
        Assert.Equal(expected, new AppPreferencesStore(Paths).Current.Mode);
    }

    [Fact]
    public void Setting_a_mode_keeps_the_legacy_switch_in_step_and_round_trips()
    {
        var store = new AppPreferencesStore(Paths);
        store.Update(p => p.SetMode(CodingPermissionMode.AskRisky));
        var fresh = new AppPreferencesStore(Paths).Current;
        Assert.Equal(CodingPermissionMode.AskRisky, fresh.Mode);
        Assert.Equal("ask-risky", fresh.PermissionMode);
        Assert.True(fresh.RequireApproval); // an older build still asks
        store.Update(p => p.SetMode(CodingPermissionMode.Auto));
        Assert.False(new AppPreferencesStore(Paths).Current.RequireApproval);
    }

    [Fact]
    public void Queued_turns_saved_before_modes_keep_asking()
    {
        var legacy = new ConversationRunner.QueuedTurn("x", null, RequireApproval: true, AllowHandOff: null);
        Assert.Equal(CodingPermissionMode.Ask, legacy.Mode);
        Assert.Equal(CodingPermissionMode.AskRisky, (legacy with { PermissionMode = "ask-risky" }).Mode);
    }

    [Fact]
    public void Agent_sessions_never_run_less_cautiously_than_they_were_started()
    {
        var info = new WorkspaceSessionInfo("a", "t", Tmp, DateTime.UtcNow, DateTime.UtcNow) { PermissionMode = "ask" };
        Assert.Equal(CodingPermissionMode.Ask, SwarmCodePermissions.ForSession(CodingPermissionMode.Auto, info));
        Assert.Equal(CodingPermissionMode.AskRisky, SwarmCodePermissions.ForSession(CodingPermissionMode.AskRisky, info with { PermissionMode = null }));
        Assert.Equal(CodingPermissionMode.Ask, SwarmCodePermissions.ForSession(CodingPermissionMode.Ask, info with { PermissionMode = "auto" }));
    }

    [Fact]
    public void Settings_rules_are_read_from_coding_permissions()
    {
        File.WriteAllText(Paths.UserConfigPath, "{\"Coding\":{\"Permissions\":{\"Allow\":[\"git.commit\"],\"Deny\":[\"shell.*\"]}}}");
        var rules = new UserSettingsFile(Paths).ReadPermissionRules();
        Assert.Equal(["git.commit"], rules.Allow);
        Assert.Equal(["shell.*"], rules.Deny);
        var bound = SwarmCodeHostComposer.BuildConfiguration(Paths);
        var coding = new CodingOptions();
        Microsoft.Extensions.Configuration.ConfigurationBinder.Bind(bound.GetSection(CodingOptions.SectionName), coding);
        Assert.Equal(["shell.*"], coding.Permissions.Deny);
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met.");
            await Task.Delay(20);
        }
    }
}
