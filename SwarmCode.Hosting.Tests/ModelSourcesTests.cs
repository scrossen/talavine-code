using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Coding.Routing;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.ToolUse;

namespace SwarmCode.Hosting.Tests;

public sealed class ModelSourcesTests : TempDirTest
{
    [Fact]
    public void NamedSourceKeyReadsIgnoredEnvWithoutApplyingItToOtherProviders()
    {
        var source = new ConfiguredModelSource { Name="venice", Kind="Compatible", Endpoint="https://example.test/v1", Model="model", ApiKeyEnvironmentVariable="VENICE_TEST_KEY" };
        File.WriteAllText(Path.Combine(Tmp,".env"), "# local credentials\nVENICE_TEST_KEY=fixture-secret\n");
        var lookup = new ModelSourceEnvironmentKeys(Tmp, [source]);
        Assert.Equal("fixture-secret", lookup.Get(source.Provider));
        Assert.Null(lookup.Get("OpenRouter"));
    }

    [Fact]
    public async Task ComposedProviderUsesNamedEnvFileKeyForConfiguredSource()
    {
        var source = new ConfiguredModelSource { Name="venice", Kind="Compatible", Endpoint="https://example.test/v1", Model="model", ApiKeyEnvironmentVariable="VENICE_TEST_KEY" };
        File.WriteAllText(Path.Combine(Tmp,".env"), "VENICE_TEST_KEY=fixture-secret\n");
        await new ModelSourcesStore(Paths).SaveAsync(new(0,[source]));
        var services = new ServiceCollection(); services.AddLogging();
        services.AddSwarmCodeHost(new() { DataPaths=Paths, RepositoryRoot=Tmp });
        using var provider = services.BuildServiceProvider();
        Assert.Equal("fixture-secret", await provider.GetRequiredService<SwarmCodeApiKeyProvider>().GetApiKeyAsync(source.Provider));
    }

    private static ConfiguredModelSource Local() => new() {Name="my-local",Kind="Ollama",Endpoint="http://localhost:11434",Model="community/custom:latest",RequiresApiKey=false};
    [Fact]
    public async Task IndependentWritersAndDeleteThenSaveRejectStaleDrafts()
    {
        var a=new ModelSourcesStore(Paths); var b=new ModelSourcesStore(Paths);
        await a.SaveAsync(new(0,[Local()]));
        var stale=a.Read();
        await b.SaveAsync(b.Read() with { Sources=[] });
        await Assert.ThrowsAsync<InvalidOperationException>(()=>a.SaveAsync(stale));
        Assert.Empty(new ModelSourcesStore(Paths).Read().Sources);
        var current=b.Read();
        await b.SaveAsync(current with {Sources=[Local() with {Enabled=false}]});
        Assert.False(new ModelSourcesStore(Paths).Read().Sources[0].Enabled);
    }

    [Fact]
    public async Task CorruptSourcesAreNeverReplaced()
    {
        var path=Path.Combine(Paths.RootDirectory,"model-sources.json");
        File.WriteAllText(path,"{broken");
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(()=>new ModelSourcesStore(Paths).SaveAsync(new(0,[])));
        Assert.Equal("{broken",File.ReadAllText(path));
    }

    [Fact]
    public async Task CompositionMakesAllSourcesSelectableWithMatchingToolAdaptersAndDecisionChoices()
    {
        var local=Local();
        var custom=new ConfiguredModelSource {Name="hosted",Kind="Compatible",Endpoint="https://example.test/v1",Model="community/model",RequiresApiKey=false};
        await new ModelSourcesStore(Paths).SaveAsync(new(0,[local,custom,
            new() {Name="router",Kind="OpenRouter",Model="family/model"},
            new() {Name="studio",Kind="LMStudio",Endpoint="http://localhost:1234/v1",Model="loaded-model",RequiresApiKey=false}]));
        var services=new ServiceCollection(); services.AddLogging();
        services.AddSwarmCodeHost(new() {DataPaths=Paths,RepositoryRoot=Tmp});
        using var provider=services.BuildServiceProvider(new ServiceProviderOptions {ValidateScopes=true});
        var lanes=provider.GetRequiredService<IOptions<CodingLaneOptions>>().Value.Lanes;
        foreach(var name in new[]{"my-local","hosted","router","studio"}) Assert.Contains(lanes,l=>l.Name==name);
        var tools=provider.GetRequiredService<IToolUseProviderRegistry>();
        Assert.Equal(custom.Provider,tools.Resolve(custom.Provider).ProviderName);
        Assert.Equal("Ollama",tools.Resolve("Ollama").ProviderName);
        Assert.Equal("LMStudio",tools.Resolve("LMStudio").ProviderName);
        Assert.Equal("OpenRouter",tools.Resolve("OpenRouter").ProviderName);
        Assert.Equal("Venice",tools.Resolve("Venice").ProviderName);
        Assert.Equal(local.Endpoint,provider.GetRequiredService<IOptions<OllamaProviderOptions>>().Value.Endpoint);
        using var scope=provider.CreateScope();
        var selected=await scope.ServiceProvider.GetRequiredService<ILlmRouter>().SelectOptimalModelAsync(FunctionTypes.AgentChat,"fixture",
            new LlmRouterOptions {OverrideProvider=custom.Provider,OverrideModel=custom.Model,OverrideProvenance=LlmOverrideProvenance.User,DisableFallback=true});
        Assert.Equal(custom.Provider,selected.Provider);
        Assert.Equal(custom.Model,selected.Model);
        // A custom provider with no pack must remain selectable without a catalog/network request.
        var choices=await CodingDecisionCandidates.SuggestAsync([lanes.Single(l=>l.Name=="hosted")],
            scope.ServiceProvider.GetRequiredService<CodingModelCatalog>(),[]);
        Assert.Equal(custom.Model,Assert.Single(choices).Model);
    }
}
