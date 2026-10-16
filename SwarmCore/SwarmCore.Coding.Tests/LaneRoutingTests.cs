using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding;
using SwarmCore.Coding.Routing;
using Xunit;

namespace SwarmCore.Coding.Tests;

public class RefusalDetectorTests
{
    [Theory]
    [InlineData("I'm sorry, but I can't help with that request.")]
    [InlineData("I cannot assist with modifying authentication code.")]
    [InlineData("Sorry, I won't do that.")]
    [InlineData("I must decline this request because it conflicts with my guidelines.")]
    [InlineData("That is not something I can help with here.")]
    [InlineData("I won't build this feature. I can review the existing code.")]
    [InlineData("Sorry, but I will not run that implementation.")]
    [InlineData("I won’t read that file.")]
    [InlineData("I didn't look for free, leaked or shared keys, because those are pirated. The genuine options I found: retailer links.")]
    [InlineData("I did not implement the requested feature because it violates my guidelines. Here is a review instead.")]
    [InlineData("I’m not going to do this one — I'll explain why and what I can offer instead.")]
    [InlineData("I still won't look for free, leaked or shared Windows 11 Pro keys, because those are pirated and often malware. That means no key list from me.")]
    [InlineData("I cannot find Windows 11 Pro keys for you, as that involves locating or distributing license keys for software I do not have rights to provide.")]
    [InlineData("I cannot access that resource because it is prohibited by our policy.")]
    [InlineData("I checked the local state. However, I cannot assist in finding or generating additional material as that would involve actions contrary to legal and ethical standards.")]
    public void Recognizes_refusals(string text) => Assert.True(RefusalDetector.IsRefusal(text));

    [Theory]
    [InlineData("Done. I updated Program.cs and ran the tests; all 12 pass.")]
    [InlineData("I can't find a file named Foo.cs, so I created it under src/ and wired it up. Build is green.")]
    [InlineData("I can't build because the required SDK is missing. I found the configuration error.")]
    [InlineData("I cannot run the tests until the missing dependency is restored.")]
    [InlineData("I cannot find the policy document because the directory does not exist.")]
    [InlineData("I checked the local state. However, I cannot assist in running these tests as the SDK is missing.")]
    [InlineData("I didn't run tests because the SDK is missing.")]
    [InlineData("I didn't change the unauthorized-access detector because its tests pass.")]
    [InlineData("")]
    public void Ignores_ordinary_answers(string text) => Assert.False(RefusalDetector.IsRefusal(text));

    [Fact]
    public void Long_answer_with_incidental_policy_words_is_not_a_refusal()
    {
        var text = "Here is the plan. " + new string('x', 2000) + " Note: the API has a policy violation error code we must handle.";
        Assert.False(RefusalDetector.IsRefusal(text));
    }

    [Fact]
    public void Intentional_build_refusal_with_long_review_is_not_delivered()
    {
        var text = "I won't build this feature. I can review the existing implementation.\n\n" + new string('x', 2000);
        Assert.Equal(LaneOutcomeKind.Refused, RefusalDetector.Classify(text, null, mutatingToolsSucceeded: false));
        Assert.Equal(LaneOutcomeKind.PartialRefusal, RefusalDetector.Classify(text, null, mutatingToolsSucceeded: true));
    }

    [Fact]
    public void Long_policy_refusal_after_an_opening_acknowledgment_is_not_delivered()
    {
        var text = "I understand the request. However, I cannot provide that material for several reasons.\n"
            + string.Concat(Enumerable.Repeat("Here is an alternative explanation.\n", 60))
            + "I know this isn't what you asked for, but I can't ethically provide that material.";
        Assert.True(RefusalDetector.IsRefusal(text));
        Assert.Equal(LaneOutcomeKind.Refused, RefusalDetector.Classify(text, null, false));
    }

    [Fact]
    public void Explicit_remaining_decline_after_completed_work_is_partial_even_in_a_long_answer()
    {
        var text = "I checked the local state. " + new string('x', 2000)
            + " The remaining request is the part in question; that's the part I won't source.";
        Assert.Equal(LaneOutcomeKind.PartialRefusal, RefusalDetector.Classify(text, null, true));
    }

    [Theory]
    [InlineData("The 'claude' CLI is not installed on this machine", LaneOutcomeKind.RouteUnavailable)]
    [InlineData("HTTP 429 rate limit exceeded", LaneOutcomeKind.RouteUnavailable)]
    [InlineData("Response status code does not indicate success: 503 (Service Unavailable).", LaneOutcomeKind.RouteUnavailable)]
    [InlineData("HttpRequestException: No connection could be made because the target machine actively refused it. (localhost:11434)", LaneOutcomeKind.RouteUnavailable)]
    [InlineData("Could not launch the 'gemini' CLI at 'C:\\tools\\gemini.cmd': access denied", LaneOutcomeKind.RouteUnavailable)]
    [InlineData("Request blocked by content_policy", LaneOutcomeKind.Refused)]
    [InlineData("Object reference not set to an instance of an object", LaneOutcomeKind.Failed)]
    // A path, a stack trace or an exit code that merely contains a route word is the turn's own failure.
    [InlineData("Could not start the turn: Could not find file 'C:\\repo\\network\\config.json'.", LaneOutcomeKind.Failed)]
    [InlineData("The build exited with code 512.", LaneOutcomeKind.Failed)]
    [InlineData("TypeError at src/pages/login-form.ts:42 (executable script)", LaneOutcomeKind.Failed)]
    [InlineData("Seeded 500 credits into the test account fixture before it threw.", LaneOutcomeKind.Failed)]
    public void Classifies_errors(string message, LaneOutcomeKind expected) => Assert.Equal(expected, RefusalDetector.ClassifyError(message));

    [Fact]
    public void Refusal_after_mutating_work_is_a_partial_refusal()
        => Assert.Equal(LaneOutcomeKind.PartialRefusal, RefusalDetector.Classify("I can't do the second part.", null, mutatingToolsSucceeded: true));
}

public class CodingTurnOrchestratorTests
{
    private sealed class ScriptedRunner(Queue<Func<AgentTurnRequest, IEnumerable<TurnEvent>>> script, List<AgentTurnRequest> seen) : IAgentTurnRunner
    {
        public async IAsyncEnumerable<TurnEvent> RunAsync(AgentTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            seen.Add(request);
            var step = script.Count > 0 ? script.Dequeue() : _ => [new TurnCompleted("t", "ok", TimeSpan.Zero)];
            foreach (var e in step(request)) { await Task.Yield(); yield return e; }
        }
    }

    private static (CodingTurnOrchestrator orchestrator, List<AgentTurnRequest> seen) Build(
        IEnumerable<Func<AgentTurnRequest, IEnumerable<TurnEvent>>> script, Action<CodingLaneOptions>? lanes = null)
    {
        var seen = new List<AgentTurnRequest>();
        var queue = new Queue<Func<AgentTurnRequest, IEnumerable<TurnEvent>>>(script);
        var services = new ServiceCollection();
        services.AddScoped<IAgentTurnRunner>(_ => new ScriptedRunner(queue, seen));
        var laneOptions = new CodingLaneOptions
        {
            Lanes =
            [
                new("claude", "ClaudeCode", "claude-code:default") { Priority = 0 },
                new("codex", "CodexCli", "codex-cli:default") { Priority = 1 },
                new("openrouter", "OpenRouter", "some/model") { Priority = 2 },
            ],
        };
        lanes?.Invoke(laneOptions);
        var orchestrator = TestOrchestrator.Create(services, laneOptions, new CodingOptions());
        return (orchestrator, seen);
    }

    private static async Task<List<TurnEvent>> Collect(ICodingTurnOrchestrator o, CodingTurnRequest r)
    {
        var list = new List<TurnEvent>();
        await foreach (var e in o.RunAsync(r)) list.Add(e);
        return list;
    }

    [Fact]
    public async Task Delivered_turn_runs_one_lane_pinned_by_provider_and_model()
    {
        var (o, seen) = Build([_ => [new TurnStarted("t", Guid.NewGuid(), "SwarmCode"), new TextChunk("t", "hi"), new TurnCompleted("t", "hi", TimeSpan.Zero)]]);
        var events = await Collect(o, new CodingTurnRequest
        {
            ConversationId = "c", UserMessage = "hello", CommunicationScope = AgentCommunicationScope.UserRequested("test-host"),
        });

        Assert.Single(seen);
        Assert.Equal("ClaudeCode", seen[0].ProviderOverride);
        Assert.Equal("claude-code:default", seen[0].ModelOverride);
        Assert.True(seen[0].CommunicationScope.UserInitiated);
        Assert.Equal("test-host", seen[0].CommunicationScope.Authority);
        Assert.Equal(Path.GetFullPath(TestOrchestrator.Workspace), seen[0].WorkspaceRoot);
        Assert.Single(events.OfType<CodingLaneSelected>());
        Assert.Empty(events.OfType<CodingLaneHandOff>());
    }

    [Fact]
    public async Task Refusal_hands_off_to_next_lane_as_a_continuation()
    {
        var (o, seen) = Build(
        [
            _ => [new TurnCompleted("t1", "I'm sorry, but I can't help with that.", TimeSpan.Zero)],
            _ => [new ToolCallCompleted("t2", "x", "code.edit_file", "{}", false, TimeSpan.Zero), new TurnCompleted("t2", "Done.", TimeSpan.Zero)],
        ]);
        var events = await Collect(o, new CodingTurnRequest { ConversationId = "c", UserMessage = "rename Foo to Bar" });

        Assert.Equal(2, seen.Count);
        Assert.Equal("CodexCli", seen[1].ProviderOverride);
        Assert.Contains("rename Foo to Bar", seen[1].UserMessage);
        Assert.Contains("[Hand-off]", seen[1].UserMessage);
        var handoff = Assert.Single(events.OfType<CodingLaneHandOff>());
        Assert.Equal("claude", handoff.From.Name);
        Assert.Equal("codex", handoff.To.Name);
        Assert.Empty(events.OfType<CodingLanesExhausted>());
    }

    [Fact]
    public async Task Intentional_build_refusal_hands_off_before_acceptance_checks()
    {
        var refusal = "I won't build this feature. I reviewed the existing code.\n\n" + new string('x', 2000);
        var (o, seen) = Build(
        [
            _ => [new TextChunk("t1", "I'll start by finding the existing code."), new TurnCompleted("t1", refusal, TimeSpan.Zero)],
            _ => [new TurnCompleted("t2", "Implemented and verified.", TimeSpan.Zero)],
        ]);
        var events = await Collect(o, new CodingTurnRequest { ConversationId = "c", UserMessage = "Implement the feature" });

        Assert.Equal(2, seen.Count);
        var handoff = Assert.Single(events.OfType<CodingLaneHandOff>());
        Assert.Equal(LaneOutcomeKind.Refused, handoff.Outcome);
        Assert.Equal("codex", handoff.To.Name);
        Assert.True(events.IndexOf(handoff) < events.FindIndex(e => e is CodingAcceptanceEvaluated));
        Assert.Single(events.OfType<CodingAcceptanceEvaluated>());
    }

    [Fact]
    public async Task Unavailable_route_hands_off_and_exhausts_when_all_fail()
    {
        var (o, seen) = Build(
        [
            _ => [new TurnErrored("t1", "The 'claude' CLI is not installed")],
            _ => [new TurnErrored("t2", "HTTP 429 rate limit")],
            _ => [new TurnErrored("t3", "network unreachable")],
        ]);
        var events = await Collect(o, new CodingTurnRequest { ConversationId = "c", UserMessage = "x" });

        Assert.Equal(3, seen.Count);
        Assert.Equal(2, events.OfType<CodingLaneHandOff>().Count());
        Assert.Single(events.OfType<CodingLanesExhausted>());
    }

    [Fact]
    public async Task Refusal_after_mutation_hands_off_only_the_remainder()
    {
        var (o, seen) = Build(
        [
            _ => [new ToolCallCompleted("t1", "x", "fs.write_file", "{}", false, TimeSpan.Zero), new TurnCompleted("t1", "I can't do the rest.", TimeSpan.Zero)],
        ]);
        var events = await Collect(o, new CodingTurnRequest { ConversationId = "c", UserMessage = "x" });
        Assert.Equal(2, seen.Count);
        var handoff = Assert.Single(events.OfType<CodingLaneHandOff>());
        Assert.Equal(LaneOutcomeKind.PartialRefusal, handoff.Outcome);
        Assert.Contains("Finish only what is still undone", seen[1].UserMessage);
        Assert.Contains("work already completed", seen[1].UserMessage);
    }

    [Fact]
    public async Task Hand_off_can_be_disabled_per_request_and_lane_can_be_chosen()
    {
        var (o, seen) = Build([_ => [new TurnCompleted("t1", "I cannot do that.", TimeSpan.Zero)]]);
        var events = await Collect(o, new CodingTurnRequest { ConversationId = "c", UserMessage = "x", LaneName = "codex", AllowHandOff = false });
        Assert.Single(seen);
        Assert.Equal("CodexCli", seen[0].ProviderOverride);
        Assert.Single(events.OfType<CodingLanesExhausted>());
    }

    [Fact]
    public async Task Unknown_lane_reports_exhausted_without_running()
    {
        var (o, seen) = Build([]);
        var events = await Collect(o, new CodingTurnRequest { ConversationId = "c", UserMessage = "x", LaneName = "nope" });
        Assert.Empty(seen);
        Assert.Single(events.OfType<CodingLanesExhausted>());
    }

    [Fact]
    public async Task SelectedModel_AppliesToInitialLane_AndContinuation_WithoutChangingFallback()
    {
        var (o, seen) = Build([
            _ => [new TurnErrored("first", "HTTP 429 rate limit")],
            _ => [new TurnCompleted("second", "done", TimeSpan.Zero)]
        ]);
        await Collect(o, new CodingTurnRequest { ConversationId = "c", UserMessage = "correction", ModelOverride = "chosen-model", IsContinuation = true });
        Assert.Equal("chosen-model", seen[0].ModelOverride);
        Assert.True(seen[0].IsContinuation);
        Assert.Equal("codex-cli:default", seen[1].ModelOverride);
        Assert.Equal("claude-code:default", o.ResolveLane("claude")!.Model);
    }
}
