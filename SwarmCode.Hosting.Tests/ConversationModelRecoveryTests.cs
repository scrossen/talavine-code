using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Abstractions.LlmRouting.Decisions;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Routing;
using SwarmCore.LlmRouting.Decisions;

namespace SwarmCode.Hosting.Tests;

public sealed class ConversationModelRecoveryTests : TempDirTest
{
    private async Task<string> LegacySessionAsync()
    {
        var catalog = new WorkspaceSessionCatalog(Paths);
        var session = await catalog.CreateAsync(Tmp, laneName: "original", model: "original-model");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(Paths.SessionIndexPath))!;
        document["sessions"]![0]!.AsObject().Remove("selection");
        await File.WriteAllTextAsync(Paths.SessionIndexPath, document.ToJsonString());
        return session.Id;
    }

    [Theory]
    [InlineData("Running")]
    [InlineData("Completed")]
    public async Task ReopeningLegacyJob_RestoresExactModelWithoutRunningOrReassessing(string jobState)
    {
        var id = await LegacySessionAsync();
        var jobs = new FileConversationJobStore(Paths);
        jobs.Save(id, new() { State = jobState, ActiveLane = "resumed", ActiveModel = "saved-model@high" });
        var catalog = new WorkspaceSessionCatalog(Paths);
        var decider = new UnexpectedDecisionProvider();
        var settings = new DecisionSettingsStore(Paths);
        await settings.SaveAsync(new() { Enabled = true, Connection = new() { Provider = decider.Name },
            Candidates = [new("a", "original", "original-model", "Original"), new("b", "resumed", "new-default", "New default")] });
        var coordinator = new DecisionSessionCoordinator(settings, catalog, new([decider]), jobs);
        var orchestrator = new CaptureOrchestrator();
        using var provider = new ServiceCollection().AddLogging().AddSingleton(catalog).AddSingleton(jobs)
            .AddSingleton(coordinator).AddSingleton<IConversationStore>(new FileConversationStore(Paths))
            .AddSingleton<ICodingTurnOrchestrator>(orchestrator).BuildServiceProvider();
        await using var runner = new ConversationRunner(provider, id);
        await runner.LoadHistoryAsync();
        Assert.False(runner.IsRunning);
        Assert.Null(orchestrator.Request);
        Assert.Equal("resumed", runner.ActiveLane);
        Assert.Equal("saved-model@high", runner.ActiveModel);
        var restored = (await new WorkspaceSessionCatalog(Paths).GetAsync(id))!;
        Assert.Equal("resumed", restored.LaneName);
        Assert.Equal("saved-model@high", restored.ModelChoices["resumed"]);
        Assert.True(restored.Selection!.InitialAssessmentComplete);
        Assert.False(restored.Selection.UserSelected);
        Assert.Equal("original-model", restored.ModelChoices["original"]);

        // The resumed host must dispatch the stored model even if its caller supplies today's defaults.
        var finished = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.TurnFinished += result => finished.TrySetResult(result);
        Assert.True(runner.TrySend("Continue", "original", modelOverride: "new-default"));
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("resumed", orchestrator.Request!.LaneName);
        Assert.Equal("saved-model@high", orchestrator.Request.ModelOverride);
        Assert.Equal(0, decider.Calls);
    }

    [Fact]
    public async Task LegacyTurnWithoutOpeningHistory_RecordsActualModelBeforeCompletion()
    {
        var id = await LegacySessionAsync();
        var catalog = new WorkspaceSessionCatalog(Paths);
        var coordinator = new DecisionSessionCoordinator(new(Paths), catalog, new([]));
        var prepared = await coordinator.PrepareAsync(id, "Continue", "default", "default-model", [], false, default);
        Assert.Equal("original-model", prepared.Model);
        // A handoff happens during the turn, before its final answer or activity update.
        Assert.NotNull(await coordinator.ObserveAsync(id, prepared.Revision,
            new("resumed", "test", "exact-model@high"), true, default));
        var reopened = new DecisionSessionCoordinator(new(Paths), new(Paths), new([]));
        var next = await reopened.PrepareAsync(id, "Continue after shutdown", "default", "changed-default", [], false, default);
        Assert.Equal("resumed", next.Lane);
        Assert.Equal("exact-model@high", next.Model);
    }

    [Fact]
    public async Task IndependentRecoveryWriter_PreservesEdits_AndCannotReplaceNewerChoiceOrRecreateDeletion()
    {
        var id = await LegacySessionAsync();
        var recovery = new WorkspaceSessionCatalog(Paths);
        var editor = new WorkspaceSessionCatalog(Paths);
        await recovery.GetAsync(id);
        await Task.WhenAll(recovery.RestoreLegacySelectionAsync(id, "resumed", "saved-model"),
            editor.RenameAsync(id, "Renamed"), editor.SetTemporaryAsync(id, true));
        var restored = (await editor.GetAsync(id))!;
        Assert.Equal("Renamed", restored.Title);
        Assert.True(restored.Temporary);

        await editor.SetModelAsync(id, "chosen", "new-choice");
        var current = (await recovery.RestoreLegacySelectionAsync(id, "resumed", "stale-model"))!;
        Assert.Equal("chosen", current.LaneName);
        Assert.Equal("new-choice", current.Selection!.Model);
        Assert.True(current.Selection.UserSelected);
        Assert.False(await recovery.TrySetSelectionAsync(id, restored.Selection!.Revision,
            restored.Selection with { Model = "stale-model" }));
        await editor.SetTemporaryAsync(id, false);
        await recovery.RestoreLegacySelectionAsync(id, "resumed", "stale-model");
        Assert.False((await recovery.GetAsync(id))!.Temporary);
        await editor.DeleteAsync(id);
        Assert.Null(await recovery.RestoreLegacySelectionAsync(id, "resumed", "stale-model"));
        Assert.Null(await editor.GetAsync(id));
    }

    [Fact]
    public async Task NewConversation_RemainsEligibleForInitialSelection()
    {
        var catalog = new WorkspaceSessionCatalog(Paths);
        var session = await catalog.CreateAsync(Tmp, laneName: "original", model: "original-model");
        var restored = (await catalog.RestoreLegacySelectionAsync(session.Id, "stale", "stale-model"))!;
        Assert.Equal(session.Selection, restored.Selection);
        Assert.True(ModelSelectionPolicy.ShouldAssess(restored.Selection!));
    }

    private sealed class UnexpectedDecisionProvider : IChoiceDecisionProvider
    {
        public string Name => "unexpected";
        public int Calls;
        public Task<ChoiceDecision> ChooseAsync(DecisionConnection connection, ChoiceDecisionRequest request, CancellationToken ct = default)
        {
            Calls++;
            throw new InvalidOperationException("Opening a legacy session must not trigger first-prompt selection.");
        }
    }

    private sealed class CaptureOrchestrator : ICodingTurnOrchestrator
    {
        public CodingTurnRequest? Request;
        public IReadOnlyList<CodingLane> Lanes => [new("original", "test", "new-default"), new("resumed", "test", "new-default")];
        public CodingLane? ResolveLane(string? name) => Lanes.FirstOrDefault(l => l.Name == name);
        public async IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Request = request;
            await Task.Yield();
            yield return new CodingLaneSelected("turn", new(request.LaneName!, "test", request.ModelOverride!), 1);
            yield return new TextChunk("turn", "Synthetic response");
        }
    }
}
