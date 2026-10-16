using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Routing;

namespace SwarmCore.Coding.Tests;

public sealed class CodingRoutingPolicyTests
{
    private sealed class Runner(bool refuse = false) : IAgentTurnRunner
    {
        public List<AgentTurnRequest> Seen { get; } = [];
        public async IAsyncEnumerable<TurnEvent> RunAsync(AgentTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Seen.Add(request); await Task.Yield();
            if (refuse) yield return new TurnCompleted("t", "I cannot assist with that request.", TimeSpan.Zero);
            else yield return new TurnErrored("t", "HTTP 429 rate limit exceeded");
        }
    }

    private static async Task<List<TurnEvent>> Run(IAgentTurnRunner runner, CodingTurnRequest request, params CodingLane[] lanes)
    {
        var services = new ServiceCollection(); services.AddSingleton<IAgentTurnRunner>(runner);
        var orchestrator = TestOrchestrator.Create(services, new() { Lanes = lanes.ToList(), MaxLanesPerRequest = 8 }, new(), out var provider);
        using (provider)
        {
            var events = new List<TurnEvent>();
            await foreach (var evt in orchestrator.RunAsync(request)) events.Add(evt);
            return events;
        }
    }

    private static CodingTurnRequest Request => new() { ConversationId = "routing", UserMessage = "hello", StopOnRefusal = true };

    [Fact]
    public async Task UnavailableRoutesNeverRepeatUnderAnotherLaneName()
    {
        var runner = new Runner();
        var events = await Run(runner, Request,
            new("first", "OpenRouter", "family/model"),
            new("alias", "openrouter", "family/model") { Description = "Same route" },
            new("local", "Ollama", "local-model"));
        Assert.Equal(new[] { "OpenRouter", "Ollama" }, runner.Seen.Select(r => r.ProviderOverride));
        Assert.Single(events.OfType<CodingLaneHandOff>());
        Assert.Single(events.OfType<CodingLanesExhausted>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExplicitChoiceNeverSwitchesEvenWhenHandoffIsEnabled(bool refuse)
    {
        var runner = new Runner(refuse);
        var events = await Run(runner, Request with { LaneName = "local", ModelOverride = "user-model", SelectionPinned = true, AllowHandOff = true },
            new("local", "Ollama", "default-model"), new("hosted", "OpenRouter", "other"));
        Assert.Equal("user-model", Assert.Single(runner.Seen).ModelOverride);
        Assert.True(runner.Seen[0].DisableRouteFallback);
        Assert.Empty(events.OfType<CodingLaneHandOff>());
    }

    /// <summary>Refuses on the named lanes, completes elsewhere.</summary>
    private sealed class SelectiveRunner(params string[] refusingProviders) : IAgentTurnRunner
    {
        public List<AgentTurnRequest> Seen { get; } = [];
        public async IAsyncEnumerable<TurnEvent> RunAsync(AgentTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Seen.Add(request); await Task.Yield();
            yield return refusingProviders.Contains(request.ProviderOverride, StringComparer.OrdinalIgnoreCase)
                ? new TurnCompleted("t", "I'm sorry, but I can't help with that request.", TimeSpan.Zero)
                : new TurnCompleted("t", "Here is the complete answer.", TimeSpan.Zero);
        }
    }

    [Fact]
    public async Task WithoutStopOnRefusalADeclineHandsOffToTheDeclinePreferredLaneFirst()
    {
        var runner = new SelectiveRunner("OpenRouter");
        var events = await Run(runner, Request with { StopOnRefusal = false, DisableRouteFallback = true },
            new("first", "OpenRouter", "one"), new("local", "Ollama", "two"), new("open", "Venice", "uncensored") { PreferOnDecline = true });
        // The decline-preferred lane is tried before the lane order would reach it, and the inner router stays pinned to each lane's route.
        Assert.Equal(new[] { "OpenRouter", "Venice" }, runner.Seen.Select(r => r.ProviderOverride));
        Assert.All(runner.Seen, r => Assert.True(r.DisableRouteFallback));
        var handOff = Assert.Single(events.OfType<CodingLaneHandOff>());
        Assert.Equal(LaneOutcomeKind.Refused, handOff.Outcome);
        Assert.Equal("open", handOff.To.Name);
        Assert.Empty(events.OfType<CodingLanesExhausted>());
        Assert.Contains(events.OfType<TurnCompleted>(), t => t.FinalText == "Here is the complete answer.");
    }

    [Fact]
    public async Task ARouteFailureKeepsTheOrdinaryLaneOrder()
    {
        var runner = new Runner(); // 429 on every lane: unavailable, not a decline
        var events = await Run(runner, Request with { StopOnRefusal = false },
            new("first", "OpenRouter", "one"), new("local", "Ollama", "two"), new("open", "Venice", "uncensored") { PreferOnDecline = true });
        Assert.Equal(new[] { "OpenRouter", "Ollama", "Venice" }, runner.Seen.Select(r => r.ProviderOverride));
        Assert.Single(events.OfType<CodingLanesExhausted>());
    }

    [Fact]
    public void HandOffCandidatesPutDeclinePreferredLanesFirstOnlyAfterADecline()
    {
        CodingLane a = new("a", "P", "1"), b = new("b", "Q", "2") { PreferOnDecline = true }, c = new("c", "R", "3"), d = new("d", "S", "4") { PreferOnDecline = true };
        Assert.Equal(["b", "d", "c"], CodingRoutingPolicy.HandOffCandidates([a, b, c, d], [a], LaneOutcomeKind.Refused).Select(l => l.Name));
        Assert.Equal(["b", "d", "c"], CodingRoutingPolicy.HandOffCandidates([a, b, c, d], [a], LaneOutcomeKind.PartialRefusal).Select(l => l.Name));
        Assert.Equal(["b", "c", "d"], CodingRoutingPolicy.HandOffCandidates([a, b, c, d], [a], LaneOutcomeKind.RouteUnavailable).Select(l => l.Name));
        Assert.Equal(["b", "c", "d"], CodingRoutingPolicy.HandOffCandidates([a, b, c, d], [a], LaneOutcomeKind.Failed).Select(l => l.Name));
    }

    [Fact]
    public async Task RefusalStopsBeforeAnyOtherProviderAndDoesNotRequestReassessment()
    {
        var runner = new Runner(true);
        var events = await Run(runner, Request, new("first", "OpenRouter", "one"), new("local", "Ollama", "two"));
        Assert.Single(runner.Seen);
        Assert.False(Assert.Single(events.OfType<CodingLanesExhausted>()).ReassessModel);
        Assert.Empty(events.OfType<CodingLaneHandOff>());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("native")]
    public async Task MissingOrBlockedPinnedChoiceDoesNotSubstituteTheDefault(string name)
    {
        var runner = new Runner();
        var events = await Run(runner, Request with { LaneName = name, SelectionPinned = true, AllowNativeLanes = false },
            new("local", "Ollama", "one"), new("native", "CodexNative", "two"));
        Assert.Empty(runner.Seen);
        Assert.Single(events.OfType<CodingLanesExhausted>());
    }

    [Fact]
    public async Task ModelOverrideCannotRevisitItsOriginalLane()
    {
        var runner = new Runner();
        await Run(runner, Request with { LaneName = "first", ModelOverride = "override" },
            new("first", "OpenRouter", "default"), new("second", "Ollama", "local"));
        Assert.Equal(new[] { "override", "local" }, runner.Seen.Select(r => r.ModelOverride));
    }
}
