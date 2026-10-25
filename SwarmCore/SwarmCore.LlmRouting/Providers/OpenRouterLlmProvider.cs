using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.Json;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.ProviderPacks;

namespace SwarmCore.LlmRouting.Providers;

/// <summary>
/// OpenRouter provider. OpenRouter exposes an OpenAI-compatible API at
/// <c>https://openrouter.ai/api/v1</c> and accepts provider-qualified model ids
/// such as <c>openai/gpt-5-mini</c> plus documented aliases like
/// <c>~openai/gpt-latest</c>.
/// </summary>
public class OpenRouterLlmProvider : OpenAILlmProvider
{
    private static readonly OpenRouterProviderPack Pack =
        ProviderPackCatalog.GetRequired("OpenRouter").OpenRouter!;
    private readonly OpenAICompatibleExecutionContract _executionContract =
        new(OpenAICompatibleContractFlavor.OpenRouter);
    private readonly OpenRouterProviderOptions _openRouterOptions;

    public override string ProviderName => "OpenRouter";

    public override async Task<LlmModelCatalog> GetModelCatalogAsync(CancellationToken cancellationToken = default)
    {
        using var client = HttpClientFactory.CreateClient();
        ApplyDefaultRequestHeaders(client, await GetApiKeyAsync().ConfigureAwait(false));
        // This endpoint retains the account's provider/privacy/guardrail preferences.
        using var response = await client.GetAsync($"{BaseUrl}/models/user", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = await OpenRouterCatalogContract.ReadBoundedDocumentAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var details = ParseCatalog(document.RootElement);
        return new LlmModelCatalog(details.Select(m => m.Id).ToArray(), "Account-filtered provider API catalog") { Details = details, ReasoningEfforts = ParseReasoningEfforts(document.RootElement) };
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseReasoningEfforts(JsonElement root)
    {
        var choices = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in root.GetProperty("data").EnumerateArray())
        {
            if (!model.TryGetProperty("id", out var id) || id.GetString() is not { Length: > 0 } name) continue;
            if (model.TryGetProperty("supported_parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Array)
                choices[name] = parameters.EnumerateArray().Any(p => p.GetString() is "reasoning" or "reasoning.effort")
                    ? ["low", "medium", "high"] : [];
        }
        return choices;
    }

    /// <summary>
    /// Reads the account catalog through the shared OpenRouter catalog
    /// contract, which owns the vendor schema and its sanity bounds.
    /// </summary>
    internal static IReadOnlyList<LlmCatalogModel> ParseCatalog(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The OpenRouter model catalog response did not contain a model list.");
        }

        var result = new List<LlmCatalogModel>();
        foreach (var item in data.EnumerateArray())
        {
            if (!OpenRouterCatalogContract.TryReadModel(item, out var model)) continue;
            bool? moderated = item.TryGetProperty("top_provider", out var provider)
                && provider.ValueKind == JsonValueKind.Object
                && provider.TryGetProperty("is_moderated", out var moderation)
                && moderation.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? moderation.GetBoolean() : null;
            result.Add(new LlmCatalogModel(model.Id, model.ContextTokens, model.Capabilities.SupportsTools,
                PerMillion(model.PromptPricePerToken), PerMillion(model.CompletionPricePerToken), moderated));
        }
        return result;

        static decimal? PerMillion(decimal? perToken) => perToken * 1_000_000m;
    }
    public override LlmProviderExecutionContractDescriptor ExecutionContract => _executionContract.Descriptor;
    public override LlmProviderExecutionTranslation TranslateExecution(
        string model,
        LlmGenerationParameters parameters,
        LlmRequestControl control,
        IReadOnlyDictionary<string, object>? providerExperimentalSettings = null)
        => _executionContract.Translate(model, parameters, control, providerExperimentalSettings);
    public override bool SupportsEmbeddings => false;
    public override string? DefaultEmbeddingModel => null;

    protected override string BaseUrl => string.IsNullOrWhiteSpace(_openRouterOptions.Endpoint)
        ? Pack.BaseUrl
        : NormalizeOpenRouterBaseUrl(_openRouterOptions.Endpoint!);

    // OpenRouter's catalog includes many provider namespaces; keep the full list
    // and let callers/model preferences choose the desired route.
    protected override IReadOnlyList<string> CatalogModelPrefixes => Array.Empty<string>();

    protected override IReadOnlyList<string> DefaultModels => Pack.DefaultModels;

    protected override IReadOnlyList<string> PreferredModels => Pack.DefaultModels;

    protected override string DefaultModel => _openRouterOptions.DefaultModel ?? Pack.DefaultModel;

    protected override IReadOnlyList<string> VisionModels => Pack.VisionModelPatterns;

    public OpenRouterLlmProvider(
        ILogger<OpenRouterLlmProvider> logger,
        ILlmApiKeyProvider apiKeyProvider,
        IHttpClientFactory httpClientFactory,
        IOptions<OpenRouterProviderOptions> options)
        : base(logger, apiKeyProvider, httpClientFactory)
    {
        _openRouterOptions = options.Value;
    }

    public override bool SupportsModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return false;

        var normalized = model.Trim();
        return normalized.StartsWith("~", StringComparison.Ordinal)
               || normalized.Contains('/', StringComparison.Ordinal)
               || base.SupportsModel(normalized);
    }

    protected override void ApplyDefaultRequestHeaders(HttpClient client, string apiKey)
    {
        base.ApplyDefaultRequestHeaders(client, apiKey);

        if (!string.IsNullOrWhiteSpace(_openRouterOptions.SiteUrl))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("HTTP-Referer", _openRouterOptions.SiteUrl);
        }

        if (!string.IsNullOrWhiteSpace(_openRouterOptions.ApplicationTitle))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-OpenRouter-Title", _openRouterOptions.ApplicationTitle);
        }
    }

    private protected override void ApplyExecutionSettings(OpenAICompletionRequest request, Dictionary<string, object>? executionSettings)
    {
        base.ApplyExecutionSettings(request, executionSettings);

        // Always ask OpenRouter for usage accounting so the response reports
        // the actually billed cost instead of leaving only local estimates.
        request.UsageAccounting = new Dictionary<string, object> { ["include"] = true };

        if (executionSettings is null)
        {
            return;
        }

        if (executionSettings.TryGetValue("reasoning", out var reasoning)
            && reasoning is IReadOnlyDictionary<string, object>)
        {
            request.Reasoning = reasoning;
            request.ReasoningEffort = null;
        }

        var fallbackModels = ReadStringList(executionSettings, "openrouter_models")
            ?? ReadStringList(executionSettings, "models");
        if (fallbackModels is { Count: > 0 })
        {
            request.Models = fallbackModels;
        }

        if (TryReadString(executionSettings, "openrouter_session_id", out var sessionId)
            || TryReadString(executionSettings, "session_id", out sessionId))
        {
            request.SessionId = sessionId;
        }

        var provider = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        AddStringList(provider, "order", executionSettings, "openrouter_provider_order", "provider_order");
        AddStringList(provider, "only", executionSettings, "openrouter_provider_only", "provider_only");
        AddStringList(provider, "ignore", executionSettings, "openrouter_provider_ignore", "provider_ignore");
        AddStringList(provider, "quantizations", executionSettings, "openrouter_provider_quantizations", "provider_quantizations");
        AddString(provider, "sort", executionSettings, "openrouter_provider_sort", "provider_sort");
        AddString(provider, "data_collection", executionSettings, "openrouter_data_collection", "data_collection");
        AddBoolean(provider, "allow_fallbacks", executionSettings, "openrouter_allow_fallbacks", "allow_fallbacks");
        AddBoolean(provider, "require_parameters", executionSettings, "openrouter_require_parameters", "require_parameters");
        AddBoolean(provider, "zdr", executionSettings, "openrouter_zdr", "zdr");
        AddNumber(provider, "preferred_min_throughput", executionSettings, "openrouter_preferred_min_throughput", "preferred_min_throughput");
        AddNumber(provider, "preferred_max_latency", executionSettings, "openrouter_preferred_max_latency", "preferred_max_latency");

        var maxPrice = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        AddNumber(maxPrice, "prompt", executionSettings, "openrouter_max_prompt_price", "openrouter_max_price_prompt", "max_prompt_price");
        AddNumber(maxPrice, "completion", executionSettings, "openrouter_max_completion_price", "openrouter_max_price_completion", "max_completion_price");
        AddNumber(maxPrice, "request", executionSettings, "openrouter_max_request_price", "openrouter_max_price_request", "max_request_price");
        if (maxPrice.Count > 0)
        {
            provider["max_price"] = maxPrice;
        }

        if (request.ResponseFormat is not null)
        {
            provider.TryAdd("require_parameters", true);
        }

        if (provider.Count > 0)
        {
            request.Provider = provider;
        }

        var autoRouterPlugin = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        AddStringList(autoRouterPlugin, "allowed_models", executionSettings, "openrouter_auto_allowed_models", "auto_router_allowed_models");
        AddInteger(autoRouterPlugin, "cost_quality_tradeoff", executionSettings, "openrouter_auto_cost_quality_tradeoff", "auto_router_cost_quality_tradeoff");
        if (autoRouterPlugin.Count > 0)
        {
            autoRouterPlugin["id"] = "auto-router";
            request.Plugins = new List<Dictionary<string, object>> { autoRouterPlugin };
        }
    }

    private static string NormalizeOpenRouterBaseUrl(string endpoint)
    {
        var normalized = endpoint.Trim().TrimEnd('/');
        var queryIdx = normalized.IndexOf('?');
        if (queryIdx >= 0)
        {
            normalized = normalized[..queryIdx].TrimEnd('/');
        }

        foreach (var suffix in new[] { "/chat/completions", "/models" })
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^suffix.Length].TrimEnd('/');
            }
        }

        if (normalized.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        if (normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        if (normalized.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            return $"{normalized}/v1";
        }

        return string.IsNullOrWhiteSpace(normalized)
            ? Pack.BaseUrl
            : $"{normalized}/api/v1";
    }

    private static void AddString(
        Dictionary<string, object> target,
        string targetKey,
        Dictionary<string, object> source,
        params string[] sourceKeys)
    {
        foreach (var key in sourceKeys)
        {
            if (TryReadString(source, key, out var value))
            {
                target[targetKey] = value;
                return;
            }
        }
    }

    private static void AddStringList(
        Dictionary<string, object> target,
        string targetKey,
        Dictionary<string, object> source,
        params string[] sourceKeys)
    {
        foreach (var key in sourceKeys)
        {
            var values = ReadStringList(source, key);
            if (values is { Count: > 0 })
            {
                target[targetKey] = values;
                return;
            }
        }
    }

    private static void AddBoolean(
        Dictionary<string, object> target,
        string targetKey,
        Dictionary<string, object> source,
        params string[] sourceKeys)
    {
        foreach (var key in sourceKeys)
        {
            if (TryReadBoolean(source, key, out var value))
            {
                target[targetKey] = value;
                return;
            }
        }
    }

    private static void AddNumber(
        Dictionary<string, object> target,
        string targetKey,
        Dictionary<string, object> source,
        params string[] sourceKeys)
    {
        foreach (var key in sourceKeys)
        {
            if (TryReadNumber(source, key, out var value))
            {
                target[targetKey] = value;
                return;
            }
        }
    }

    private static void AddInteger(
        Dictionary<string, object> target,
        string targetKey,
        Dictionary<string, object> source,
        params string[] sourceKeys)
    {
        foreach (var key in sourceKeys)
        {
            if (TryReadNumber(source, key, out var value))
            {
                target[targetKey] = (int)Math.Clamp(Math.Round(value), 0, 10);
                return;
            }
        }
    }

    private static bool TryReadString(Dictionary<string, object> source, string key, out string value)
    {
        value = "";
        if (!source.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        value = raw.ToString()?.Trim() ?? "";
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryReadBoolean(Dictionary<string, object> source, string key, out bool value)
    {
        value = false;
        if (!source.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        if (raw is bool boolean)
        {
            value = boolean;
            return true;
        }

        return bool.TryParse(raw.ToString(), out value);
    }

    private static bool TryReadNumber(Dictionary<string, object> source, string key, out double value)
    {
        value = 0;
        if (!source.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        return raw switch
        {
            double d => SetNumber(d, out value),
            float f => SetNumber(f, out value),
            decimal m => SetNumber((double)m, out value),
            int i => SetNumber(i, out value),
            long l => SetNumber(l, out value),
            _ => double.TryParse(raw.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        };
    }

    private static bool SetNumber(double candidate, out double value)
    {
        value = candidate;
        return !double.IsNaN(candidate) && !double.IsInfinity(candidate);
    }

    private static List<string>? ReadStringList(Dictionary<string, object> source, string key)
    {
        if (!source.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        if (raw is IEnumerable<string> strings)
        {
            return strings
                .SelectMany(SplitListValue)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (raw is System.Collections.IEnumerable enumerable && raw is not string)
        {
            var values = new List<string>();
            foreach (var item in enumerable)
            {
                if (item is not null)
                {
                    values.AddRange(SplitListValue(item.ToString() ?? ""));
                }
            }

            return values
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var parsed = SplitListValue(raw.ToString() ?? "")
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return parsed.Count == 0 ? null : parsed;
    }

    private static IEnumerable<string> SplitListValue(string value)
        => value.Split([',', '\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
