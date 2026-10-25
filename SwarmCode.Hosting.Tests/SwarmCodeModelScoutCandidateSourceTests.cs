using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SwarmCode.Hosting.Llm;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.LocalRuntime;
using SwarmCore.LlmRouting.Services;

namespace SwarmCode.Hosting.Tests;

public sealed class SwarmCodeModelScoutCandidateSourceTests : TempDirTest
{
    [Fact]
    public async Task Exact_installed_local_model_is_not_truncated_and_never_becomes_quality_proof()
    {
        using var http = new InventoryHttp();
        var provider = new Provider("Ollama");
        var source = await SourceAsync(provider, http);
        var request = new ModelScoutRecommendationRequest { FunctionType = "Coding", Provider = "Ollama",
            ExactModel = "worker-9", RequiresToolUse = true, MinimumContextTokens = 8192 };
        var candidate = Assert.Single(await source.LoadCandidatesAsync(request));
        Assert.Equal("worker-9", candidate.Model);
        Assert.True(candidate.IsLocal);
        Assert.True(candidate.SupportsToolUse);
        Assert.Equal(16384, candidate.MaxContextTokens);
        Assert.Equal(0m, candidate.EstimatedCostUsd);
        Assert.Equal(75m, candidate.CapabilityScore);
        Assert.Equal(ModelScoutCapabilityEvidenceKind.CatalogPrior, candidate.CapabilityEvidenceKind);
        Assert.Null(candidate.FunctionEvidence);
        Assert.Null(candidate.EvidenceFunctionType);
        Assert.NotNull(ModelScoutCandidateRanker.Rank(request, [candidate]).Selected);
        Assert.Null(ModelScoutCandidateRanker.Rank(new() { FunctionType = "Coding", Tier = ModelScoutTier.Frontier }, [candidate]).Selected);
        Assert.Null(ModelScoutCandidateRanker.Rank(new() { FunctionType = "Coding", RequireMeasuredCapabilityScore = true }, [candidate]).Selected);
        Assert.Null(ModelScoutCandidateRanker.Rank(new() { FunctionType = "Coding", RequireCurrentFunctionEvidence = true }, [candidate]).Selected);
        Assert.Equal(0, provider.InferenceCalls);
    }

    [Theory]
    [InlineData("hosted-alias")]
    [InlineData("not-installed")]
    public async Task Cloud_alias_and_stale_configured_model_are_absent(string exactModel)
    {
        using var http = new InventoryHttp();
        var source = await SourceAsync(new("Ollama"), http);
        Assert.Empty(await source.LoadCandidatesAsync(new() { FunctionType = "Coding", Provider = "Ollama", ExactModel = exactModel }));
    }

    [Fact]
    public async Task Artifact_change_during_metadata_read_is_not_joined_to_installed_inventory()
    {
        using var http = new InventoryHttp();
        var source = await SourceAsync(new("Ollama") { Digest = "replacement" }, http);
        Assert.Empty(await source.LoadCandidatesAsync(new() { FunctionType = "Coding", ExactModel = "worker-9" }));
    }

    [Fact]
    public async Task Decision_only_metadata_does_not_satisfy_worker_tool_gate()
    {
        using var http = new InventoryHttp();
        var source = await SourceAsync(new("Ollama"), http);
        var request = new ModelScoutRecommendationRequest { FunctionType = "Coding", ExactModel = "decision-only", RequiresToolUse = true };
        var candidate = Assert.Single(await source.LoadCandidatesAsync(request));
        Assert.False(candidate.SupportsToolUse);
        Assert.Null(ModelScoutCandidateRanker.Rank(request, [candidate]).Selected);
    }

    [Fact]
    public async Task Catalog_unknown_price_and_structured_support_stay_unknown()
    {
        using var http = new InventoryHttp();
        var provider = new Provider("RemoteCatalog");
        var source = await SourceAsync(provider, http);
        var candidate = Assert.Single(await source.LoadCandidatesAsync(new() { FunctionType = "Coding", ExactModel = "worker-9" }));
        Assert.False(candidate.IsLocal);
        Assert.True(candidate.SupportsToolUse);
        Assert.Null(candidate.EstimatedCostUsd);
        Assert.Null(candidate.SupportsStructuredOutput);
        Assert.Equal(0, provider.InferenceCalls);
    }

    [Fact]
    public async Task Public_daemon_cannot_inherit_local_proof_from_stale_loopback_settings()
    {
        using var http = new InventoryHttp();
        var source = await SourceAsync(new("Ollama"), http, "https://public.example");
        Assert.Empty(await source.LoadCandidatesAsync(new() { FunctionType = "Coding", ExactModel = "worker-9" }));
        Assert.Empty(http.Requests);
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("https://public.example")]
    public async Task Metadata_from_another_port_or_remote_endpoint_cannot_join_local_inventory(string observedEndpoint)
    {
        using var http = new InventoryHttp();
        var source = await SourceAsync(new("Ollama") { Endpoint = observedEndpoint }, http, "http://127.0.0.1:11435");
        Assert.Empty(await source.LoadCandidatesAsync(new() { FunctionType = "Coding", ExactModel = "worker-9" }));
    }

    [Fact]
    public async Task Matching_configured_loopback_port_and_normalized_trailing_slash_preserve_local_proof()
    {
        using var http = new InventoryHttp();
        var source = await SourceAsync(new("Ollama") { Endpoint = "http://127.0.0.1:11435" }, http, "http://127.0.0.1:11435/");
        var candidate = Assert.Single(await source.LoadCandidatesAsync(new() { FunctionType = "Coding", ExactModel = "worker-9" }));
        Assert.True(candidate.IsLocal);
        Assert.All(http.Requests, uri => Assert.Equal(11435, uri.Port));
    }

    [Fact]
    public async Task Connected_source_bootstraps_fresh_inventory_without_duplicate_llm_settings_row()
    {
        using var http = new InventoryHttp();
        var settings = new JsonLlmSettingsProvider(Paths);
        var sources = await ConnectedSourceAsync();
        var provider = new Provider("Ollama");
        var source = ConnectedCandidateSource(settings, sources, provider, http);
        Assert.Empty(await settings.GetAllProviderSettingsAsync());
        var candidate = Assert.Single(await source.LoadCandidatesAsync(new() { FunctionType = "Coding", Provider = "Ollama", ExactModel = "worker-9" }));
        Assert.True(candidate.IsLocal);
        Assert.True(candidate.SupportsToolUse);
        Assert.Equal(75m, candidate.CapabilityScore);
        Assert.Null(candidate.FunctionEvidence);
        Assert.Empty(await new JsonLlmSettingsProvider(Paths).GetAllProviderSettingsAsync());
        Assert.Equal(0, provider.InferenceCalls);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Disabled_connected_source_or_explicit_row_prevents_bootstrap(bool sourceEnabled, bool rowEnabled)
    {
        using var http = new InventoryHttp();
        var settings = new JsonLlmSettingsProvider(Paths);
        Assert.True((await settings.SaveProviderAsync(new() { ProviderName = "Ollama", IsEnabled = rowEnabled,
            Endpoint = "http://127.0.0.1:11434" })).Success);
        var source = ConnectedCandidateSource(settings, await ConnectedSourceAsync(sourceEnabled), new("Ollama"), http);
        Assert.Empty(await source.LoadCandidatesAsync(new() { FunctionType = "Coding", ExactModel = "worker-9" }));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Explicit_remote_settings_endpoint_does_not_gain_local_proof_from_connected_loopback_source()
    {
        using var http = new InventoryHttp();
        var settings = new JsonLlmSettingsProvider(Paths);
        Assert.True((await settings.SaveProviderAsync(new() { ProviderName = "Ollama", Endpoint = "https://public.example" })).Success);
        var source = ConnectedCandidateSource(settings, await ConnectedSourceAsync(), new("Ollama"), http);
        Assert.Empty(await source.LoadCandidatesAsync(new() { FunctionType = "Coding", ExactModel = "worker-9" }));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Changed_source_endpoint_requires_the_registered_adapter_to_rebind_before_local_admission()
    {
        using var http = new InventoryHttp();
        var settings = new JsonLlmSettingsProvider(Paths);
        var source = ConnectedCandidateSource(settings, await ConnectedSourceAsync(endpoint: "http://127.0.0.1:11435"), new("Ollama"), http);
        Assert.Empty(await source.LoadCandidatesAsync(new() { FunctionType = "Coding", ExactModel = "worker-9" }));
        Assert.Empty(http.Requests);
    }

    private async Task<ModelSourcesStore> ConnectedSourceAsync(bool enabled = true, string endpoint = "http://127.0.0.1:11434")
    {
        var sources = new ModelSourcesStore(Paths);
        await sources.SaveAsync(new(0, [new() { Name = "local-models", Kind = "Ollama", Endpoint = endpoint,
            Model = "worker-9", RequiresApiKey = false, Enabled = enabled }]));
        return sources;
    }

    private static SwarmCodeModelScoutCandidateSource ConnectedCandidateSource(JsonLlmSettingsProvider settings,
        ModelSourcesStore sources, Provider provider, InventoryHttp http)
        => new(settings, new LlmProviderRegistry([provider]), new(http),
            Options.Create(new OllamaProviderOptions { Endpoint = "http://127.0.0.1:11434" }), modelSources: sources);

    private async Task<SwarmCodeModelScoutCandidateSource> SourceAsync(Provider provider, InventoryHttp http,
        string actualEndpoint = "http://127.0.0.1:11434")
    {
        var settings = new JsonLlmSettingsProvider(Paths);
        Assert.True((await settings.SaveProviderAsync(new() { ProviderName = provider.ProviderName,
            Endpoint = provider.ProviderName == "Ollama" ? "http://127.0.0.1:11434" : "https://models.example",
            DefaultModel = "not-installed" })).Success);
        return new(settings, new LlmProviderRegistry([provider]), new(http), Options.Create(new OllamaProviderOptions { Endpoint = actualEndpoint }));
    }

    private sealed class InventoryHttp : HttpMessageHandler, IHttpClientFactory
    {
        public System.Collections.Concurrent.ConcurrentBag<Uri> Requests { get; } = [];
        public HttpClient CreateClient(string name) => new(this, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            var models = Enumerable.Range(0, 10).Select(index => "worker-" + index).Append("hosted-alias").Append("decision-only");
            object body = request.RequestUri!.AbsolutePath switch
            {
                "/api/version" => new { version = "test" },
                "/api/tags" => new { models = models.Select(name => new { name, digest = "artifact", size = 100 }) },
                _ => await ShowAsync(request, ct)
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) };
        }
        private static async Task<object> ShowAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var model = body.RootElement.GetProperty("model").GetString();
            return new { capabilities = model == "decision-only" ? new[] { "completion" } : new[] { "completion", "tools" },
                remote_host = model == "hosted-alias" ? "https://hosted.example" : null };
        }
    }

    private sealed class Provider(string name) : ILlmProvider, ILlmModelCapabilityEvidenceProvider, ILlmModelCatalogProvider, ICredentialFreeLlmProvider
    {
        public string ProviderName => name;
        public string Digest { get; init; } = "artifact";
        public string Endpoint { get; init; } = "http://127.0.0.1:11434";
        public int InferenceCalls { get; private set; }
        public bool SupportsStreaming => false;
        public bool SupportsEmbeddings => false;
        public string? DefaultEmbeddingModel => null;
        public bool SupportsModel(string model) => true;
        public bool SupportsVision(string model) => false;
        public Task<List<string>> GetAvailableModelsAsync() => Task.FromResult(new List<string> { "worker-9" });
        public Task<LlmModelCatalog> GetModelCatalogAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmModelCatalog(["worker-9"], "fake-advertised-catalog")
                { Details = [new("worker-9", 16384, true, null, null, null)] });
        public Task<LlmModelCapabilityEvidence?> GetModelCapabilityEvidenceAsync(string model, CancellationToken ct = default)
            => Task.FromResult<LlmModelCapabilityEvidence?>(new(name, model, "test", Endpoint, Digest,
                DateTimeOffset.UtcNow, ["completion", "tools"], model != "decision-only", false, null, 16384, "fake-read-only-metadata"));
        public Task<(bool IsHealthy, string Message)> TestConnectivityAsync() => Task.FromResult((true, "test"));
        public Task<LlmResponse> GenerateResponseAsync(string prompt, string model, Dictionary<string, object>? executionSettings = null)
        { InferenceCalls++; throw new InvalidOperationException("Discovery must never run inference."); }
        public Task<LlmResponse> GenerateVisionResponseAsync(string prompt, string imageBase64, string model, Dictionary<string, object>? executionSettings = null)
            => GenerateResponseAsync(prompt, model, executionSettings);
        public Task<LlmResponse> GenerateStreamingResponseAsync(string prompt, string model, Func<string, Task> onChunk, Dictionary<string, object>? executionSettings = null)
            => GenerateResponseAsync(prompt, model, executionSettings);
        public Task<LlmEmbeddingResponse> GenerateEmbeddingAsync(string text, string model, Dictionary<string, object>? executionSettings = null) => throw new NotSupportedException();
        public Task<List<LlmEmbeddingResponse>> GenerateEmbeddingsAsync(IList<string> texts, string model, Dictionary<string, object>? executionSettings = null) => throw new NotSupportedException();
    }
}
