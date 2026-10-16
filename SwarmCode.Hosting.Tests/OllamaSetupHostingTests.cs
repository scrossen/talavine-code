using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwarmCore.Coding.Routing;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.LocalRuntime;

namespace SwarmCode.Hosting.Tests;

public sealed class OllamaSetupHostingTests : TempDirTest
{
    private const string Endpoint = "http://127.0.0.1:11434";
    private const string Name = "community/abliterated:latest";
    private sealed class Http : HttpMessageHandler, IHttpClientFactory
    {
        public string Tags { get; set; } = """{"models":[{"name":"community/abliterated:latest","digest":"d1","size":100,"capabilities":["tools"]}]}""";
        public bool Offline { get; set; }
        public HttpClient CreateClient(string name) => new(this, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Offline ? throw new HttpRequestException("Offline") : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(request.RequestUri!.AbsolutePath switch { "/api/version" => "{\"version\":\"1\"}", "/api/show" => "{\"capabilities\":[\"tools\"]}", _ => Tags }, Encoding.UTF8, "application/json") });
    }
    private OllamaCodingLaneSource Source(Http http, ModelSourcesStore store) => new(
        Options.Create(new CodingLaneOptions { Lanes = [new("cloud", "Cloud", "model")] }),
        Options.Create(new OllamaProviderOptions { Endpoint = Endpoint }), store, new(http), NullLogger<OllamaCodingLaneSource>.Instance);

    [Fact]
    public async Task FreshRequestDropsDeletedAndOfflineModelsAndHonorsDisable()
    {
        using var http = new Http(); var store = new ModelSourcesStore(Paths); var source = Source(http, store);
        Assert.True((await source.GetLanesAsync("c")).Lanes.Single(l => l.Provider == "Ollama").PreferOnDecline);
        http.Tags = "{\"models\":[]}";
        Assert.DoesNotContain((await source.GetLanesAsync("c")).Lanes, l => l.Provider == "Ollama");
        http.Offline = true;
        Assert.Single((await source.GetLanesAsync("c")).Lanes);
        http.Offline = false;
        await store.SaveAsync(store.Read() with { Sources = [new() { Kind="Ollama", Name="ollama", Endpoint=Endpoint, Model=Name, RequiresApiKey=false, Enabled=false }] });
        Assert.DoesNotContain((await source.GetLanesAsync("c")).Lanes, l => l.Provider == "Ollama");
    }

    [Fact]
    public async Task PreferencesSurviveRestartAndRejectCompetingAndDeleteThenSaveWriters()
    {
        var a = new ModelSourcesStore(Paths); var b = new ModelSourcesStore(Paths);
        var original = new LocalModelPreference(Endpoint, Name, "d1", LocalModelBehavior.Uncensored, false);
        await a.SaveAsync(new(0, []) { LocalModels = [original], PreferLocalModelsOnDecline = false });
        var stale = a.Read();
        await b.SaveAsync(b.Read() with { Sources = [new() { Kind="Ollama", Name="ollama", Endpoint=Endpoint, Model=Name, RequiresApiKey=false }] });
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.SaveAsync(stale with { LocalModels = [original with { UseOnDecline = true }] }));
        var current = new ModelSourcesStore(Paths).Read();
        Assert.False(current.PreferLocalModelsOnDecline); Assert.Equal(original, Assert.Single(current.LocalModels));
        await a.SaveAsync(current with { LocalModels = [] });
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.SaveAsync(current));
        Assert.Empty(new ModelSourcesStore(Paths).Read().LocalModels);
    }

    [Fact]
    public async Task SavedCorrectionsAffectTheNextRequestButNotAReplacementArtifact()
    {
        using var http = new Http(); var store = new ModelSourcesStore(Paths);
        await store.SaveAsync(new(0, []) { LocalModels = [new(Endpoint, Name, "d1", LocalModelBehavior.Standard)] });
        Assert.False((await Source(http, store).GetLanesAsync("c")).Lanes.Single(l => l.Provider == "Ollama").PreferOnDecline);
        http.Tags = http.Tags.Replace("d1", "d2");
        Assert.True((await Source(http, new ModelSourcesStore(Paths)).GetLanesAsync("c")).Lanes.Single(l => l.Provider == "Ollama").PreferOnDecline);
    }

    [Fact]
    public void CompositionKeepsACustomLaneSourceAndDisablesImplicitDownloads()
    {
        var custom = new OptionsCodingLaneSource(Options.Create(new CodingLaneOptions()));
        var services = new ServiceCollection(); services.AddLogging();
        services.AddSwarmCodeHost(new() { DataPaths = Paths, RepositoryRoot = Tmp, ConfigureServices = s => s.AddSingleton<ICodingLaneSource>(custom) });
        using var provider = services.BuildServiceProvider();
        Assert.Same(custom, provider.GetRequiredService<ICodingLaneSource>());
        Assert.False(provider.GetRequiredService<IOptions<OllamaProviderOptions>>().Value.AutoPullMissingModels);
    }
}
