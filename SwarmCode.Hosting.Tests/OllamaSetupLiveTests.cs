using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwarmCore.Coding.Routing;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.LocalRuntime;

namespace SwarmCode.Hosting.Tests;

public sealed class OllamaSetupLiveFactAttribute : FactAttribute
{
    public OllamaSetupLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SWARMCODE_OLLAMA_SETUP_LIVE") != "1")
            Skip = "Set SWARMCODE_OLLAMA_SETUP_LIVE=1 to use the local Ollama server and public Hugging Face catalog.";
    }
}

public sealed class OllamaSetupLiveTests : TempDirTest
{
    [OllamaSetupLiveFact]
    public async Task InstalledModelLoadsAndCallsAToolAndIsAPreferredFallback()
    {
        using var services = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        var client = new OllamaSetupClient(services.GetRequiredService<IHttpClientFactory>());
        var model = Environment.GetEnvironmentVariable("SWARMCODE_OLLAMA_TEST_MODEL") ?? OpenModelDiscovery.Examples[0].Model;
        var inventory = await client.InventoryAsync(OllamaSetupClient.DefaultEndpoint);
        Assert.Contains(inventory.Models, m => m.Name == model && m.SupportsTools);
        var result = await client.TestAsync(OllamaSetupClient.DefaultEndpoint, model);
        Assert.True(result.Passed, result.Message);
        var source = new OllamaCodingLaneSource(Options.Create(new CodingLaneOptions { Lanes = [new("primary", "Cloud", "model")] }),
            Options.Create(new OllamaProviderOptions { Endpoint = OllamaSetupClient.DefaultEndpoint }), new(Paths), client, NullLogger<OllamaCodingLaneSource>.Instance);
        var lanes = (await source.GetLanesAsync("local-probe")).Lanes;
        Assert.Contains(lanes, l => l.Model == model && l.PreferOnDecline);
        Assert.Equal(model, CodingRoutingPolicy.HandOffCandidates(lanes, [lanes[0]], LaneOutcomeKind.Refused).First().Model);
    }

    [OllamaSetupLiveFact]
    public async Task BothStartingPointsHaveTheirExactQuantizationAndDiscoveryFindsCurrentCandidates()
    {
        using var services = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        var discovery = new OpenModelDiscovery(services.GetRequiredService<IHttpClientFactory>());
        foreach (var example in OpenModelDiscovery.Examples)
        {
            var repository = example.Model["hf.co/".Length..].Split(':')[0];
            var detail = await discovery.InspectAsync(repository);
            var download = Assert.Single(detail.Downloads, d => d.Model == example.Model);
            Assert.True(download.SizeBytes > 0);
            Assert.True(detail.Candidate.Classification.ReducedRefusal);
        }
        Assert.NotEmpty(await discovery.SearchAsync(newest: true));
    }
}
