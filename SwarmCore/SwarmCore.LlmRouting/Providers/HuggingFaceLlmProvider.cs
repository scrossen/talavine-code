using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.ProviderPacks;

namespace SwarmCore.LlmRouting.Providers;

/// <summary>
/// Hugging Face Inference Router provider. HF exposes an OpenAI-compatible chat
/// completions API at <c>https://router.huggingface.co/v1</c> that fans requests
/// out to hosted Inference Providers (together.ai, fireworks, hyperbolic, etc.).
/// Users need an HF token (<c>hf_…</c>) and a model id in the
/// <c>namespace/model</c> form (e.g. <c>meta-llama/Meta-Llama-3.1-70B-Instruct</c>).
///
/// We inherit OpenAI's implementation and only override identity/base URL. The
/// router doesn't expose a /models list compatible with OpenAI's schema, so
/// <see cref="OpenAILlmProvider.HasListModelsEndpoint"/> is turned off — the UI falls
/// back to <see cref="OpenAILlmProvider.DefaultModels"/> for the picker.
/// </summary>
public class HuggingFaceLlmProvider : OpenAILlmProvider
{
    private static readonly DirectApiProviderPack Pack =
        ProviderPackCatalog.GetRequired("HuggingFace").DirectApi!;
    private readonly HuggingFaceProviderOptions _hfOptions;

    public override string ProviderName => "HuggingFace";

    protected override string BaseUrl => string.IsNullOrWhiteSpace(_hfOptions.Endpoint)
        ? Pack.BaseUrl
        : _hfOptions.Endpoint!.TrimEnd('/');

    protected override bool HasListModelsEndpoint => Pack.HasListModelsEndpoint;

    // Routed inference deployments vary by backend; not all accept the
    // stream_options request field, so streamed usage is estimated instead.
    protected override bool SupportsStreamUsageOption => false;

    protected override IReadOnlyList<string> CatalogModelPrefixes => Array.Empty<string>();

    protected override IReadOnlyList<string> DefaultModels => Pack.DefaultModels;

    protected override IReadOnlyList<string> PreferredModels => Pack.PreferredModels;

    protected override string DefaultModel => _hfOptions.DefaultModel ?? Pack.DefaultModel;

    // HF router doesn't guarantee any particular vision model id shape — treat
    // vision as opt-in via the model id containing "vision".
    protected override IReadOnlyList<string> VisionModels => Pack.VisionModelPatterns;

    public HuggingFaceLlmProvider(
        ILogger<HuggingFaceLlmProvider> logger,
        ILlmApiKeyProvider apiKeyProvider,
        IHttpClientFactory httpClientFactory,
        IOptions<HuggingFaceProviderOptions> options)
        : base(logger, apiKeyProvider, httpClientFactory)
    {
        _hfOptions = options.Value;
    }
}
