using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.Providers;

namespace SwarmCode.Hosting.Tests;

public sealed class DynamicModelCatalogTests
{
    [Theory]
    [InlineData("Claude")]
    [InlineData("Gemini")]
    public async Task ApiCatalogs_FollowPaginationWithoutInventingNames(string name)
    {
        var handler = new PagedHandler(name);
        ILlmModelCatalogProvider provider = name == "Claude"
            ? new ClaudeLlmProvider(NullLogger<ClaudeLlmProvider>.Instance, new Keys(), new Factory(handler), Options.Create(new ClaudeProviderOptions { Endpoint = "http://127.0.0.1:9345/v1" }))
            : new GeminiLlmProvider(NullLogger<GeminiLlmProvider>.Instance, new Keys(), new Factory(handler), Options.Create(new GeminiProviderOptions { Endpoint = "http://127.0.0.1:9345/v1" }));
        Assert.Equal(["brand-new-one", "brand-new-two"], (await provider.GetModelCatalogAsync()).Models);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task DeepSeek_RefreshesNewIds_AndNeverSubstitutesShippedNamesOnFailure()
    {
        var handler = new CatalogHandler();
        var provider = new DeepSeekLlmProvider(NullLogger<DeepSeekLlmProvider>.Instance, new Keys(), new Factory(handler),
            Options.Create(new DeepSeekProviderOptions { Endpoint = "http://127.0.0.1:9345/v1" }));
        handler.Json = """{"data":[{"id":"deepseek-new-fixture-model"}]}""";
        Assert.Equal(["deepseek-new-fixture-model"], (await provider.GetModelCatalogAsync()).Models);
        handler.Json = """{"data":[{"id":"deepseek-next-fixture-model"}]}""";
        Assert.Equal(["deepseek-next-fixture-model"], (await provider.GetModelCatalogAsync()).Models);
        handler.Status = HttpStatusCode.Unauthorized;
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetModelCatalogAsync());
        handler.Status = HttpStatusCode.OK; handler.Json = """{"data":[]}""";
        Assert.Empty((await provider.GetModelCatalogAsync()).Models);
        Assert.Equal(4, handler.Calls);
    }

    [Fact]
    public async Task NativeDiscovery_UsesControlOperationInsteadOfInference()
    {
        var runner = new CatalogRunner();
        // The local auth proof is replaced by a test runner's login-status response.
        var options = new SubscriptionCliProviderOptions();
        options.CodexCli.ExecutablePath = Environment.ProcessPath;
        options.CodexCli.PreferLocalSubscriptionAuthProof = false;
        var provider = new CodexCliLlmProvider(Options.Create(options), runner, NullLogger<CodexCliLlmProvider>.Instance);
        var catalog = await provider.GetModelCatalogAsync();
        Assert.Equal(["codex-cli:model-from-installed-cli"], catalog.Models);
        var discovery = Assert.Single(runner.Requests, r => r.Operation == SubscriptionCliProcessOperation.ModelDiscovery);
        Assert.Equal(SubscriptionCliCatalogProtocol.CodexAppServer, discovery.CatalogProtocol);
        Assert.Equal(["app-server"], discovery.Arguments);
        Assert.Null(discovery.StandardInput);
        Assert.DoesNotContain(runner.Requests, r => r.Operation == SubscriptionCliProcessOperation.Inference);
    }

    private sealed class CatalogRunner : ISubscriptionCliProcessRunner
    {
        public List<SubscriptionCliProcessRequest> Requests { get; } = [];
        public Task<SubscriptionCliProcessResult> RunAsync(SubscriptionCliProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(request.Operation == SubscriptionCliProcessOperation.ModelDiscovery
                ? new SubscriptionCliProcessResult(0, """{"id":2,"result":{"data":[{"model":"model-from-installed-cli"}]}}""", "")
                : new SubscriptionCliProcessResult(0, "Logged in using ChatGPT", ""));
        }
    }
    private sealed class Keys : ILlmApiKeyProvider
    {
        public Task<string?> GetApiKeyAsync(string providerName) => Task.FromResult<string?>("fixture-key");
        public Task SetApiKeyAsync(string providerName, string apiKey) => Task.CompletedTask;
        public Task<bool> HasApiKeyAsync(string providerName) => Task.FromResult(true);
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }
    private sealed class PagedHandler(string provider) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            if (Calls == 2) Assert.Contains(provider == "Claude" ? "after_id=page-two" : "pageToken=page-two", request.RequestUri!.Query);
            var json = provider == "Claude"
                ? (Calls == 1 ? """{"data":[{"id":"brand-new-one"}],"has_more":true,"last_id":"page-two"}""" : """{"data":[{"id":"brand-new-two"}],"has_more":false}""")
                : (Calls == 1 ? """{"models":[{"name":"models/brand-new-one"}],"nextPageToken":"page-two"}""" : """{"models":[{"name":"models/brand-new-two"}]}""");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
    private sealed class CatalogHandler : HttpMessageHandler
    {
        public string Json = "{}";
        public HttpStatusCode Status = HttpStatusCode.OK;
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/models", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Json) });
        }
    }
}
