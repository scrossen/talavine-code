using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Coding.Routing;
using System.Net;
using System.Net.Http.Json;

namespace SwarmCode.Hosting.Tests;

[Trait("LlmMode", "Mock")]
[Trait("DatabaseMode", "Ephemeral")]
public sealed class BuildModelSelectionTests : TempDirTest
{
    private const string LocalEndpoint = "http://127.0.0.1:19432";

    [Fact]
    public async Task Pre_build_catalog_discovers_installed_Ollama_models_without_starting_a_workspace_or_inference()
    {
        using var http = new LocalCatalogHttp();
        var started = new StartProbe();
        await using var catalog = LocalCatalog(http, started);
        Assert.DoesNotContain(catalog.Lanes, l => l.Provider == "Ollama");
        await catalog.RefreshAsync();
        var local = catalog.Lanes.Where(l => l.Provider == "Ollama").ToArray();
        Assert.Equal(new[] { "local/one:latest", "local/two:latest" }, local.Select(l => l.Model));
        Assert.Equal("Ollama", Assert.Single(CodingLaneSelections.ForPicker(local)).SelectionLabel);
        var models = await catalog.GetModelsAsync(local[0]);
        Assert.Contains("local/one:latest", models.Models);
        Assert.Contains("local/two:latest", models.Models);
        Assert.False(started.Started);
        Assert.Empty(await new WorkspaceSessionCatalog(Paths).ListAsync(Tmp));
        Assert.DoesNotContain(http.Paths, p => p is "/api/chat" or "/api/generate" or "/api/pull");
    }

    [Fact]
    public async Task Pre_build_refresh_drops_missing_models_and_keeps_other_providers_when_Ollama_is_offline()
    {
        using var http = new LocalCatalogHttp();
        await using var catalog = LocalCatalog(http, new());
        await catalog.RefreshAsync();
        Assert.Contains(catalog.Lanes, l => l.Provider == "Ollama");
        http.Empty = true;
        await catalog.RefreshAsync();
        Assert.DoesNotContain(catalog.Lanes, l => l.Provider == "Ollama");
        http.Empty = false;
        await catalog.RefreshAsync();
        Assert.Contains(catalog.Lanes, l => l.Provider == "Ollama");
        http.Offline = true;
        await catalog.RefreshAsync();
        Assert.Equal("other", Assert.Single(catalog.Lanes).Name);
    }

    [Fact]
    public async Task Pre_build_discovery_honors_an_explicitly_disabled_Ollama_connection()
    {
        await new ModelSourcesStore(Paths).SaveAsync(new(0,
        [new() { Kind = "Ollama", Name = "local", Endpoint = LocalEndpoint, Model = "local/one:latest", Enabled = false, RequiresApiKey = false }]));
        using var http = new LocalCatalogHttp();
        await using var catalog = LocalCatalog(http, new());
        await catalog.RefreshAsync();
        Assert.DoesNotContain(catalog.Lanes, l => l.Provider == "Ollama");
        Assert.DoesNotContain("/api/tags", http.Paths);
    }

    private ModelSelectionCatalog LocalCatalog(LocalCatalogHttp http, StartProbe started) => new(new()
    {
        RepositoryRoot = Tmp, DataPaths = Paths,
        Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LlmProviders:Ollama:Endpoint"] = LocalEndpoint,
            ["CodingLanes:Lanes:0:Name"] = "other",
            ["CodingLanes:Lanes:0:Provider"] = "CodexNative",
            ["CodingLanes:Lanes:0:Model"] = "codex-cli:default"
        }).Build(),
        ConfigureServices = s => { s.AddSingleton<IHttpClientFactory>(http); s.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(started); }
    });

    private sealed class LocalCatalogHttp : HttpMessageHandler, IHttpClientFactory
    {
        public readonly System.Collections.Concurrent.ConcurrentBag<string> Paths = [];
        public bool Offline, Empty;
        public HttpClient CreateClient(string name) => new(this, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (Offline) throw new HttpRequestException("Local server offline");
            Assert.DoesNotContain(path, new[] { "/api/chat", "/api/generate", "/api/pull" });
            if (request.RequestUri.Port != 19432) return new(HttpStatusCode.NotFound);
            var body = path switch
            {
                "/api/version" => """{"version":"1.0"}""",
                "/api/tags" => Empty ? """{"models":[]}""" : """{"models":[{"name":"local/one:latest","digest":"one","size":100},{"name":"local/two:latest","digest":"two","size":200},{"name":"embedding:latest","digest":"embedding","size":50},{"name":"remote-alias:latest","digest":"remote","size":0}]}""",
                "/api/show" => await ShowAsync(request, ct),
                _ => "{}"
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }
        private static async Task<string> ShowAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var input = await request.Content!.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct);
            return input.GetProperty("model").GetString() switch
            {
                "embedding:latest" => """{"capabilities":["embedding"]}""",
                "remote-alias:latest" => """{"capabilities":["tools"],"remote_host":"https://remote.invalid"}""",
                _ => """{"capabilities":["completion","tools"]}"""
            };
        }
    }

    [Fact]
    public async Task Catalog_uses_execution_registration_without_opening_a_workspace_or_starting_services()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CodingLanes:Lanes:0:Name"] = "my-provider",
            ["CodingLanes:Lanes:0:Provider"] = "CodexNative",
            ["CodingLanes:Lanes:0:Model"] = "codex-cli:default"
        }).Build();
        var started = new StartProbe();
        await using var catalog = new ModelSelectionCatalog(new()
        {
            RepositoryRoot = Tmp, DataPaths = Paths, Configuration = config,
            ConfigureServices = s => s.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(started)
        });
        var lane = Assert.Single(catalog.Lanes);
        Assert.Equal("my-provider", lane.Name);
        Assert.Equal(SwarmCore.Abstractions.LlmRouting.SubscriptionCliCatalog.CodexCliProvider, catalog.Provider(lane));
        Assert.False(started.Started);
        Assert.Empty(await new WorkspaceSessionCatalog(Paths).ListAsync(Tmp));
    }

    [Fact]
    public async Task Explicit_build_choice_including_thinking_is_a_durable_user_choice_before_first_prompt()
    {
        CodingLane[] lanes = [new("subscription", "CodexNative", "codex-cli:default")];
        var choice = new CodingModelChoice("SUBSCRIPTION", "codex-cli:custom-model@high");
        var selected = choice.Resolve(lanes);
        var catalog = new WorkspaceSessionCatalog(Paths);
        var session = await catalog.CreateAsync(Tmp);
        await catalog.SetModelAsync(session.Id, selected.Name, selected.Model);
        var reopened = (await new WorkspaceSessionCatalog(Paths).GetAsync(session.Id))!;
        Assert.True(reopened.Selection!.UserSelected);
        Assert.Equal("subscription", reopened.Selection.Route);
        Assert.Equal(choice.Model, reopened.Selection.Model);
        Assert.Equal(choice.Model, reopened.ModelChoices["subscription"]);
        Assert.Equal("codex-cli:default", lanes[0].Model);
    }

    [Fact]
    public void Missing_provider_or_empty_model_cannot_silently_fall_back()
    {
        Assert.Throws<InvalidOperationException>(() => new CodingModelChoice("removed", "model@high").Resolve([]));
        Assert.Throws<InvalidOperationException>(() => new CodingModelChoice("route", " ").Resolve([new("route", "custom", "default")]));
    }

    private sealed class StartProbe : Microsoft.Extensions.Hosting.IHostedService
    {
        public bool Started;
        public Task StartAsync(CancellationToken ct) { Started = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
