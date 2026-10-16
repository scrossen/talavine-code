using System.Runtime.CompilerServices;
using SwarmCode.Ui;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Routing;

namespace SwarmCode.Tests;

public sealed class ConsoleAcceptanceTests
{
    private sealed class ScriptedOrchestrator(params TurnEvent[] events) : ICodingTurnOrchestrator
    {
        public IReadOnlyList<CodingLane> Lanes => [];
        public CodingLane? ResolveLane(string? name) => null;
        public async IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var evt in events) { ct.ThrowIfCancellationRequested(); await Task.Yield(); yield return evt; }
        }
    }

    private sealed class RecordingOrchestrator : ICodingTurnOrchestrator
    {
        public CodingTurnRequest? Request { get; private set; }
        public IReadOnlyList<CodingLane> Lanes => [];
        public CodingLane? ResolveLane(string? name) => null;
        public async IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Request = request;
            await Task.Yield();
            yield return new TurnCompleted("job", "done", TimeSpan.Zero);
        }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("claude", true)]
    [InlineData("claude", false)]
    public async Task Lane_selects_the_start_and_handoff_is_a_separate_preference(string? lane, bool allowHandOff)
    {
        var renderer = new ConsoleRenderer(new() { Quiet = true }, new StringWriter(), new StringWriter());
        var orchestrator = new RecordingOrchestrator();
        await new TurnRunner(orchestrator, renderer, allowHandOff).RunAsync("conversation", "task", lane, default);
        Assert.Equal(lane, orchestrator.Request!.LaneName);
        Assert.False(orchestrator.Request!.SelectionPinned);
        Assert.Equal(allowHandOff, CodingRoutingPolicy.CanHandOff(orchestrator.Request, configured: true));
    }

    private static CodingAcceptanceEvaluated Acceptance(CodingAcceptanceStatus status) => new("job", new(status, status.ToString(), []));
    private static CodingLaneSelected Selected() => new("job", new CodingLane("fixture", "fixture", "fixture"), 1);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_acceptance_cannot_return_completed_after_model_answer(bool json)
    {
        using var stdout = new StringWriter();
        using var diagnostics = new StringWriter();
        var renderer = new ConsoleRenderer(new() { Quiet = true, Json = json }, stdout, diagnostics);
        var runner = new TurnRunner(new ScriptedOrchestrator(Selected(), new TurnCompleted("lane", "candidate says done", TimeSpan.Zero),
            Acceptance(CodingAcceptanceStatus.Failed), new TurnErrored("job", "Task completion failed host acceptance")), renderer, false);
        Assert.Equal(TurnOutcome.Errored, await runner.RunAsync("conversation", "task", null, default));
        if (!json)
        {
            Assert.Empty(stdout.ToString());
            Assert.Contains("acceptance failed", diagnostics.ToString());
        }
        else Assert.Contains("CodingAcceptanceEvaluated", stdout.ToString());
    }

    [Fact]
    public async Task Repair_success_emits_only_final_accepted_answer()
    {
        using var stdout = new StringWriter();
        using var diagnostics = new StringWriter();
        var renderer = new ConsoleRenderer(new() { Quiet = true }, stdout, diagnostics);
        var runner = new TurnRunner(new ScriptedOrchestrator(Selected(), new TurnCompleted("first", "broken candidate", TimeSpan.Zero),
            Acceptance(CodingAcceptanceStatus.Failed), Selected(), new TurnCompleted("second", "fixed answer", TimeSpan.Zero),
            Acceptance(CodingAcceptanceStatus.Passed)), renderer, false);
        Assert.Equal(TurnOutcome.Completed, await runner.RunAsync("conversation", "task", null, default));
        Assert.Equal("fixed answer" + Environment.NewLine, stdout.ToString());
        Assert.Contains("acceptance failed", diagnostics.ToString());
        Assert.Contains("acceptance passed", diagnostics.ToString());
    }

    [Fact]
    public async Task Limit_after_candidate_never_returns_completed()
    {
        using var stdout = new StringWriter();
        using var diagnostics = new StringWriter();
        var renderer = new ConsoleRenderer(new() { Quiet = true }, stdout, diagnostics);
        var runner = new TurnRunner(new ScriptedOrchestrator(Selected(), new TurnCompleted("lane", "candidate", TimeSpan.Zero),
            new CodingTurnLimitReached("job", "Token budget reached")), renderer, false);
        Assert.Equal(TurnOutcome.Errored, await runner.RunAsync("conversation", "task", null, default));
        Assert.Empty(stdout.ToString());
        Assert.Contains("Completion is unverified", diagnostics.ToString());
    }

    [Fact]
    public async Task Unverified_answer_is_explicitly_labelled()
    {
        using var stdout = new StringWriter();
        using var diagnostics = new StringWriter();
        var renderer = new ConsoleRenderer(new() { Quiet = true }, stdout, diagnostics);
        var runner = new TurnRunner(new ScriptedOrchestrator(Selected(), new TurnCompleted("lane", "answer", TimeSpan.Zero),
            new CodingAcceptanceEvaluated("job", CodingAcceptanceResult.Unverified)), renderer, false);
        Assert.Equal(TurnOutcome.Completed, await runner.RunAsync("conversation", "task", null, default));
        Assert.Equal("answer" + Environment.NewLine, stdout.ToString());
        Assert.Contains("completion unverified", diagnostics.ToString());
    }

    [Fact]
    public async Task Candidate_without_acceptance_event_is_not_terminal_success()
    {
        using var stdout = new StringWriter();
        var renderer = new ConsoleRenderer(new() { Quiet = true }, stdout, new StringWriter());
        var runner = new TurnRunner(new ScriptedOrchestrator(new TurnCompleted("lane", "candidate", TimeSpan.Zero)), renderer, false);
        Assert.Equal(TurnOutcome.Errored, await runner.RunAsync("conversation", "task", null, default));
        Assert.Empty(stdout.ToString());
    }
}
