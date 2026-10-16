using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SwarmCode.Hosting.Extensions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding;
using SwarmCore.Coding.Extensions;
using SwarmCore.Coding.Permissions;
using SwarmCore.Coding.Routing;

namespace SwarmCode.Hosting.Tests;

public class ExtensionsHostTests : TempDirTest
{
    private static readonly CodingLane Lane = new("claude", "ClaudeCode", "claude-code:default");
    private static bool Win => OperatingSystem.IsWindows();
    private static string Touch(string path) => Win ? $"type nul > \"{path}\"" : $"touch '{path}'";
    private static string Block(string reason) => Win ? $"echo {reason} 1>&2 & exit /b 2" : $"echo '{reason}' 1>&2; exit 2";

    private string Repo
    {
        get
        {
            var p = Path.Combine(Tmp, "repo");
            Directory.CreateDirectory(p);
            return p;
        }
    }

    private sealed class FakeOrchestrator : ICodingTurnOrchestrator
    {
        public CodingTurnRequest? LastRequest { get; private set; }
        public IReadOnlyList<CodingLane> Lanes { get; } = [Lane];
        public CodingLane? ResolveLane(string? name) => Lane;

        public async IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            LastRequest = request;
            await Task.Yield();
            yield return new CodingLaneSelected("t", Lane, 1);
            yield return new TurnCompleted("t", "done", TimeSpan.FromSeconds(1));
        }
    }

    private (ServiceProvider Provider, FakeOrchestrator Orchestrator, ConversationRunner Runner) Build(Action<CodingHooksOptions> hooks)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConversationStore>(new FileConversationStore(Paths));
        services.AddSingleton(new FileConversationJobStore(Paths));
        services.AddSingleton<IPermissionBroker>(new UiPermissionBroker());
        var orchestrator = new FakeOrchestrator();
        services.AddSingleton<ICodingTurnOrchestrator>(orchestrator);
        var repo = Repo;
        services.Configure<CodingOptions>(o => { o.RepositoryRoot = repo; hooks(o.Hooks); });
        services.AddCodingHooks();
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        return (provider, orchestrator, new ConversationRunner(provider, "conv"));
    }

    private static Task<TurnResult> NextFinish(ConversationRunner runner)
    {
        var tcs = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.TurnFinished += r => tcs.TrySetResult(r);
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task UserPromptSubmitHook_Block_KeepsTheMessageFromTheLane()
    {
        var (provider, orchestrator, runner) = Build(h => h.UserPromptSubmit.Add(new CodingHookDefinition { Command = Block("no-friday-deploys") }));
        using var _ = provider;
        await using var __ = runner;
        var finished = NextFinish(runner);
        Assert.True(runner.TrySend("deploy now", "claude"));
        var result = await finished;
        Assert.Null(orchestrator.LastRequest);
        Assert.Contains("no-friday-deploys", result.Error);
        Assert.DoesNotContain(runner.Entries, e => e is UserEntry);
        Assert.Contains(runner.Entries, e => e is NoticeEntry { Kind: NoticeKind.Error } n && n.Text.Contains("UserPromptSubmit hook blocked"));
    }

    [Fact]
    public async Task UserPromptSubmitHook_Context_GoesToTheLane_TranscriptShowsTypedText_AndStopHookRuns()
    {
        var marker = Path.Combine(Tmp, "stopped.txt");
        var (provider, orchestrator, runner) = Build(h =>
        {
            h.UserPromptSubmit.Add(new CodingHookDefinition { Command = Win ? "echo ticket ABC-1 is open" : "echo 'ticket ABC-1 is open'" });
            h.Stop.Add(new CodingHookDefinition { Command = Touch(marker) });
        });
        using var _ = provider;
        await using var __ = runner;
        var finished = NextFinish(runner);
        Assert.True(runner.TrySend("fix the bug", "claude"));
        var result = await finished;
        Assert.True(result.Completed);
        Assert.StartsWith("fix the bug\n\n[Context added by a UserPromptSubmit hook]\nticket ABC-1 is open", orchestrator.LastRequest!.UserMessage);
        Assert.Equal("fix the bug", Assert.IsType<UserEntry>(runner.Entries[0]).Text);
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public async Task ApprovalStore_IsKeyedByRepositoryAndExactHash_AndKeepsConcurrentApprovals()
    {
        var store = new FileCodingHookApprovalStore(Paths);
        var other = new FileCodingHookApprovalStore(Paths);
        var sha = new string('a', 64);
        Assert.False(await store.IsApprovedAsync(Repo, sha));
        await store.ApproveAsync(Repo, sha);
        Assert.True(await other.IsApprovedAsync(Repo + Path.DirectorySeparatorChar, sha));
        Assert.False(await store.IsApprovedAsync(Repo, new string('b', 64)));
        await store.ApproveAsync(Repo, new string('b', 64));
        Assert.False(await store.IsApprovedAsync(Repo, sha)); // approving new content replaces the old approval

        var roots = Enumerable.Range(0, 12).Select(i => Path.Combine(Tmp, "r" + i)).ToList();
        await Task.WhenAll(roots.Select((r, i) => Task.Run(() => (i % 2 == 0 ? store : other).ApproveAsync(r, sha))));
        foreach (var r in roots) Assert.True(await store.IsApprovedAsync(r, sha), r);
        Assert.True(await store.IsApprovedAsync(Repo, new string('b', 64)));

        await other.RevokeAsync(Repo);
        Assert.False(await store.IsApprovedAsync(Repo, new string('b', 64)));
        Assert.Throws<ArgumentException>(() => { store.ApproveAsync(Repo, "short").GetAwaiter().GetResult(); });
    }

    [Fact]
    public async Task SwarmCodeHost_UnapprovedRepositoryHooksNeverRun_ApprovalEnablesThem_ChangeNeedsReapproval()
    {
        var marker = Path.Combine(Tmp, "hook-ran.txt");
        var repo = Repo;
        void WriteHooks(string suffix)
        {
            Directory.CreateDirectory(Path.Combine(repo, ".swarmcode"));
            var command = Touch(marker) + (Win ? " & exit /b 2" : "; exit 2") + suffix;
            File.WriteAllText(Path.Combine(repo, ".swarmcode", "hooks.json"),
                new JsonObject { ["PreToolUse"] = new JsonArray(new JsonObject { ["matcher"] = "shell.*", ["command"] = command }) }.ToJsonString());
        }
        WriteHooks("");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSwarmCodeHost(new SwarmCodeHostOptions { RepositoryRoot = repo, DataPaths = Paths });
        using var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var guard = sp.GetRequiredService<IToolInvocationGuard>();
        var source = sp.GetRequiredService<ICodingHookSource>();
        var approvals = sp.GetRequiredService<ICodingHookApprovalStore>();
        var request = new ToolInvocationRequest("shell.exec", ToolSideEffect.ExternalSend, new { command = "git push" }, "conv");

        Assert.True((await guard.InspectAsync(request)).Allowed);
        Assert.False(File.Exists(marker));

        var inspection = (await source.InspectRepositoryAsync(repo))!;
        Assert.True(inspection.NeedsApproval);
        Assert.Contains("This repository wants to run hooks", SwarmCodeExtensions.DescribeApprovalRequest(inspection));
        await approvals.ApproveAsync(repo, inspection.Sha256!);
        Assert.False((await guard.InspectAsync(request)).Allowed);
        Assert.True(File.Exists(marker));
        File.Delete(marker);

        WriteHooks(" ");
        Assert.True((await guard.InspectAsync(request)).Allowed);
        Assert.False(File.Exists(marker));
        Assert.True(File.Exists(Path.Combine(Paths.RootDirectory, FileCodingHookApprovalStore.FileName)));
    }

    [Fact]
    public async Task Inventory_ListsCommandsSkillsAndHooks_WithoutRunningAnything()
    {
        var repo = Repo;
        Directory.CreateDirectory(Path.Combine(repo, ".swarmcode", "commands"));
        File.WriteAllText(Path.Combine(repo, ".swarmcode", "commands", "review.md"), "---\ndescription: Review\n---\nReview $ARGUMENTS");
        File.WriteAllText(Path.Combine(repo, ".swarmcode", "commands", "clear.md"), "shadow");
        Directory.CreateDirectory(Path.Combine(Paths.RootDirectory, "skills", "notes"));
        File.WriteAllText(Path.Combine(Paths.RootDirectory, "skills", "notes", "SKILL.md"), "---\nname: notes\ndescription: Take notes\n---\nbody");
        var marker = Path.Combine(Tmp, "never.txt");
        File.WriteAllText(Path.Combine(repo, ".swarmcode", "hooks.json"),
            new JsonObject { ["Stop"] = new JsonArray(new JsonObject { ["command"] = Touch(marker) }) }.ToJsonString());
        var user = new CodingHooksOptions();
        user.PreToolUse.Add(new CodingHookDefinition { Matcher = "shell.*", Command = "audit" });

        var inventory = await SwarmCodeExtensions.InventoryAsync(repo, Paths, user, new FileCodingHookApprovalStore(Paths), ["clear"]);

        Assert.Equal(["review"], inventory.Commands.Select(c => c.Name));
        Assert.Equal(["notes"], inventory.Skills.Select(s => s.Name));
        Assert.Equal("audit", Assert.Single(inventory.UserHooks).Command);
        Assert.True(inventory.RepositoryHooks!.NeedsApproval);
        Assert.False(File.Exists(marker));
    }

    // ---- lifecycle hooks: SessionStart, SessionEnd, Notification ----

    [Fact]
    public async Task SessionStartHook_ContextReachesTheLaneMessageOnce_AndSessionEndFiresOnDispose()
    {
        var ended = Path.Combine(Tmp, "ended.json");
        var (provider, orchestrator, runner) = Build(h =>
        {
            h.SessionStart.Add(new CodingHookDefinition { Command = Win ? "echo repo uses pnpm" : "echo 'repo uses pnpm'" });
            h.SessionEnd.Add(new CodingHookDefinition { Command = Win ? $"findstr \"^\" > \"{ended}\"" : $"cat > '{ended}'" });
        });
        using var _ = provider;

        await runner.RunSessionStartHooksAsync("resume");
        Assert.DoesNotContain(runner.Entries, e => e is NoticeEntry);

        var finished = NextFinish(runner);
        Assert.True(runner.TrySend("add a script", "claude"));
        Assert.True((await finished).Completed);
        Assert.StartsWith("[Context added by a SessionStart hook]\nrepo uses pnpm\n\nadd a script", orchestrator.LastRequest!.UserMessage);
        Assert.Equal("add a script", Assert.IsType<UserEntry>(runner.Entries.OfType<UserEntry>().First()).Text);

        finished = NextFinish(runner);
        Assert.True(runner.TrySend("and a test", "claude"));
        Assert.True((await finished).Completed);
        Assert.Equal("and a test", orchestrator.LastRequest!.UserMessage); // consumed once

        await runner.DisposeAsync();
        Assert.True(File.Exists(ended), "SessionEnd did not run on dispose");
        var payload = JsonNode.Parse(File.ReadAllText(ended).Trim())!.AsObject();
        Assert.Equal("SessionEnd", (string?)payload["event"]);
        Assert.Equal("closed", (string?)payload["reason"]);
        Assert.Equal("conv", (string?)payload["conversationId"]);
    }

    [Fact]
    public async Task NotificationHook_FiresWhenAPermissionRequestIsRaisedToThePerson()
    {
        var notified = Path.Combine(Tmp, "notified.json");
        var (provider, _, runner) = Build(h => h.Notification.Add(new CodingHookDefinition { Command = Win ? $"findstr \"^\" > \"{notified}\"" : $"cat > '{notified}'" }));
        using var _ = provider;
        await using var __ = runner;
        var broker = (UiPermissionBroker)provider.GetRequiredService<IPermissionBroker>();

        var consent = broker.RequestAsync(new SwarmCore.Skills.Models.PermissionConsentRequest
        {
            SkillId = "tool.shell.exec",
            SkillName = "Shell command",
            FunctionName = "echo hi",
            SessionId = "conv",
            MissingPermissions = [new SwarmCore.Skills.Models.SkillPermission { PermissionId = "tool.shell.exec", Name = "shell" }],
        });
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!File.Exists(notified) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(File.Exists(notified), "Notification hook did not run");
        var request = Assert.Single(runner.Entries.OfType<PermissionEntry>());
        broker.Respond(request.RequestId, approved: false, SwarmCore.Skills.Models.PermissionGrantScope.Once);
        await consent;

        JsonObject? payload = null;
        while (payload is null && DateTime.UtcNow < deadline)
        {
            try { payload = JsonNode.Parse(File.ReadAllText(notified).Trim()) as JsonObject; } catch (Exception) { await Task.Delay(50); }
        }
        Assert.NotNull(payload);
        Assert.Equal("Notification", (string?)payload["event"]);
        Assert.Equal("permission", (string?)payload["kind"]);
        Assert.Contains("Shell command / echo hi", (string?)payload["message"]);
    }

    // ---- repository permission rules through a real turn ----

    private static readonly CodingLane ApiLane = new("claude-tools", "ClaudeCode", "claude-code:default") { Priority = 90 };

    private sealed class GuardProbingEngine(Func<IServiceProvider> services, List<ToolGuardDecision> decisions) : IAgentTurnRunner
    {
        public async IAsyncEnumerable<TurnEvent> RunAsync(AgentTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var guard = services().GetRequiredService<IToolInvocationGuard>();
            foreach (var (tool, effect) in new[] { ("shell.exec", ToolSideEffect.Exec), ("git.commit", ToolSideEffect.WritesLocal), ("git.push", ToolSideEffect.ExternalSend) })
                decisions.Add(await guard.InspectAsync(new ToolInvocationRequest(tool, effect, new object(), request.ConversationId), ct));
            await Task.Yield();
            yield return new TurnCompleted("t", "done", TimeSpan.Zero);
        }
    }

    private ServiceProvider PolicyServices(string repo, CodingPermissionRules userRules, List<ToolGuardDecision> decisions)
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
        services.AddScoped<IAgentTurnRunner>(_ => new GuardProbingEngine(() => built!, decisions));
        services.AddSingleton<ICodingLaneSource>(new OptionsCodingLaneSource(Microsoft.Extensions.Options.Options.Create(new CodingLaneOptions { Lanes = [ApiLane] })));
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new CodingOptions { RepositoryRoot = repo, Permissions = userRules }));
        services.AddSingleton<ICodingTurnOrchestrator>(sp => ActivatorUtilities.CreateInstance<CodingTurnOrchestrator>(sp));
        services.AddSwarmCodeExtensionStores(Paths); // the durable repo-settings approval store
        built = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        return built;
    }

    private static async Task<TurnResult> SendAsync(ConversationRunner runner, CodingPermissionMode mode)
    {
        var finished = NextFinish(runner);
        Assert.True(runner.TrySend("change it", "claude-tools", mode));
        var result = await finished;
        Assert.True(result.Completed, (result.Error ?? "not completed") + " | " + string.Join(" / ", runner.Entries.OfType<NoticeEntry>().Select(n => n.Text + " " + n.Details)));
        return result;
    }

    [Fact]
    public async Task RepositoryDenyRuleBlocksATool_AndRepositoryAllowRulesApplyOnlyAfterApprovalThroughTheFileStore()
    {
        var repo = Repo;
        Directory.CreateDirectory(Path.Combine(repo, ".claude"));
        File.WriteAllText(Path.Combine(repo, ".claude", "settings.json"), """{ "permissions": { "allow": ["git.commit"], "deny": ["shell.*"] } }""");
        var decisions = new List<ToolGuardDecision>();
        using var provider = PolicyServices(repo, new CodingPermissionRules { Allow = ["git.push"] }, decisions);
        await using var runner = new ConversationRunner(provider, "conv");

        // Unapproved: the repository deny refuses shell.exec, the repository allow does nothing (git.commit asks under AskRisky), the person's allow runs.
        var result = await SendAsync(runner, CodingPermissionMode.AskRisky);
        Assert.True(result.Completed);
        Assert.Collection(decisions,
            shell => Assert.False(shell.Allowed),
            commit => Assert.True(commit.Allowed && commit.RequiresApproval),
            push => Assert.True(push.Allowed && !push.RequiresApproval));
        Assert.Contains("shell.*", decisions[0].BlockReason);

        // Approve the current content through the durable store, as the console /permissions or the desktop prompt does.
        var inspection = await SwarmCodePermissions.InspectRepositoryAsync(provider, repo);
        Assert.True(inspection.NeedsApproval);
        Assert.Contains("git.commit", SwarmCodePermissions.DescribeApprovalRequest(inspection));
        await provider.GetRequiredService<ICodingRepositorySettingsApprovalStore>().ApproveAsync(repo, inspection.Sha256!);
        Assert.True(File.Exists(Path.Combine(Paths.RootDirectory, FileCodingRepositorySettingsApprovalStore.FileName)));
        Assert.True((await SwarmCodePermissions.InspectRepositoryAsync(provider, repo)).Approved);

        decisions.Clear();
        result = await SendAsync(runner, CodingPermissionMode.AskRisky);
        Assert.True(result.Completed);
        Assert.Collection(decisions,
            shell => Assert.False(shell.Allowed),                           // deny still wins
            commit => Assert.True(commit.Allowed && !commit.RequiresApproval), // approved repository allow
            push => Assert.True(push.Allowed && !push.RequiresApproval));

        // Any change to the file needs a fresh approval; deny rules keep applying meanwhile.
        File.WriteAllText(Path.Combine(repo, ".claude", "settings.json"), """{ "permissions": { "allow": ["git.commit", "browser.*"], "deny": ["shell.*"] } }""");
        decisions.Clear();
        await SendAsync(runner, CodingPermissionMode.AskRisky);
        Assert.False(decisions[0].Allowed);
        Assert.True(decisions[1].RequiresApproval);
        Assert.True((await SwarmCodePermissions.InspectRepositoryAsync(provider, repo)).NeedsApproval);
    }
}
