using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.ProviderPacks;

namespace SwarmCore.LlmRouting.Providers;

/// <summary>
/// DeepSeek provider. The chat/completions API is OpenAI-compatible at
/// <c>https://api.deepseek.com/v1</c>, so we inherit OpenAI's implementation and
/// only override endpoint/model identity.
/// </summary>
public class DeepSeekLlmProvider : OpenAILlmProvider
{
    private static readonly DirectApiProviderPack Pack =
        ProviderPackCatalog.GetRequired("DeepSeek").DirectApi!;
    private readonly DeepSeekProviderOptions _deepSeekOptions;
    private readonly OpenAICompatibleExecutionContract _deepSeekContract = new(OpenAICompatibleContractFlavor.DeepSeek);

    public override string ProviderName => "DeepSeek";
    public override LlmProviderExecutionContractDescriptor ExecutionContract => _deepSeekContract.Descriptor;
    public override LlmProviderExecutionTranslation TranslateExecution(string model, LlmGenerationParameters parameters,
        LlmRequestControl control, IReadOnlyDictionary<string, object>? providerExperimentalSettings = null)
        => _deepSeekContract.Translate(model, parameters, control, providerExperimentalSettings);

    private protected override void ApplyExecutionSettings(OpenAICompletionRequest request, Dictionary<string, object>? executionSettings)
    {
        base.ApplyExecutionSettings(request, executionSettings);
        if (executionSettings?.TryGetValue("thinking", out var thinking) == true
            && thinking is IReadOnlyDictionary<string, object>)
            request.Thinking = thinking;
        if (executionSettings?.TryGetValue("reasoning_effort", out var effort) == true)
            request.ReasoningEffort = effort?.ToString();
    }

    // DeepSeek's OpenAI-compatible endpoint. Current docs use
    // https://api.deepseek.com as base_url, with /models and /chat/completions
    // underneath it.
    protected override string BaseUrl => string.IsNullOrWhiteSpace(_deepSeekOptions.Endpoint)
        ? Pack.BaseUrl
        : NormalizeDeepSeekBaseUrl(_deepSeekOptions.Endpoint!);

    protected override IReadOnlyList<string> CatalogModelPrefixes => Pack.CatalogModelPrefixes;

    protected override IReadOnlyList<string> DefaultModels => Pack.DefaultModels;

    protected override IReadOnlyList<string> PreferredModels => Pack.PreferredModels;

    protected override string DefaultModel => _deepSeekOptions.DefaultModel ?? Pack.DefaultModel;

    protected override IReadOnlyList<string> VisionModels => Pack.VisionModelPatterns;

    public DeepSeekLlmProvider(
        ILogger<DeepSeekLlmProvider> logger,
        ILlmApiKeyProvider apiKeyProvider,
        IHttpClientFactory httpClientFactory,
        IOptions<DeepSeekProviderOptions> options)
        : base(logger, apiKeyProvider, httpClientFactory)
    {
        _deepSeekOptions = options.Value;
    }

    private static string NormalizeDeepSeekBaseUrl(string endpoint)
    {
        var normalized = endpoint.Trim().TrimEnd('/');
        foreach (var suffix in new[] { "/chat/completions", "/models", "/v1" })
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^suffix.Length].TrimEnd('/');
            }
        }

        return string.IsNullOrWhiteSpace(normalized)
            ? Pack.BaseUrl
            : normalized;
    }
}
