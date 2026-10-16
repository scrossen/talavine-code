using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Configuration;

namespace SwarmCore.LlmRouting.Providers;

/// <summary>Venice AI's OpenAI-compatible model catalog and chat completions.</summary>
public sealed class VeniceLlmProvider : OpenAILlmProvider
{
    private readonly VeniceProviderOptions _options;

    public VeniceLlmProvider(ILogger<VeniceLlmProvider> logger, ILlmApiKeyProvider keys,
        IHttpClientFactory http, IOptions<VeniceProviderOptions> options) : base(logger, keys, http)
        => _options = options.Value;

    public override string ProviderName => "Venice";
    protected override string BaseUrl => (_options.Endpoint ?? "https://api.venice.ai/api/v1").TrimEnd('/');
    protected override string DefaultModel => _options.DefaultModel ?? "zai-org-glm-5-2";
    protected override IReadOnlyList<string> DefaultModels => [DefaultModel];
    protected override IReadOnlyList<string> PreferredModels => [DefaultModel];
    protected override IReadOnlyList<string> CatalogModelPrefixes => [];
    protected override IReadOnlyList<string> VisionModels => [];
    public override bool SupportsVision(string model) => false;
    public override bool SupportsEmbeddings => false;
    public override string? DefaultEmbeddingModel => null;
    public override bool SupportsModel(string model) => !string.IsNullOrWhiteSpace(model);
}
