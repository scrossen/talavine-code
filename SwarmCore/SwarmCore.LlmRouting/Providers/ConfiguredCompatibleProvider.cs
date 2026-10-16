using Microsoft.Extensions.Logging;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.ToolUse;

namespace SwarmCore.LlmRouting.Providers;

/// <summary>Named OpenAI-compatible connection; never filters model IDs to a vendor family.</summary>
public class ConfiguredCompatibleProvider : OpenAILlmProvider, IToolUseProviderSource
{
    private readonly ConfiguredModelSource source;
    public static ConfiguredCompatibleProvider Create(ConfiguredModelSource source, ILlmApiKeyProvider keys,
        IHttpClientFactory http, ILoggerFactory logging) => source.RequiresApiKey
            ? new ConfiguredCompatibleProvider(source, keys, http, logging) : new CredentialFree(source, keys, http, logging);

    protected ConfiguredCompatibleProvider(ConfiguredModelSource source, ILlmApiKeyProvider keys,
        IHttpClientFactory http, ILoggerFactory logging) : base(logging.CreateLogger<ConfiguredCompatibleProvider>(), keys, http)
    {
        source.Validate(false);
        this.source = source;
        ToolUseProvider = new OpenAIStyleToolUseProvider(source.Provider, BaseUrl + "/chat/completions",
            async ct => source.RequiresApiKey
                ? new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await GetApiKeyAsync().WaitAsync(ct)) : null,
            http, logging.CreateLogger<OpenAIStyleToolUseProvider>());
    }
    public override string ProviderName => source.Provider;
    protected override bool RequiresApiKey => source.RequiresApiKey;
    protected override string BaseUrl => source.Endpoint.TrimEnd('/');
    protected override string DefaultModel => source.Model;
    protected override IReadOnlyList<string> DefaultModels => string.IsNullOrWhiteSpace(source.Model) ? [] : [source.Model];
    protected override IReadOnlyList<string> CatalogModelPrefixes => [];
    protected override IReadOnlyList<string> PreferredModels => DefaultModels;
    protected override IReadOnlyList<string> VisionModels => [];
    public override bool SupportsVision(string model) => false; // Do not invent endpoint capabilities.
    public override bool SupportsEmbeddings => false;
    public override string? DefaultEmbeddingModel => null;
    public override bool SupportsModel(string model) => !string.IsNullOrWhiteSpace(model);
    protected override void ApplyDefaultRequestHeaders(HttpClient client, string apiKey)
    {
        if (source.RequiresApiKey) base.ApplyDefaultRequestHeaders(client, apiKey);
    }
    public IToolUseProvider ToolUseProvider { get; }

    // This marker describes authentication only; it grants neither LocalOnly locality nor model clearance.
    private sealed class CredentialFree(ConfiguredModelSource source, ILlmApiKeyProvider keys,
        IHttpClientFactory http, ILoggerFactory logging)
        : ConfiguredCompatibleProvider(source, keys, http, logging), ICredentialFreeLlmProvider;
}
