using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.ProviderPacks;

namespace SwarmCore.LlmRouting.Providers;

/// <summary>
/// xAI Grok provider. Grok's chat/completions API is OpenAI-compatible at
/// <c>https://api.x.ai/v1</c>, so we inherit the full OpenAI implementation and only
/// override endpoint/model identity. The user-visible model names start with
/// "grok-"; model catalog filtering is restricted accordingly so the catalog doesn't
/// surface noise.
/// </summary>
public class GrokLlmProvider : OpenAILlmProvider
{
    private static readonly DirectApiProviderPack Pack =
        ProviderPackCatalog.GetRequired("Grok").DirectApi!;
    private readonly GrokProviderOptions _grokOptions;

    public override string ProviderName => "Grok";

    protected override string BaseUrl => string.IsNullOrWhiteSpace(_grokOptions.Endpoint)
        ? Pack.BaseUrl
        : _grokOptions.Endpoint!.TrimEnd('/');

    protected override IReadOnlyList<string> CatalogModelPrefixes => Pack.CatalogModelPrefixes;

    protected override IReadOnlyList<string> DefaultModels => Pack.DefaultModels;

    protected override IReadOnlyList<string> PreferredModels => Pack.PreferredModels;

    protected override string DefaultModel => _grokOptions.DefaultModel ?? Pack.DefaultModel;

    protected override IReadOnlyList<string> VisionModels => Pack.VisionModelPatterns;

    public GrokLlmProvider(
        ILogger<GrokLlmProvider> logger,
        ILlmApiKeyProvider apiKeyProvider,
        IHttpClientFactory httpClientFactory,
        IOptions<GrokProviderOptions> options)
        : base(logger, apiKeyProvider, httpClientFactory)
    {
        _grokOptions = options.Value;
    }
}
