using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Coding.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;
using SwarmCore.Agent.Runtime;

namespace SwarmCore.Coding.Tests;

public sealed class FallbackDiscoveryTests
{
    private sealed class Discovery : ICodingFallbackDiscovery
    {
        public int Calls;
        public Task<CodingFallbackDiscoveryResult> DiscoverAsync(IReadOnlyList<CodingLane> configured, IReadOnlyList<CodingLane> tried, CodingFallbackContext context, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new CodingFallbackDiscoveryResult([new("first-candidate", "Catalog", "family/a"), new("second-candidate", "Catalog", "family/b")]));
        }
    }

    private sealed class Runner(bool refuse) : IAgentTurnRunner
    {
        public readonly List<AgentTurnRequest> Requests = [];
        public async IAsyncEnumerable<TurnEvent> RunAsync(AgentTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            await Task.Yield();
            yield return new TurnCompleted("t", refuse && Requests.Count < 3 ? "I won't do that." : "The requested result.", TimeSpan.Zero);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Refusal_discovers_alternatives_before_usual_lanes_and_preserves_attempt_limit(bool refuse)
    {
        var discovery = new Discovery();
        var runner = new Runner(refuse);
        var services = new ServiceCollection();
        services.AddSingleton<IAgentTurnRunner>(runner);
        services.AddSingleton<ICodingFallbackDiscovery>(discovery);
        var orchestrator = TestOrchestrator.Create(services, new CodingLaneOptions
        {
            Lanes = [new("initial", "Initial", "original"), new("usual", "Usual", "default")], MaxLanesPerRequest = 3,
        }, new());
        var events = new List<TurnEvent>();
        await foreach (var evt in orchestrator.RunAsync(new() { ConversationId = "discovery", UserMessage = "original objective",
            CommunicationScope = AgentCommunicationScope.UserRequested("test-host") })) events.Add(evt);
        Assert.Equal(refuse ? 1 : 0, discovery.Calls);
        Assert.Equal(refuse ? new[] { "original", "family/a", "family/b" } : ["original"], runner.Requests.Select(r => r.ModelOverride));
        Assert.All(runner.Requests, request => Assert.Contains("original objective", request.UserMessage));
        Assert.DoesNotContain(runner.Requests, request => request.ProviderOverride == "Usual");
        Assert.Empty(events.OfType<CodingLanesExhausted>());
    }

    [Fact]
    public void Search_respects_metadata_prices_preferences_and_distinct_families()
    {
        LlmCatalogModel[] models =
        [
            new("alpha/one", 65536, true, .1m, .2m, false),
            new("alpha/two", 65536, true, .1m, .2m, false),
            new("beta/one", 65536, true, .15m, .3m, false),
            new("gamma/one", 65536, true, 0m, 0m, false),
            new("beta/expensive", 65536, true, 50m, 100m, false),
            new("beta/unknown-price", 65536, true, null, null, false),
            new("beta/no-tools", 65536, false, 0m, 0m, false),
            new("beta/small", 8000, true, 0m, 0m, false),
            new("beta/one:batch", 65536, true, 0m, 0m, false),
            new("~beta/latest", 65536, true, 0m, 0m, false),
        ];
        var policy = new CodingFallbackDiscoveryOptions { PreferredModelNamespaces = ["alpha", "beta"], MaxCandidates = 3 };
        var result = CatalogCodingFallbackDiscovery.SelectCandidates("Provider", models, [], policy);
        Assert.Equal(["alpha/one", "beta/one", "gamma/one"], result.Select(l => l.Model));
        Assert.All(result, lane => Assert.Equal("route:" + lane.Route, lane.HealthKey));
        var tried = CatalogCodingFallbackDiscovery.SelectCandidates("Provider", models, [result[0]], policy);
        Assert.DoesNotContain(tried, l => l.Model == "alpha/one");
        Assert.Equal(3, tried.Count);
    }

    [Fact]
    public void Unknown_metadata_does_not_become_false_or_free()
    {
        LlmCatalogModel[] models = [new("alpha/unknown", null, false, null, null, null)];
        Assert.Empty(CatalogCodingFallbackDiscovery.SelectCandidates("Provider", models, [], new()));
    }
}
