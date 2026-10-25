using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Abstractions.Collaboration;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Routing;
using SwarmCore.Coding.Review;

namespace SwarmCore.Coding.Tests;

public sealed class TurnAssistanceTests
{
    private static readonly CodingLane Worker = new("worker", "ProviderA", "model-a");
    private static readonly CodingLane Reviewer = new("reviewer", "ProviderB", "model-b");
    private static AssistanceModelChoice Pick(CodingLane lane) => new(lane.Name, lane.Provider, lane.Model, lane.Name);
    private sealed class Completion(List<string> trace, bool fail = false) : IModelPanelCompletion
    {
        public Task<string> CompleteAsync(CodingLane lane, string prompt, int maxTokens, CancellationToken ct)
        {
            trace.Add("opinion:" + lane.Name);
            if (fail) throw new InvalidOperationException("offline");
            return Task.FromResult("Consider an incremental implementation and focused tests.");
        }
    }
    private sealed class Runner(List<AgentTurnRequest> requests, List<string> trace, bool failReview) : IAgentTurnRunner
    {
        public async IAsyncEnumerable<TurnEvent> RunAsync(AgentTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            requests.Add(request);
            var review = request.ConversationId.Contains("-review-");
            trace.Add(review ? "review" : request.IsContinuation ? "final" : "work");
            await Task.Yield();
            if (review && failReview) { yield return new TurnErrored("review", "offline"); yield break; }
            yield return new TurnCompleted("test", review ? "Check the edge case." : request.IsContinuation ? "Final answer with review addressed." : "Work is ready.", TimeSpan.Zero);
        }
    }
    private static CodingTurnOrchestrator Build(List<AgentTurnRequest> requests, List<string> trace, bool failReview = false, bool failOpinion = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAgentTurnRunner>(new Runner(requests, trace, failReview));
        services.AddSingleton<IModelPanelCompletion>(new Completion(trace, failOpinion));
        return TestOrchestrator.Create(services, new CodingLaneOptions { Lanes = [Worker, Reviewer], HandOffOnRefusal = false }, new CodingOptions());
    }
    private static CodingTurnRequest Request() => new()
    {
        Metadata = new Dictionary<string, string> { ["host-authority-scope"] = "retain-original-restrictions" },
        ConversationId = "assistance-" + Guid.NewGuid().ToString("N"), UserMessage = "Explain this repository.", LaneName = Worker.Name,
        Assistance = new() { GetOpinionsFirst = true, RecommendOpinionModels = false, OpinionModels = [Pick(Reviewer)],
            ReviewBeforeFinishing = true, RecommendReviewer = false, Reviewer = Pick(Reviewer) }
    };
    private static async Task<List<TurnEvent>> Drain(IAsyncEnumerable<TurnEvent> stream)
    { var list = new List<TurnEvent>(); await foreach (var e in stream) list.Add(e); return list; }

    public class SelectionProxy : System.Reflection.DispatchProxy
    {
        public List<SwarmCore.Abstractions.LlmRouting.LlmRouterOptions> Options { get; } = [];
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (method?.Name != "SelectOptimalModelAsync") throw new NotSupportedException();
            Options.Add((SwarmCore.Abstractions.LlmRouting.LlmRouterOptions)args![2]!);
            return Task.FromResult(new SwarmCore.Abstractions.LlmRouting.LlmModelSelection { Provider = Reviewer.Provider, Model = Reviewer.Model, Reason = "Independent evidence checking" });
        }
    }
    [Fact]
    public async Task Host_selected_route_fence_never_invokes_opinion_selector_panel_or_reviewer_models()
    {
        var seen = new List<AgentTurnRequest>(); var trace = new List<string>();
        var services = new ServiceCollection().AddSingleton<IAgentTurnRunner>(new Runner(seen, trace, false))
            .AddSingleton<IModelPanelCompletion>(new Completion(trace));
        var selector = System.Reflection.DispatchProxy.Create<SwarmCore.Abstractions.LlmRouting.ILlmRouter, SelectionProxy>();
        services.AddSingleton(selector);
        var orchestrator = TestOrchestrator.Create(services,
            new CodingLaneOptions { Lanes = [Worker, Reviewer], HandOffOnRefusal = true }, new CodingOptions());
        var request = Request() with { RequireSelectedRoute = true,
            RouteOverrideProvenance = SwarmCore.Abstractions.LlmRouting.LlmOverrideProvenance.Policy,
            Assistance = new() { GetOpinionsFirst = true, ReviewBeforeFinishing = true } };
        var events = await Drain(orchestrator.RunAsync(request));
        Assert.Single(events.OfType<TurnCompleted>()); Assert.Single(seen); Assert.Equal(["work"], trace);
        Assert.Empty(((SelectionProxy)(object)selector).Options);
        Assert.Equal(Worker.Provider, seen[0].ProviderOverride); Assert.Equal(Worker.Model, seen[0].ModelOverride);
        Assert.Equal(request.RouteOverrideProvenance, seen[0].RouteOverrideProvenance); Assert.False(request.SelectionPinned);
    }
    [Fact]
    public async Task Recommendations_use_the_existing_selector_with_finite_candidates_and_do_not_replace_pins()
    {
        var seen = new List<AgentTurnRequest>(); var trace = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton<IAgentTurnRunner>(new Runner(seen, trace, false));
        services.AddSingleton<IModelPanelCompletion>(new Completion(trace));
        var selector = System.Reflection.DispatchProxy.Create<SwarmCore.Abstractions.LlmRouting.ILlmRouter, SelectionProxy>();
        services.AddSingleton(selector);
        var orchestrator = TestOrchestrator.Create(services,
            new CodingLaneOptions { Lanes = [Worker, Reviewer, new("third", "ProviderC", "model-c")], HandOffOnRefusal = false }, new CodingOptions());
        var request = Request() with { Assistance = new() { GetOpinionsFirst = true, ReviewBeforeFinishing = true } };
        var events = await Drain(orchestrator.RunAsync(request));
        Assert.Single(events.OfType<TurnCompleted>());
        Assert.Equal(2, ((SelectionProxy)(object)selector).Options.Count);
        Assert.All(((SelectionProxy)(object)selector).Options, o =>
        {
            Assert.True(o.ModelRequest!.RequirePreferredModel);
            Assert.DoesNotContain(Worker.Model, o.ModelRequest.PreferredModels);
        });
        Assert.Contains(events.OfType<CodingLaneNotice>(), e => e.Details?.Contains("Independent evidence checking") == true);
        Assert.Equal(Reviewer.Provider, seen[1].ProviderOverride);
    }

    [Fact]
    public async Task Opinion_precedes_work_review_is_fresh_and_read_only_final_completion_waits_for_revision()
    {
        var seen = new List<AgentTurnRequest>(); var trace = new List<string>();
        var events = await Drain(Build(seen, trace).RunAsync(Request()));
        Assert.Equal(["opinion:reviewer", "work", "review", "final"], trace);
        Assert.Equal(3, seen.Count);
        Assert.Contains("Advisory model opinions", seen[0].UserMessage);
        Assert.Equal("Explain this repository.", seen[0].AuthenticatedOriginalUserMessage);
        Assert.NotEqual(seen[0].ConversationId, seen[1].ConversationId);
        Assert.Equal("Explain this repository.", seen[1].AuthenticatedOriginalUserMessage);
        Assert.Equal("retain-original-restrictions", seen[1].Metadata!["host-authority-scope"]);
        Assert.True(SwarmCore.Coding.Planning.CodingPlanMode.IsActive(seen[1].Metadata));
        Assert.DoesNotContain("[Plan mode]", seen[1].UserMessage);
        Assert.Equal("ProviderB", seen[1].ProviderOverride);
        Assert.True(seen[2].IsContinuation);
        Assert.Equal("ProviderA", seen[2].ProviderOverride);
        Assert.Equal("Explain this repository.", seen[2].AuthenticatedOriginalUserMessage);
        Assert.Equal("Final answer with review addressed.", Assert.Single(events.OfType<TurnCompleted>()).FinalText);
        Assert.DoesNotContain(events.OfType<CodingLaneSelected>(), e => e.Lane.Name == Reviewer.Name);
        Assert.Contains(events.OfType<CodingLaneNotice>(), e => e.Message == Reviewer.Route && e.Details == "Check the edge case.");
    }

    [Fact]
    public async Task Failed_review_does_not_publish_work_as_completed_or_silently_skip_review()
    {
        var seen = new List<AgentTurnRequest>(); var trace = new List<string>();
        var events = await Drain(Build(seen, trace, failReview: true).RunAsync(Request()));
        Assert.Equal(["opinion:reviewer", "work", "review"], trace);
        Assert.Empty(events.OfType<TurnCompleted>());
        Assert.Contains(events.OfType<TurnErrored>(), e => e.Message.Contains("independent review did not finish"));
    }

    [Fact]
    public async Task Failed_opinion_stops_before_work_and_unavailable_pin_is_not_substituted()
    {
        var seen = new List<AgentTurnRequest>(); var trace = new List<string>();
        var events = await Drain(Build(seen, trace, failOpinion: true).RunAsync(Request()));
        Assert.Empty(seen); Assert.Empty(events.OfType<TurnCompleted>());
        var request = Request();
        events = await Drain(Build(seen, trace).RunAsync(request with { Assistance = request.Assistance with { Reviewer = Pick(Reviewer) with { RouteName = "removed" } } }));
        Assert.Empty(seen); Assert.Contains(events.OfType<TurnErrored>(), e => e.Message.Contains("Pinned model"));
    }

    [Fact]
    public async Task Off_preserves_the_original_single_turn_path()
    {
        var seen = new List<AgentTurnRequest>(); var trace = new List<string>();
        var events = await Drain(Build(seen, trace).RunAsync(Request() with { Assistance = new() }));
        Assert.Equal(["work"], trace); Assert.Single(seen); Assert.Single(events.OfType<TurnCompleted>());
    }
}
