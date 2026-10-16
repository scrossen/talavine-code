using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Routing;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.Coding.Tests;

public sealed class ModelResponseObservationTests
{
    private sealed class Memory : IModelResponseMemory
    {
        public List<ModelResponseObservation> Rows = [];
        public bool FailWrites;
        public Task AppendAsync(ModelResponseObservation row, CancellationToken ct = default)
        {
            if (FailWrites) throw new IOException("History unavailable");
            Rows.Add(row); return Task.CompletedTask;
        }
        public Task<IReadOnlyList<ModelResponseObservation>> ReadAsync(string scopeId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ModelResponseObservation>>(Rows);
    }
    private sealed class Runner(string answer, bool error) : IAgentTurnRunner
    {
        public int Calls;
        public async IAsyncEnumerable<TurnEvent> RunAsync(AgentTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Calls++;
            await Task.Yield();
            yield return new TurnUsageReported("t", "OpenRouter", "family/resolved-v2", 10, 5, 0, null);
            if (error) yield return new TurnErrored("t", "Unauthorized account") { FailureCategory = LlmFailureCategory.AuthInvalid };
            else yield return new TurnCompleted("t", answer, TimeSpan.Zero);
        }
    }

    [Fact]
    public async Task History_write_failure_is_visible_and_never_reexecutes_the_completed_action()
    {
        var memory = new Memory { FailWrites = true };
        var runner = new Runner("answer", false);
        var services = new ServiceCollection();
        services.AddSingleton<IModelResponseMemory>(memory);
        services.AddSingleton<IAgentTurnRunner>(runner);
        var orchestrator = TestOrchestrator.Create(services, new CodingLaneOptions
        { Lanes = [new("router", "OpenRouter", "family/model")], MaxLanesPerRequest = 1 }, new());
        var events = new List<TurnEvent>();
        await foreach (var evt in orchestrator.RunAsync(new() { ConversationId = "c", UserMessage = "ordinary request",
            CommunicationScope = AgentCommunicationScope.UserRequested("test") })) events.Add(evt);
        Assert.Equal(1, runner.Calls);
        Assert.Empty(memory.Rows);
        Assert.Contains(events.OfType<CodingLaneNotice>(), e => e.Message.Contains("history could not be saved"));
        Assert.Contains(events, e => e is CodingAcceptanceEvaluated);
    }
    private sealed class Verifier(CodingAcceptanceStatus status) : ICodingAcceptanceVerifier
    {
        public Task<CodingAcceptanceResult> VerifyAsync(CodingAcceptanceContext context, CancellationToken ct = default)
            => Task.FromResult(new CodingAcceptanceResult(status, "host check", []));
    }

    [Theory]
    [InlineData(CodingAcceptanceStatus.Unverified, false, false, true, ModelResponseOutcome.AnsweredUnverified)]
    [InlineData(CodingAcceptanceStatus.Passed, false, false, true, ModelResponseOutcome.Verified)]
    [InlineData(CodingAcceptanceStatus.Failed, false, false, true, ModelResponseOutcome.AcceptanceFailed)]
    [InlineData(CodingAcceptanceStatus.Unverified, true, false, true, ModelResponseOutcome.Declined)]
    [InlineData(CodingAcceptanceStatus.Unverified, false, true, true, ModelResponseOutcome.RuntimeFailed)]
    [InlineData(CodingAcceptanceStatus.Passed, false, false, false, ModelResponseOutcome.Verified)]
    public async Task Observations_keep_actual_model_identity_and_host_acceptance_distinct_and_private_turns_write_nothing(
        CodingAcceptanceStatus status, bool refuse, bool error, bool remember, ModelResponseOutcome expected)
    {
        var memory = new Memory();
        var services = new ServiceCollection();
        services.AddSingleton<IModelResponseMemory>(memory);
        services.AddSingleton<IAgentTurnRunner>(new Runner(refuse ? "I won't do that." : "answer without proof", error));
        services.AddSingleton<ICodingAcceptanceVerifier>(new Verifier(status));
        var options = new CodingOptions(); options.Acceptance.MaxRepairAttempts = 0;
        var orchestrator = TestOrchestrator.Create(services, new CodingLaneOptions
        { Lanes = [new("router", "OpenRouter", "openrouter/auto")], MaxLanesPerRequest = 1 }, options);
        await foreach (var _ in orchestrator.RunAsync(new() { ConversationId = "c", UserMessage = "private original request",
            RememberModelResponses = remember, CommunicationScope = AgentCommunicationScope.UserRequested("test") })) { }
        if (!remember) { Assert.Empty(memory.Rows); return; }
        var row = Assert.Single(memory.Rows);
        Assert.Equal("family/resolved-v2", row.Model);
        Assert.Equal(expected, row.Outcome);
        Assert.Equal(ModelResponseLearning.Fingerprint("private original request"), row.TaskFingerprint);
        Assert.DoesNotContain("private original request", System.Text.Json.JsonSerializer.Serialize(row));
    }
}
