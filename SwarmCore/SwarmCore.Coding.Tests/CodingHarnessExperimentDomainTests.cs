using System.Runtime.CompilerServices;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Experiments;
using SwarmCore.Coding.Routing;
using SwarmCore.HarnessLearning;

namespace SwarmCore.Coding.Tests;

public sealed class CodingHarnessExperimentDomainTests
{
    private sealed class Orchestrator(IReadOnlyList<TurnEvent> events) : ICodingTurnOrchestrator
    {
        public int Runs { get; private set; }
        public IReadOnlyList<CodingLane> Lanes => [];
        public CodingLane? ResolveLane(string? name) => null;
        public async IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Runs++;
            foreach (var evt in events) { ct.ThrowIfCancellationRequested(); yield return evt; }
            await Task.CompletedTask;
        }
    }

    private sealed class Trial(IReadOnlyList<TurnEvent> events) : ICodingHarnessExperimentTrial
    {
        public Orchestrator Runner { get; } = new(events);
        public ICodingTurnOrchestrator Orchestrator => Runner;
        public string Provider => "test";
        public string Model => "model";
        public bool Disposed { get; private set; }
        public CodingTurnRequest Request { get; init; } = new()
        {
            ConversationId = "fresh", UserMessage = "fixture", SelectionPinned = true,
            AllowHandOff = false, DisableRouteFallback = true, RememberModelResponses = false,
            ExecutionLimits = new() { MaxCumulativeTokens = 1000 }
        };
        public Task<HarnessTrialObservation> VerifyAsync(CancellationToken ct)
            => Task.FromResult(new HarnessTrialObservation(1, true, "host", HarnessEvidence.Hash("receipt"), null));
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private static HarnessTrialRequest Request() => new(
        new("profile", "coding", HarnessEvidence.Hash("environment"), HarnessEvidence.Hash("suite")),
        new("base", "current", new Dictionary<string, string>()), new("case", HarnessTaskSplit.Development), 0, 1000);

    [Fact]
    public async Task Adapter_uses_real_turn_stream_usage_and_independent_host_verification()
    {
        var trial = new Trial([new CodingLaneSelected("turn", new("test", "test", "model"), 1),
            new TurnUsageReported("turn", "test", "model", 100, 20), new TurnCompleted("turn", "untrusted prose", TimeSpan.Zero)]);
        var result = await new CodingHarnessExperimentDomain((_, _) => Task.FromResult<ICodingHarnessExperimentTrial>(trial)).RunAsync(Request(), default);
        Assert.Equal(1, result.Score);
        Assert.Equal(120, result.Tokens);
        Assert.True(result.InvariantsPassed);
        Assert.True(trial.Disposed);
    }

    [Fact]
    public async Task A_foreign_route_cannot_pass_even_when_its_artifact_is_correct()
    {
        var trial = new Trial([new TurnUsageReported("turn", "other-provider", "model", 100, 20), new TurnCompleted("turn", "done", TimeSpan.Zero)]);
        var result = await new CodingHarnessExperimentDomain((_, _) => Task.FromResult<ICodingHarnessExperimentTrial>(trial)).RunAsync(Request(), default);
        Assert.False(result.InvariantsPassed);
    }

    [Fact]
    public async Task Model_prose_cannot_complete_an_errored_turn_and_absent_usage_is_unknown()
    {
        var trial = new Trial([new TurnCompleted("turn", "done", TimeSpan.Zero), new TurnErrored("turn", "failed")]);
        var result = await new CodingHarnessExperimentDomain((_, _) => Task.FromResult<ICodingHarnessExperimentTrial>(trial)).RunAsync(Request(), default);
        Assert.Equal(0, result.Score);
        Assert.Null(result.Tokens);
    }

    [Fact]
    public async Task Unbounded_or_memory_reading_turns_are_rejected_before_dispatch()
    {
        var trial = new Trial([]);
        trial = new Trial([]) { Request = trial.Request with { RememberModelResponses = true } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CodingHarnessExperimentDomain((_, _) => Task.FromResult<ICodingHarnessExperimentTrial>(trial)).RunAsync(Request(), default));
        Assert.Equal(0, trial.Runner.Runs);
        Assert.True(trial.Disposed);
    }
}
