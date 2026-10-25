using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmCode.Hosting.Goals;
using SwarmCore.Abstractions.Collaboration;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding;
using SwarmCore.Coding.Goals;
using SwarmCore.Coding.Permissions;
using SwarmCore.Coding.Review;
using SwarmCore.Coding.Routing;
using SwarmCore.LlmRouting.Decisions;

namespace SwarmCode.Hosting.Tests;

/// <summary>Real desktop runner/selection/goal/lane admission; only physical model runners are synthetic.</summary>
public sealed class ConversationGoalSelectedRouteRecoveryTests : TempDirTest
{
    private const string SelectedModel = "codex-cli:synthetic-selected-model@high";
    private static readonly CodingLane NativeLane = new("selected-native", "CodexNative", "codex-cli:configured-default");
    private static readonly CodingLane EngineLane = new("other-engine", "SyntheticEngine", "different-configured-model");

    private sealed class NativeProbe : INativeLaneRunner
    {
        public ConcurrentQueue<NativeLaneTurn> Calls { get; } = new();
        public bool Supports(CodingLane lane) => lane.Provider == NativeLane.Provider;
        public async IAsyncEnumerable<TurnEvent> RunAsync(NativeLaneTurn turn, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Calls.Enqueue(turn);
            await Task.Yield();
            yield return new TurnErrored("synthetic-native", "Synthetic native route unavailable.")
            { FailureCategory = LlmFailureCategory.ModelNotFound };
        }
    }

    private sealed class EngineProbe : IAgentTurnRunner
    {
        public ConcurrentQueue<AgentTurnRequest> Calls { get; } = new();
        public async IAsyncEnumerable<TurnEvent> RunAsync(AgentTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Calls.Enqueue(request);
            await Task.Yield();
            yield return new TurnCompleted("synthetic-engine", "The synthetic ordinary response is complete.", TimeSpan.Zero);
        }
    }

    private sealed class Workspace(string root) : ICodingWorkspaceResolver
    {
        public Task<CodingWorkspace?> ResolveAsync(string id, CancellationToken ct = default)
            => Task.FromResult<CodingWorkspace?>(new() { RepositoryRoot = root, AcceptanceChecks = [] });
    }

    private sealed class RecordedPolicy(ICodingGoalRequestPolicy actual) : ICodingGoalRequestPolicy
    {
        public ConcurrentQueue<CodingTurnRequest> Requests { get; } = new();
        public async Task<CodingTurnRequest> PrepareAsync(CodingTurnRequest request, CancellationToken ct = default)
        {
            var prepared = await actual.PrepareAsync(request, ct);
            Requests.Enqueue(prepared);
            return prepared;
        }
    }

    private sealed class ForbiddenAcceptance : IContinualObjectiveAcceptanceService
    {
        public int Calls;
        public Task<ContinualAcceptanceVerdict> EvaluateAsync(ContinualAcceptanceRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("Unresolved physical receipts cannot reach a completion model.");
        }
    }

    private sealed class ForbiddenPanel : IModelPanelCompletion
    {
        public int Calls;
        public Task<string> CompleteAsync(CodingLane lane, string prompt, int maxTokens, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("Exact recovery cannot ask a different opinion or review model.");
        }
    }

    private sealed record Harness(ServiceProvider Provider, ConversationRunner Runner, WorkspaceSessionCatalog Sessions,
        FileCodingGoalStore Store, CodingGoalOrchestrator Goals, RecordedPolicy Policy, NativeProbe Native,
        EngineProbe Engine, ForbiddenAcceptance Acceptance, ForbiddenPanel Panel, WorkspaceSessionInfo Session) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Runner.DisposeAsync();
            await Provider.DisposeAsync();
        }
    }

    private async Task<Harness> BuildAsync(bool assistance = false)
    {
        var sessions = new WorkspaceSessionCatalog(Paths);
        var session = await sessions.CreateAsync(Tmp, "Selected native work", NativeLane.Name, model: SelectedModel);
        await sessions.SetModelAsync(session.Id, NativeLane.Name, SelectedModel);
        await sessions.SetTemporaryAsync(session.Id, true);
        if (assistance) await sessions.SetAssistanceAsync(session.Id, new TurnAssistanceOptions
        {
            GetOpinionsFirst = true, RecommendOpinionModels = false,
            OpinionModels = [new("different-opinion", EngineLane.Provider, EngineLane.Model!, EngineLane.Name)],
            ReviewBeforeFinishing = true, RecommendReviewer = false,
            Reviewer = new("different-review", EngineLane.Provider, EngineLane.Model!, EngineLane.Name),
        }, session.AssistanceRevision);
        session = (await sessions.GetAsync(session.Id))!;
        var native = new NativeProbe(); var engine = new EngineProbe(); var audit = new ForbiddenAcceptance();
        var panel = new ForbiddenPanel();
        var store = new FileCodingGoalStore(Paths); var workspace = new Workspace(Tmp);
        var services = new ServiceCollection(); services.AddLogging();
        services.AddSingleton(sessions);
        services.AddSingleton<IConversationStore>(new FileConversationStore(Paths));
        services.AddSingleton(new FileConversationJobStore(Paths));
        services.AddSingleton(new DecisionSettingsStore(Paths));
        // The real explicit-picker selection path runs; no decision provider, LLM router, CLI or worker is installed.
        services.AddSingleton(new ModelDecisionSelector([]));
        services.AddSingleton<DecisionSessionCoordinator>();
        services.AddSingleton<ICodingWorkspaceResolver>(workspace);
        services.AddSingleton<ICodingLaneSource>(new OptionsCodingLaneSource(Options.Create(new CodingLaneOptions
        { DefaultLane = NativeLane.Name, HandOffOnRefusal = true, Lanes = [NativeLane, EngineLane] })));
        services.AddSingleton<INativeLaneRunner>(native);
        services.AddSingleton<IAgentTurnRunner>(engine);
        services.AddSingleton<IModelPanelCompletion>(panel);
        services.AddSingleton<ICodingTurnOrchestrator>(provider => new CodingTurnOrchestrator(
            provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new CodingOptions()),
            provider.GetRequiredService<ILogger<CodingTurnOrchestrator>>()));
        services.AddSingleton(provider => new RecordedPolicy(new SwarmCodeGoalRequestPolicy(provider,
            new ConfigurationBuilder().Build(), workspace, new AppPreferencesStore(Paths))));
        services.AddSingleton(provider => new CodingGoalOrchestrator(provider.GetRequiredService<ICodingTurnOrchestrator>(),
            store, audit, policy: provider.GetRequiredService<RecordedPolicy>()));
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        Assert.Null(provider.GetService<ILlmRouter>());
        return new(provider, new ConversationRunner(provider, session.Id), sessions, store,
            provider.GetRequiredService<CodingGoalOrchestrator>(), provider.GetRequiredService<RecordedPolicy>(),
            native, engine, audit, panel, session);
    }

    private static Task<TurnResult> NextFinish(ConversationRunner runner)
    {
        var finished = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.TurnFinished += result => finished.TrySetResult(result);
        return finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Installed_resume_chain_holds_selected_native_recovery_without_other_models_or_replaying_uncertain_work(bool assistance)
    {
        await using var h = await BuildAsync(assistance);
        Assert.Equal(assistance, h.Session.Assistance.Enabled);
        const string objective = "Preserve the existing artifact and finish only the remaining work.";
        const string artifact = "Exact saved artifact before the interrupted operation.";
        var request = SwarmCodeTurnPolicy.Apply(new()
        {
            ConversationId = h.Session.Id, UserMessage = objective, LaneName = NativeLane.Name,
            ModelOverride = SelectedModel, AllowHandOff = true,
        }, CodingPermissionPolicy.AutoNoRules, "synthetic-desktop-user");
        var goal = await h.Goals.StartAsync(request);
        var now = DateTimeOffset.UtcNow;
        var lease = (await h.Store.TryClaimAsync(goal.Id, goal.Version, "prior-worker", now, now.AddMinutes(2)))!;
        var prior = new ContinualToolReceipt(goal.Id, 1, 1, "exact-interrupted-operation", "codex.native_operation",
            ContinualToolReceiptState.Started, ContinualSideEffectClass.WritesLocal,
            "{\"operation\":\"synthetic-uncertain-effect\"}", null, false, now);
        Assert.True(await h.Store.UpsertReceiptAsync(goal.Id, lease.Token, prior));
        Assert.True(await h.Store.ApplyTransitionAsync(goal.Id, lease.Token, new(ContinualObjectiveStatus.Paused,
            now, now, null, artifact, "Prior outcome needs inspection.", null, "Do not replay it.", 0)));
        await h.Runner.LoadHistoryAsync();
        Assert.False(h.Runner.IsRunning); Assert.Equal(goal.Id, h.Runner.Goal!.Id);
        var selectionBefore = JsonSerializer.Serialize((await h.Sessions.GetAsync(h.Session.Id))!.Selection);
        var finish = NextFinish(h.Runner);
        Assert.True(h.Runner.ResumeGoal(NativeLane.Name, allowHandOff: true, modelOverride: SelectedModel));
        var outcome = await finish;

        var saved = (await h.Store.GetAsync(goal.Id))!;
        var hostInspection = Assert.Single(h.Policy.Requests, item => item.GoalId == goal.Id && item.ReadOnlyInspection);
        Assert.True(hostInspection.PlanMode); Assert.True(hostInspection.ReviewOnly);
        Assert.False(hostInspection.AllowNativeLanes); Assert.True(hostInspection.RequireSelectedRoute);
        var inspection = Assert.Single(h.Policy.Requests, item => item.GoalId == goal.Id && !item.ReadOnlyInspection);
        Assert.True(inspection.RequireSelectedRoute);
        Assert.False(inspection.Assistance.Enabled);
        Assert.False(inspection.SelectionPinned); // The host fence does not fabricate user-pin semantics.
        Assert.Equal(LlmOverrideProvenance.User, inspection.RouteOverrideProvenance);
        Assert.True(inspection.PlanMode); Assert.True(inspection.ReviewOnly);
        Assert.False(inspection.ReadOnlyInspection); Assert.False(inspection.AllowNativeLanes);
        Assert.True(inspection.CommunicationScope!.UserInitiated);
        Assert.Equal(NativeLane.Name, inspection.LaneName); Assert.Equal(SelectedModel, inspection.ModelOverride);
        Assert.Empty(h.Native.Calls); Assert.Empty(h.Engine.Calls); Assert.Equal(0, h.Acceptance.Calls);
        Assert.Equal(0, h.Panel.Calls);
        Assert.False(outcome.Completed);
        Assert.Equal(ContinualObjectiveStatus.Uncertain, saved.Status);
        Assert.Null(saved.LeaseToken); Assert.Equal(artifact, saved.LastOutput); Assert.Equal(objective, saved.ObjectiveText);
        Assert.Equal(SelectedModel, saved.Model);
        Assert.Equal(JsonSerializer.Serialize(prior), JsonSerializer.Serialize(Assert.Single(saved.Receipts)));
        Assert.Equal(selectionBefore, JsonSerializer.Serialize((await h.Sessions.GetAsync(h.Session.Id))!.Selection));
        // The prepared host request includes runtime source-validation delegates; serialize a DTO-shaped request.
        Assert.DoesNotContain("RequireSelectedRoute", JsonSerializer.Serialize(new CodingTurnRequest
        { ConversationId = h.Session.Id, UserMessage = objective, RequireSelectedRoute = inspection.RequireSelectedRoute }));
    }

    [Fact]
    public async Task Ordinary_host_chat_retains_visible_handoff_preference_instead_of_turning_picker_choice_into_a_hard_pin()
    {
        await using var h = await BuildAsync();
        await h.Runner.LoadHistoryAsync();
        var finish = NextFinish(h.Runner);
        Assert.True(h.Runner.TrySend("Explain this synthetic project in plain language.", NativeLane.Name,
            allowHandOff: true, modelOverride: SelectedModel));
        Assert.True((await finish).Completed);
        Assert.Equal(SelectedModel, Assert.Single(h.Native.Calls).Lane.Model);
        var engine = Assert.Single(h.Engine.Calls);
        Assert.Equal(EngineLane.Provider, engine.ProviderOverride); Assert.Equal(EngineLane.Model, engine.ModelOverride);
        Assert.Null(await h.Store.GetLatestForConversationAsync(CodingGoalOrchestrator.Partition, h.Session.Id));
        Assert.Equal(0, h.Acceptance.Calls);
    }
}
