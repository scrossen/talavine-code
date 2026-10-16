using SwarmCore.Abstractions.LlmRouting.Decisions;
using SwarmCore.Coding.Routing;
using SwarmCore.LlmRouting.Decisions;
using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Agent.Runtime;

namespace SwarmCode.Hosting.Tests;

public sealed class DecisionSelectionTests : TempDirTest
{
    private static readonly CodingLane[] Lanes = [new("fast", "test", "small"), new("strong", "test", "large@high")];
    private async Task<DecisionSessionCoordinator> CoordinatorAsync(IChoiceDecisionProvider provider, WorkspaceSessionCatalog catalog)
    {
        var store = new DecisionSettingsStore(Paths);
        await store.SaveAsync(new() { Enabled = true, Connection = new() { Provider = provider.Name },
            Candidates = [new("fast", "fast", "small", "Simple"), new("strong", "strong", "large@high", "Difficult")] });
        return new(store, catalog, new ModelDecisionSelector([provider]));
    }

    [Fact]
    public async Task FirstPromptSelects_Persists_AndDoesNotRerun()
    {
        var catalog = new WorkspaceSessionCatalog(Paths);
        var session = await catalog.CreateAsync(Tmp, laneName: "fast", model: "small");
        var provider = new Fake();
        var coordinator = await CoordinatorAsync(provider, catalog);
        var first = await coordinator.PrepareAsync(session.Id, "complex task", "fast", "small", Lanes, false, default);
        Assert.Equal("strong", first.Lane); Assert.Equal("large@high", first.Model);
        var reopened = new DecisionSessionCoordinator(new(Paths), new(Paths), new([provider]));
        var second = await reopened.PrepareAsync(session.Id, "followup", "fast", "small", Lanes, false, default);
        Assert.Equal("strong", second.Lane); Assert.Equal(1, provider.Calls);
        await reopened.MarkFailedAsync(session.Id, second.Revision, default);
        await reopened.PrepareAsync(session.Id, "failed again", "fast", "small", Lanes, false, default);
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task ExplicitSameModelSkipsDecision()
    {
        var catalog = new WorkspaceSessionCatalog(Paths);
        var session = await catalog.CreateAsync(Tmp, laneName: "fast", model: "small");
        await catalog.SetModelAsync(session.Id, "fast", "small");
        var provider = new Fake(); var coordinator = await CoordinatorAsync(provider, catalog);
        var result = await coordinator.PrepareAsync(session.Id, "hard", "fast", "small", Lanes, false, default);
        Assert.Equal("small", result.Model); Assert.Equal(0, provider.Calls);
        Assert.Null(await coordinator.ObserveAsync(session.Id, result.Revision, Lanes[1], true, default));
        var reopened = await new WorkspaceSessionCatalog(Paths).GetAsync(session.Id);
        Assert.Equal("small", reopened!.Selection!.Model);
        Assert.Equal(ModelSelectionOrigin.User, reopened.Selection.Origin);
    }

    [Fact]
    public async Task UserChoiceDuringInferenceWins_AcrossCatalogInstances()
    {
        var catalog = new WorkspaceSessionCatalog(Paths); var other = new WorkspaceSessionCatalog(Paths);
        var session = await catalog.CreateAsync(Tmp);
        var provider = new Fake { Before = () => other.SetModelAsync(session.Id, "fast", "user-model") };
        var coordinator = await CoordinatorAsync(provider, catalog);
        var result = await coordinator.PrepareAsync(session.Id, "hard", null, null, Lanes, false, default);
        Assert.Equal("user-model", result.Model); Assert.Null(result.Notice);
        Assert.True((await catalog.GetAsync(session.Id))!.Selection!.UserSelected);
    }

    [Fact]
    public async Task DeleteDuringInferenceDoesNotResurrect()
    {
        var catalog = new WorkspaceSessionCatalog(Paths); var session = await catalog.CreateAsync(Tmp);
        var provider = new Fake { Before = () => new WorkspaceSessionCatalog(Paths).DeleteAsync(session.Id) };
        var coordinator = await CoordinatorAsync(provider, catalog);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.PrepareAsync(session.Id, "hard", null, null, Lanes, false, default));
        Assert.Null(await catalog.GetAsync(session.Id));
    }

    [Fact]
    public async Task HandoffPersists_AndCannotOverwriteLaterUserChoice()
    {
        var catalog = new WorkspaceSessionCatalog(Paths); var session = await catalog.CreateAsync(Tmp);
        var coordinator = await CoordinatorAsync(new Fake(), catalog);
        var first = await coordinator.PrepareAsync(session.Id, "hard", null, null, Lanes, false, default);
        var revision = await coordinator.ObserveAsync(session.Id, first.Revision, Lanes[0], true, default);
        Assert.Equal(ModelSelectionOrigin.Handoff, (await catalog.GetAsync(session.Id))!.Selection!.Origin);
        await catalog.SetModelAsync(session.Id, "strong", "user-model");
        Assert.Null(await coordinator.ObserveAsync(session.Id, revision, Lanes[0], true, default));
        Assert.Equal("user-model", (await catalog.GetAsync(session.Id))!.Selection!.Model);
    }

    [Fact]
    public async Task FailureUsesDefault_AndDoesNotLoopOnEveryPrompt()
    {
        var catalog = new WorkspaceSessionCatalog(Paths); var session = await catalog.CreateAsync(Tmp, laneName:"fast", model:"small");
        var provider = new Fake { Before = () => throw new HttpRequestException("secret-body") };
        var coordinator = await CoordinatorAsync(provider, catalog);
        var result = await coordinator.PrepareAsync(session.Id, "hard", "fast", "small", Lanes, false, default);
        Assert.Equal("small", result.Model); Assert.DoesNotContain("secret-body", result.Notice);
        await coordinator.PrepareAsync(session.Id, "next", "fast", "small", Lanes, false, default);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task IndependentWritersPreserveTitleAndTemporaryFlag_AndSettingsRejectStaleEdit()
    {
        var a = new WorkspaceSessionCatalog(Paths); var b = new WorkspaceSessionCatalog(Paths);
        var session = await a.CreateAsync(Tmp);
        await a.GetAsync(session.Id);
        await b.RenameAsync(session.Id, "Renamed"); await b.SetTemporaryAsync(session.Id, true);
        await a.SetModelAsync(session.Id, "fast", "small");
        var fresh = await b.GetAsync(session.Id);
        Assert.Equal("Renamed", fresh!.Title); Assert.True(fresh.Temporary);
        var settings = new DecisionSettingsStore(Paths); var stale = await settings.ReadAsync();
        await new DecisionSettingsStore(Paths).SaveAsync(stale);
        await Assert.ThrowsAsync<InvalidOperationException>(() => settings.SaveAsync(stale));
    }

    private sealed class Fake : IChoiceDecisionProvider
    {
        public string Name => "fake";
        public int Calls;
        public Func<Task>? Before;
        public async Task<ChoiceDecision> ChooseAsync(DecisionConnection c, ChoiceDecisionRequest r, CancellationToken ct = default)
        { Calls++; if (Before is not null) await Before(); return new("strong", .9); }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task ActualConversationRunner_DispatchesStartingChoice_WithoutDisablingHandoff_AndPreservesSessionAfterRestart(bool userSelected, bool allowHandOff)
    {
        var catalog = new WorkspaceSessionCatalog(Paths); var session = await catalog.CreateAsync(Tmp);
        if (userSelected) await catalog.SetModelAsync(session.Id, "fast", "small");
        var coordinator = await CoordinatorAsync(new Fake(), catalog);
        var orchestrator = new CaptureOrchestrator();
        var services = new ServiceCollection(); services.AddLogging();
        services.AddSingleton(catalog); services.AddSingleton(coordinator);
        services.AddSingleton<ICodingTurnOrchestrator>(orchestrator);
        services.AddSingleton<IConversationStore>(new FileConversationStore(Paths));
        services.AddSingleton(new FileConversationJobStore(Paths));
        using var provider = services.BuildServiceProvider();
        using var runner = new ConversationRunner(provider, session.Id);
        var finished = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.TurnFinished += r => finished.TrySetResult(r);
        Assert.True(runner.TrySend("Investigate concurrency", "fast", SwarmCore.Coding.Permissions.CodingPermissionMode.Auto, allowHandOff, "small"));
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(userSelected ? "fast" : "strong", orchestrator.Request!.LaneName);
        Assert.Equal(userSelected ? "small" : "large@high", orchestrator.Request.ModelOverride);
        Assert.False(orchestrator.Request.SelectionPinned);
        Assert.Equal(allowHandOff, CodingRoutingPolicy.CanHandOff(orchestrator.Request, configured: true));
        // A refusal hands off to the next lane (the point of SwarmCode); the inner router still may not swap a lane's model.
        Assert.False(orchestrator.Request.StopOnRefusal);
        Assert.True(orchestrator.Request.DisableRouteFallback);
        var saved = await new WorkspaceSessionCatalog(Paths).GetAsync(session.Id);
        Assert.Equal(userSelected ? "small" : "large@high", saved!.Selection!.Model);
        Assert.Equal(userSelected ? "fast" : "strong", saved.LaneName);
    }

    private sealed class CaptureOrchestrator : ICodingTurnOrchestrator
    {
        public CodingTurnRequest? Request;
        public IReadOnlyList<CodingLane> Lanes => DecisionSelectionTests.Lanes;
        public CodingLane? ResolveLane(string? name) => Lanes.FirstOrDefault(l => l.Name == name);
        public async IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Request = request; await Task.Yield();
            yield return new CodingLaneSelected("test", Lanes[1], 1);
            yield return new TextChunk("test", "Synthetic execution receipt");
            // No false completion: the host marks this incomplete and requests reassessment.
        }
    }
}
