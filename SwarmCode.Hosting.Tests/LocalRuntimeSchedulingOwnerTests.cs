using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SwarmCode.Hosting.Agents;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Abstractions.LlmRouting.Decisions;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Agents;
using SwarmCore.Coding.Routing;

namespace SwarmCode.Hosting.Tests;

public sealed class LocalRuntimeSchedulingOwnerTests : TempDirTest
{
    [Fact]
    public async Task Complete_agent_tree_shares_root_but_independent_roots_and_forks_remain_separate_without_writing()
    {
        var catalog = new WorkspaceSessionCatalog(Paths);
        var root = await catalog.CreateAsync(Tmp);
        var child = await catalog.CreateAsync(Tmp, parentSessionId: root.Id);
        var grandchild = await catalog.CreateAsync(Tmp, parentSessionId: child.Id);
        var other = await catalog.CreateAsync(Tmp);
        var fork = await catalog.PublishForkAsync(root.Id, (_, _) => Task.CompletedTask, default);
        var forkChild = await catalog.CreateAsync(Tmp, parentSessionId: fork.Id);
        var before = await File.ReadAllBytesAsync(Paths.SessionIndexPath);
        Assert.Equal(root.Id, await catalog.GetSchedulingOwnerIdAsync(root.Id));
        Assert.Equal(root.Id, await catalog.GetSchedulingOwnerIdAsync(child.Id));
        Assert.Equal(root.Id, await catalog.GetSchedulingOwnerIdAsync(grandchild.Id));
        Assert.Equal(other.Id, await catalog.GetSchedulingOwnerIdAsync(other.Id));
        Assert.Equal(root.Id, fork.ForkedFromSessionId);
        Assert.Null(fork.ParentSessionId);
        Assert.Equal(fork.Id, await catalog.GetSchedulingOwnerIdAsync(fork.Id));
        Assert.Equal(fork.Id, await catalog.GetSchedulingOwnerIdAsync(forkChild.Id));
        Assert.Equal(before, await File.ReadAllBytesAsync(Paths.SessionIndexPath));
    }

    [Theory]
    [InlineData("missing-session")]
    [InlineData("missing-parent")]
    [InlineData("missing-ancestor")]
    [InlineData("self-cycle")]
    [InlineData("two-cycle")]
    [InlineData("duplicate-parent")]
    [InlineData("invalid-parent")]
    public async Task Damaged_ancestry_is_bounded_deterministic_and_isolated_under_the_requesting_session(string damage)
    {
        var now = DateTime.UtcNow;
        WorkspaceSessionInfo Row(string id, string? parent = null) => new(id, "Untrusted title", Tmp, now, now) { ParentSessionId = parent };
        var rows = damage switch
        {
            "missing-session" => new[] { Row("other") },
            "missing-parent" => [Row("child", "absent")],
            "missing-ancestor" => [Row("child", "parent"), Row("parent", "absent")],
            "self-cycle" => [Row("child", "child")],
            "two-cycle" => [Row("child", "parent"), Row("parent", "child")],
            "duplicate-parent" => [Row("child", "parent"), Row("parent"), Row("parent", "different")],
            _ => [Row("child", "../foreign")],
        };
        await File.WriteAllTextAsync(Paths.SessionIndexPath, JsonSerializer.Serialize(new { sessions = rows, recentRepositories = Array.Empty<string>() }));
        var catalog = new WorkspaceSessionCatalog(Paths);
        var before = await File.ReadAllBytesAsync(Paths.SessionIndexPath);
        Assert.Equal("child", await catalog.GetSchedulingOwnerIdAsync("child").WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("child", await catalog.GetSchedulingOwnerIdAsync("child").WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(before, await File.ReadAllBytesAsync(Paths.SessionIndexPath));
    }

    [Fact]
    public async Task Runner_preserves_actual_chat_owner_while_grouping_nested_agents_and_ignoring_origin_metadata()
    {
        var catalog = new WorkspaceSessionCatalog(Paths);
        var root = await catalog.CreateAsync(Tmp, laneName: "fixture");
        var child = await catalog.CreateAsync(Tmp, laneName: "fixture", parentSessionId: root.Id);
        var grandchild = await catalog.CreateAsync(Tmp, laneName: "fixture", parentSessionId: child.Id);
        var fork = await catalog.PublishForkAsync(root.Id, (_, _) => Task.CompletedTask, default);
        var execution = new CaptureOrchestrator();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(catalog);
        services.AddSingleton<IConversationStore>(new FileConversationStore(Paths));
        services.AddSingleton(new FileConversationJobStore(Paths));
        services.AddSingleton<ICodingTurnOrchestrator>(execution);
        await using var provider = services.BuildServiceProvider();
        using var ambient = LocalLlmRuntimeOwnerScope.Enter("outer-chat", "Outer app", "outer-root");
        foreach (var session in new[] { root, child, grandchild, fork })
        {
            using var runner = new ConversationRunner(provider, session.Id);
            var finished = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            runner.TurnFinished += result => finished.TrySetResult(result);
            Assert.True(runner.TrySend("Do the bounded fixture task", "fixture", origin: new("swarmcode-app", false,
                new Dictionary<string, string> { ["SchedulingOwnerId"] = "model-authored-spoof", ["ParentSessionId"] = fork.Id })));
            var result = await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(result.Completed, result.Error);
            var captured = execution.Observations.Last();
            Assert.Equal(session.Id, captured.Owner);
            Assert.Equal(session.Id == fork.Id ? fork.Id : root.Id, captured.SchedulingOwner);
            Assert.Equal("Talavine Code", captured.Application);
            Assert.Equal("outer-chat", LocalLlmRuntimeOwnerScope.Current);
            Assert.Equal("outer-root", LocalLlmRuntimeOwnerScope.CurrentSchedulingOwner);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delegation_assessment_uses_new_child_owner_and_trusted_root_then_restores_parent_even_on_failure(bool failDecision)
    {
        var decider = new CaptureDecisionProvider(failDecision);
        var inventory = new CaptureInventory();
        await new DecisionSettingsStore(Paths).SaveAsync(new()
        {
            Enabled = true, AutoDelegateRoutineTasks = true, Connection = new() { Provider = decider.Name },
            Candidates = [new("local", "local", "small", "Local worker"), new("lead", "lead", "large", "Lead")],
        });
        await using var workspace = await WorkspaceHost.CreateAsync(new()
        {
            RepositoryRoot = Tmp, DataPaths = Paths,
            ConfigureServices = services =>
            {
                services.AddSingleton<IChoiceDecisionProvider>(decider);
                services.AddSingleton<IDelegatedModelInventory>(inventory);
                services.AddSingleton<ICodingTurnOrchestrator>(new CaptureOrchestrator());
            },
        });
        var sessions = workspace.Services.GetRequiredService<WorkspaceSessionCatalog>();
        var root = await sessions.CreateAsync(Tmp, laneName: "lead", model: "large");
        var parent = await sessions.CreateAsync(Tmp, laneName: "lead", model: "large", parentSessionId: root.Id);
        var host = workspace.Services.GetRequiredService<SwarmCodeAgentHost>();
        using var parentScope = LocalLlmRuntimeOwnerScope.Enter(parent.Id, "Parent app", "not-the-trusted-root");
        var created = await host.CreateAgentConversationAsync(new(parent.Id, "Child", null, null, null,
            Task: "List three declared options from one file; the complete expected keys are supplied."), default);
        Assert.Equal(parent.Id, (await sessions.GetAsync(created.Id))!.ParentSessionId);
        Assert.NotEmpty(decider.Observations);
        foreach (var observed in decider.Observations.Concat(inventory.Observations))
        {
            Assert.Equal(created.Id, observed.Owner);
            Assert.Equal(root.Id, observed.SchedulingOwner);
            Assert.Equal("Talavine Code", observed.Application);
        }
        Assert.Equal(parent.Id, LocalLlmRuntimeOwnerScope.Current);
        Assert.Equal("not-the-trusted-root", LocalLlmRuntimeOwnerScope.CurrentSchedulingOwner);
        Assert.Equal("Parent app", LocalLlmRuntimeOwnerScope.CurrentApplication);
    }

    private sealed record Observation(string? Owner, string? SchedulingOwner, string? Application)
    {
        public static Observation Current => new(LocalLlmRuntimeOwnerScope.Current,
            LocalLlmRuntimeOwnerScope.CurrentSchedulingOwner, LocalLlmRuntimeOwnerScope.CurrentApplication);
    }

    private sealed class CaptureDecisionProvider(bool fail) : IChoiceDecisionProvider
    {
        public string Name => "scheduling-fixture";
        public ConcurrentQueue<Observation> Observations { get; } = new();
        public async Task<ChoiceDecision> ChooseAsync(DecisionConnection connection, ChoiceDecisionRequest request, CancellationToken ct = default)
        {
            await Task.Yield(); Observations.Enqueue(Observation.Current);
            if (fail) throw new InvalidOperationException("Synthetic unavailable decision");
            return new(request.Options.Any(o => o.Id == "routine") ? "routine" : request.Options[0].Id, .9);
        }
    }

    private sealed class CaptureInventory : IDelegatedModelInventory
    {
        public ConcurrentQueue<Observation> Observations { get; } = new();
        public async Task<IReadOnlyList<ModelDecisionCandidate>> GetEligibleAsync(string id, string task,
            IReadOnlyList<ModelDecisionCandidate> configured, CancellationToken ct)
        {
            await Task.Yield(); Observations.Enqueue(Observation.Current);
            return [new("local", "local", "small", "Fixture admitted local")];
        }
    }

    private sealed class CaptureOrchestrator : ICodingTurnOrchestrator
    {
        public ConcurrentQueue<Observation> Observations { get; } = new();
        public IReadOnlyList<CodingLane> Lanes { get; } = [new("fixture", "Fixture", "fixture-model")];
        public CodingLane? ResolveLane(string? name) => Lanes[0];
        public async IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield(); Observations.Enqueue(Observation.Current);
            yield return new TurnCompleted("fixture", "Fixture finished.", TimeSpan.Zero);
        }
    }
}
