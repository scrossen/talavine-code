using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Routing;
using SwarmCore.Coding.Tools;
using Xunit;

namespace SwarmCore.Coding.Tests;

/// <summary>
/// The hand-off decides from typed signals first: the decline tool, the provider's failure
/// category, a stuck loop, and lane cooldowns. Prose heuristics are only the fallback.
/// </summary>
public sealed class HandOffSignalTests
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

    private sealed class FakeRewriter(AgentConversationExecutionGate gate) : ITranscriptRewriter
    {
        public List<(string Match, string Replacement)> Calls { get; } = [];
        public bool OwnedDuringRewrite { get; private set; }
        public async Task<bool> ReplaceLastAssistantTextAsync(string conversationId, string matchText, string replacementText, CancellationToken ct = default)
        {
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(TimeSpan.FromMilliseconds(100));
            try { await using var lease = await gate.AcquireAsync(conversationId, probe.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { OwnedDuringRewrite = true; }
            Calls.Add((matchText, replacementText));
            return true;
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record Harness(CodingTurnOrchestrator Orchestrator, List<AgentTurnRequest> Seen, FakeRewriter Rewriter, LaneHealthTracker Health, ManualTime Time);

    private static Harness Build(IEnumerable<Func<AgentTurnRequest, IEnumerable<TurnEvent>>> script, Action<CodingLaneOptions>? lanes = null, Action<CodingOptions>? coding = null)
    {
        var seen = new List<AgentTurnRequest>();
        var queue = new Queue<Func<AgentTurnRequest, IEnumerable<TurnEvent>>>(script);
        var gate = new AgentConversationExecutionGate();
        var rewriter = new FakeRewriter(gate);
        var services = new ServiceCollection();
        services.AddSingleton(gate);
        services.AddScoped<IAgentTurnRunner>(_ => new ScriptedRunner(queue, seen));
        services.AddSingleton<ITranscriptRewriter>(rewriter);
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
        var codingOptions = new CodingOptions();
        coding?.Invoke(codingOptions);
        var time = new ManualTime();
        var health = new LaneHealthTracker(time);
        var orchestrator = TestOrchestrator.Create(services, laneOptions, codingOptions, health);
        return new Harness(orchestrator, seen, rewriter, health, time);
    }

    private static async Task<List<TurnEvent>> Collect(ICodingTurnOrchestrator o, CodingTurnRequest r)
    {
        var list = new List<TurnEvent>();
        await foreach (var e in o.RunAsync(r)) list.Add(e);
        return list;
    }

    private static CodingTurnRequest Request(string? lane = null) => new() { ConversationId = "c", UserMessage = "add a feature", LaneName = lane };

    [Fact]
    public async Task Requested_file_edit_is_not_completed_by_prose_only()
    {
        var h = Build([
            _ => [new TurnCompleted("t1", "Here is the code to add to the file: ```cs\nclass Example {}\n```", TimeSpan.Zero)],
            _ => [new TurnCompleted("t2", "I implemented it. ```cs\nclass Example {}\n```", TimeSpan.Zero)],
        ]);
        var events = await Collect(h.Orchestrator, Request("openrouter") with
        {
            UserMessage = "Implement the code changes in the repository.",
            RequireWorkspaceChange = true,
            SelectionPinned = true,
        });

        Assert.Equal(2, h.Seen.Count);
        Assert.All(h.Seen, attempt => Assert.Equal("OpenRouter", attempt.ProviderOverride));
        Assert.Contains(events, e => e is CodingLaneNotice notice && notice.Message.Contains("did not change repository files"));
        Assert.Contains(events, e => e is TurnErrored error && error.ErrorKind == TurnErrorKind.CompletionValidation
            && error.Message.Contains("no file-changing tool completed"));
        Assert.DoesNotContain(events, e => e is CodingAcceptanceEvaluated);
    }

    [Fact]
    public async Task Requested_file_edit_can_complete_after_repair_uses_a_write_tool()
    {
        var h = Build([
            _ => [new TurnCompleted("t1", "I will edit the file.", TimeSpan.Zero)],
            _ => [new ToolCallCompleted("t2", "write", "code.edit_file", "{}", false, TimeSpan.Zero),
                new TurnCompleted("t2", "The file was edited.", TimeSpan.Zero)],
        ]);
        var events = await Collect(h.Orchestrator, Request("openrouter") with
        {
            UserMessage = "Implement the code changes in the repository.",
            RequireWorkspaceChange = true,
            SelectionPinned = true,
        });

        Assert.Equal(2, h.Seen.Count);
        Assert.Contains(events, e => e is CodingAcceptanceEvaluated);
        Assert.DoesNotContain(events, e => e is TurnErrored);
    }

    [Fact]
    public async Task Refusing_reply_rewrite_owns_the_conversations_execution_gate()
    {
        var h = Build([_ => [new TurnCompleted("t1", "I cannot help with this request.", TimeSpan.Zero)],
            _ => [new TurnCompleted("t2", "Done.", TimeSpan.Zero)]]);
        await Collect(h.Orchestrator, Request());
        Assert.Single(h.Rewriter.Calls);
        Assert.True(h.Rewriter.OwnedDuringRewrite);
    }

    private sealed class BlockingAfterRefusal(AgentConversationExecutionGate gate) : IAgentTurnRunner, IAsyncDisposable
    {
        private IAsyncDisposable? _lease;
        public async IAsyncEnumerable<TurnEvent> RunAsync(AgentTurnRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new TurnCompleted("refused", "I cannot help with this request.", TimeSpan.Zero);
            _lease = await gate.AcquireAsync(request.ConversationId, ct);
        }
        public async ValueTask DisposeAsync() { if (_lease is not null) await _lease.DisposeAsync(); }
    }

    [Fact]
    public async Task Rewrite_wait_respects_request_deadline_without_cancelling_the_caller()
    {
        var gate = new AgentConversationExecutionGate();
        await using var runner = new BlockingAfterRefusal(gate);
        var services = new ServiceCollection();
        services.AddSingleton(gate);
        services.AddSingleton<IAgentTurnRunner>(runner);
        var rewriter = new FakeRewriter(gate);
        services.AddSingleton<ITranscriptRewriter>(rewriter);
        var options = new CodingOptions();
        options.ExecutionLimits = new() { Timeout = TimeSpan.FromMilliseconds(250) };
        var orchestrator = TestOrchestrator.Create(services, new CodingLaneOptions {
            Lanes = [new("api", "OpenRouter", "model")] }, options);
        var events = await Collect(orchestrator, Request()).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(rewriter.Calls);
        Assert.Contains(events, e => e is CodingTurnLimitReached);
    }

    [Fact]
    public async Task Typed_content_policy_failure_hands_off_and_neutralizes_nothing_when_no_text()
    {
        var h = Build(
        [
            _ => [new TurnErrored("t1", "Claude declined this request (content policy stop_reason: refusal).") { FailureCategory = LlmFailureCategory.ContentPolicy }],
            _ => [new TurnCompleted("t2", "Done.", TimeSpan.Zero)],
        ]);
        var events = await Collect(h.Orchestrator, Request());

        var handoff = Assert.Single(events.OfType<CodingLaneHandOff>());
        Assert.Equal(LaneOutcomeKind.Refused, handoff.Outcome);
        Assert.DoesNotContain("policy", handoff.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("policy", h.Seen[1].UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[Hand-off]", h.Seen[1].UserMessage);
        Assert.Empty(h.Rewriter.Calls);
    }

    [Fact]
    public async Task Decline_tool_with_partial_scope_hands_off_only_the_remaining_work()
    {
        var declineInput = new JsonObject { ["scope"] = "part", ["reason"] = "out of my depth", ["remaining"] = "write the integration test for the parser" };
        var h = Build(
        [
            _ =>
            [
                new ToolCallCompleted("t1", "e1", "code.edit_file", "{}", false, TimeSpan.Zero),
                new ToolCallStarted("t1", "d1", HarnessDeclineTool.ToolId, declineInput),
                new ToolCallCompleted("t1", "d1", HarnessDeclineTool.ToolId, "{}", false, TimeSpan.Zero),
                new TurnCompleted("t1", "I updated the parser. The test remains.", TimeSpan.Zero),
            ],
            _ => [new TurnCompleted("t2", "Test added.", TimeSpan.Zero)],
        ]);
        var events = await Collect(h.Orchestrator, Request());

        var handoff = Assert.Single(events.OfType<CodingLaneHandOff>());
        Assert.Equal(LaneOutcomeKind.PartialRefusal, handoff.Outcome);
        Assert.Equal(2, h.Seen.Count);
        Assert.Contains("Remaining work: write the integration test for the parser", h.Seen[1].UserMessage);
        Assert.Contains("work already completed", h.Seen[1].UserMessage);
        // The completed turn's text is neutralized so the next lane does not read a decline verbatim.
        var call = Assert.Single(h.Rewriter.Calls);
        Assert.Equal("I updated the parser. The test remains.", call.Match);
        Assert.Equal(CodingTurnOrchestrator.NeutralStopNote, call.Replacement);
    }

    [Fact]
    public async Task Unavailable_route_cools_the_lane_down_and_the_next_turn_skips_it()
    {
        var h = Build(
        [
            _ => [new TurnErrored("t1", "ClaudeCode CLI executable was not found.") { FailureCategory = LlmFailureCategory.ModelNotFound }],
            _ => [new TurnCompleted("t2", "ok", TimeSpan.Zero)],
            _ => [new TurnCompleted("t3", "ok again", TimeSpan.Zero)],
        ]);

        var first = await Collect(h.Orchestrator, Request());
        Assert.Single(first.OfType<CodingLaneHandOff>());
        Assert.True(h.Health.IsUnavailable("claude", out var reason));
        Assert.Contains("not found", reason);

        var second = await Collect(h.Orchestrator, Request());
        var skipped = Assert.Single(second.OfType<CodingLaneSkipped>());
        Assert.Equal("claude", skipped.Lane.Name);
        Assert.Equal("CodexCli", h.Seen[2].ProviderOverride);
        Assert.Empty(second.OfType<CodingLaneHandOff>());

        // After the cooldown the lane is tried again.
        h.Time.Now += TimeSpan.FromMinutes(11);
        Assert.False(h.Health.IsUnavailable("claude", out _));
    }

    [Fact]
    public async Task Explicitly_named_lane_is_tried_even_while_cooling_down()
    {
        var h = Build([_ => [new TurnCompleted("t1", "ok", TimeSpan.Zero)]]);
        h.Health.MarkUnavailable("claude", "quota", TimeSpan.FromMinutes(5));
        await Collect(h.Orchestrator, Request("claude"));
        Assert.Equal("ClaudeCode", Assert.Single(h.Seen).ProviderOverride);
        Assert.False(h.Health.IsUnavailable("claude", out _)); // success clears the mark
    }

    [Theory]
    [InlineData(RepeatedToolCallGuard.BlockMarker)]
    [InlineData(RepeatedToolCallGuard.RecoveryMarker)]
    public async Task Repeated_blocked_calls_stop_the_lane_and_hand_off_as_stuck(string marker)
    {
        var blocked = $"Blocked by safety guard: {marker}: shell.exec has already failed 3 times";
        var h = Build(
        [
            _ =>
            [
                new ToolCallCompleted("t1", "s1", "shell.exec", blocked, true, TimeSpan.Zero),
                new ToolCallCompleted("t1", "s2", "shell.exec", blocked, true, TimeSpan.Zero),
                new TurnCompleted("t1", "I kept trying.", TimeSpan.Zero),
            ],
            _ => [new TurnCompleted("t2", "Fixed with a different approach.", TimeSpan.Zero)],
        ]);
        var events = await Collect(h.Orchestrator, Request());

        var handoff = Assert.Single(events.OfType<CodingLaneHandOff>());
        Assert.Equal(LaneOutcomeKind.Stuck, handoff.Outcome);
        Assert.Contains("repeating", handoff.Reason);
        Assert.Equal(2, h.Seen.Count);
    }

    [Fact]
    public async Task Timeout_is_exhausted_not_route_unavailable_and_does_not_cool_the_lane()
    {
        var h = Build(
        [
            _ => [new TurnErrored("t1", "The agent turn exceeded its configured time budget before completing.") { FailureCategory = LlmFailureCategory.Timeout }],
            _ => [new TurnCompleted("t2", "ok", TimeSpan.Zero)],
        ]);
        var events = await Collect(h.Orchestrator, Request());
        var handoff = Assert.Single(events.OfType<CodingLaneHandOff>());
        Assert.Equal(LaneOutcomeKind.Exhausted, handoff.Outcome);
        Assert.False(h.Health.IsUnavailable("claude", out _));
    }

    [Fact]
    public void Classifier_prefers_typed_signals_over_text()
    {
        Assert.Equal(LaneOutcomeKind.Refused, RefusalDetector.Classify("All done, everything passes.", null, false, LlmFailureCategory.ContentPolicy, null, false));
        Assert.Equal(LaneOutcomeKind.PartialRefusal, RefusalDetector.Classify("All done.", null, true, LlmFailureCategory.ContentPolicy, null, false));
        Assert.Equal(LaneOutcomeKind.RouteUnavailable, RefusalDetector.Classify(null, "x", false, LlmFailureCategory.RateLimit, null, false));
        Assert.Equal(LaneOutcomeKind.Exhausted, RefusalDetector.Classify(null, "x", false, LlmFailureCategory.ContextOverflow, null, false));
        Assert.Equal(LaneOutcomeKind.Stuck, RefusalDetector.Classify("Done.", null, true, null, null, stuck: true));
        Assert.Equal(LaneOutcomeKind.PartialRefusal, RefusalDetector.Classify("Done.", null, false, null, new DeclineSignal("part", "r", "rest"), false));
        Assert.Equal(LaneOutcomeKind.Refused, RefusalDetector.Classify("Done.", null, false, null, new DeclineSignal("all", "r", null), false));
        Assert.Equal(LaneOutcomeKind.Exhausted, RefusalDetector.ClassifyError("Agent turn exceeded hard timeout of 00:30:00."));
        Assert.Equal(LaneOutcomeKind.Delivered, RefusalDetector.Classify("Built and tested; all 12 tests pass.", null, true, null, null, false));
    }

    [Fact]
    public void Hand_off_message_is_neutral_and_native_lanes_are_told_to_check_git()
    {
        var api = new CodingLane("codex", "CodexCli", "codex-cli:default");
        var native = new CodingLane("claude-native", "ClaudeCodeNative", "claude-code:default");
        var toApi = CodingTurnOrchestrator.BuildHandOffMessage("do X", api, api, LaneOutcomeKind.Refused, mutated: false, remaining: null);
        var toNative = CodingTurnOrchestrator.BuildHandOffMessage("do X", api, native, LaneOutcomeKind.PartialRefusal, mutated: true, remaining: null);
        Assert.DoesNotContain("policy", toApi, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refus", toApi, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("harness.decline", toApi);
        Assert.Contains("git status", toNative);
        Assert.Contains("Finish only what is still undone", toNative);
        Assert.DoesNotContain("already changed files", toNative);
        Assert.Contains("Inspect their receipts", toNative);
        Assert.Contains("Prior model assumptions are not user requirements or verified facts", toApi);
        Assert.EndsWith("Original request:\ndo X", toApi);
        Assert.True(native.IsNative);
        Assert.False(api.IsNative);
    }

    [Fact]
    public void Decline_tool_parses_model_input_leniently()
    {
        var signal = HarnessDeclineTool.FromInput(new JsonObject { ["Scope"] = "Partial", ["Reason"] = "nope", ["Remaining"] = "  the rest  " });
        Assert.NotNull(signal);
        Assert.True(signal!.IsPartial);
        Assert.Equal("the rest", signal.Remaining);
        Assert.Equal("all", HarnessDeclineTool.Parse(null));
        Assert.Null(HarnessDeclineTool.FromInput(null));
    }
}
