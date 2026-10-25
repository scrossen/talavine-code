using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.ProviderPacks;

namespace SwarmCore.LlmRouting.Providers;

/// <summary>
/// Venice AI's OpenAI-compatible model catalog and chat completions. Endpoint,
/// default model and price facts come from the reviewed <c>venice.json</c> pack.
/// </summary>
public sealed class VeniceLlmProvider : OpenAILlmProvider
{
    private static readonly DirectApiProviderPack Pack =
        ProviderPackCatalog.GetRequired("Venice").DirectApi!;
    private readonly VeniceProviderOptions _options;

    public VeniceLlmProvider(ILogger<VeniceLlmProvider> logger, ILlmApiKeyProvider keys,
        IHttpClientFactory http, IOptions<VeniceProviderOptions> options) : base(logger, keys, http)
        => _options = options.Value;

    public override string ProviderName => "Venice";
    protected override string BaseUrl => (string.IsNullOrWhiteSpace(_options.Endpoint) ? Pack.BaseUrl : _options.Endpoint).TrimEnd('/');
    protected override string DefaultModel => _options.DefaultModel ?? Pack.DefaultModel;
    protected override IReadOnlyList<string> DefaultModels => Pack.DefaultModels.Contains(DefaultModel, StringComparer.OrdinalIgnoreCase)
        ? Pack.DefaultModels
        : [DefaultModel, .. Pack.DefaultModels];
    protected override IReadOnlyList<string> PreferredModels => Pack.PreferredModels;
    protected override IReadOnlyList<string> CatalogModelPrefixes => Pack.CatalogModelPrefixes;
    protected override IReadOnlyList<string> VisionModels => Pack.VisionModelPatterns;
    public override bool SupportsVision(string model) => false;
    public override bool SupportsEmbeddings => false;
    public override string? DefaultEmbeddingModel => null;
    public override bool SupportsModel(string model) => !string.IsNullOrWhiteSpace(model);
}
