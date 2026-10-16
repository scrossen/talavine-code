using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SwarmCore.LlmRouting.ProviderPacks;
using SwarmCore.LlmRouting.Providers;

namespace SwarmCore.LlmRouting.Pricing;

/// <summary>
/// Fetches model pricing from provider pricing pages using HTTP + regex parsing.
/// Falls back to reviewed provider-pack pricing when fetching or parsing fails.
///
/// Pricing sources:
///   - OpenAI: https://openai.com/api/pricing/
///   - Anthropic: https://docs.anthropic.com/en/docs/about-claude/models
///   - Google: https://ai.google.dev/pricing
///   - OpenRouter: https://openrouter.ai/api/v1/models
///
/// This service does NOT depend on ILlmRouter, avoiding circular dependencies.
/// </summary>
public class ProviderPricingFetcher : IModelPricingFetcher
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ProviderPricingFetcher> _logger;

    public ProviderPricingFetcher(
        IHttpClientFactory httpClientFactory,
        ILogger<ProviderPricingFetcher> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<Dictionary<string, ModelPricingEntry>> FetchAllPricingAsync(CancellationToken ct = default)
    {
        // Start with reviewed provider-pack fallback pricing as a base.
        var result = ProviderPackPricingProvider.GetAllPricing();

        // Overlay with dynamically fetched pricing (newer data wins)
        var providers = new[] { "OpenAI", "Claude", "Gemini", "OpenRouter" };
        foreach (var provider in providers)
        {
            try
            {
                var fetched = await FetchProviderPricingAsync(provider, ct);
                foreach (var (model, entry) in fetched)
                {
                    if (entry.InputCostPer1K > 0 || entry.OutputCostPer1K > 0)
                    {
                        result[model] = entry;
                    }
                }

                _logger.LogInformation("Fetched {Count} pricing entries from {Provider}", fetched.Count, provider);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch {Provider} pricing; using provider-pack fallback", provider);
            }
        }

        return result;
    }

    public Task<Dictionary<string, ModelPricingEntry>> FetchProviderPricingAsync(
        string providerName, CancellationToken ct = default)
    {
        return providerName.ToLowerInvariant() switch
        {
            "openai" => FetchOpenAiPricingAsync(ct),
            "claude" or "anthropic" => FetchClaudePricingAsync(ct),
            "gemini" or "google" => FetchGeminiPricingAsync(ct),
            "openrouter" => FetchOpenRouterPricingAsync(ct),
            _ => Task.FromResult(new Dictionary<string, ModelPricingEntry>(StringComparer.OrdinalIgnoreCase))
        };
    }

    private async Task<Dictionary<string, ModelPricingEntry>> FetchOpenRouterPricingAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, ModelPricingEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var http = _httpClientFactory.CreateClient("ModelPricingFetcher");
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; SwarmCore/1.0)");
            var pack = ProviderPackCatalog.GetRequired("OpenRouter").OpenRouter!;
            var modelsUri = $"{pack.BaseUrl.TrimEnd('/')}{pack.ModelsPath}?output_modalities={Uri.EscapeDataString(pack.CatalogOutputModalities)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, modelsUri);
            using var response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("HTTP {StatusCode} from OpenRouter models API", response.StatusCode);
                return result;
            }

            using var document = await OpenRouterCatalogContract.ReadBoundedDocumentAsync(
                response.Content,
                ct);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return result;
            }

            var now = DateTime.UtcNow;
            foreach (var item in data.EnumerateArray())
            {
                if (!OpenRouterCatalogContract.TryReadModel(item, out var model)
                    || model.PromptPricePerToken is not { } inputPerToken
                    || model.CompletionPricePerToken is not { } outputPerToken)
                {
                    continue;
                }

                result[model.Id] = new ModelPricingEntry(
                    inputPerToken * 1000m,
                    outputPerToken * 1000m,
                    null,
                    "openrouter:models",
                    now);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to fetch OpenRouter pricing from the models API");
        }

        return result;
    }

    /// <summary>
    /// Scrapes OpenAI pricing from their public pricing page.
    /// Looks for model names with per-1M token prices in input/output format.
    /// </summary>
    private async Task<Dictionary<string, ModelPricingEntry>> FetchOpenAiPricingAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, ModelPricingEntry>(StringComparer.OrdinalIgnoreCase);
        var html = await TryDownloadAsync("https://openai.com/api/pricing/", ct)
                ?? await TryDownloadAsync("https://platform.openai.com/docs/pricing", ct);

        if (html is null)
        {
            _logger.LogWarning("Could not download OpenAI pricing page");
            return result;
        }

        // Pattern: model name followed by input and output prices in $X.XX / 1M tokens format
        // Example: "gpt-5 ... Input: $1.250 / 1M tokens ... Output: $10.000 / 1M tokens"
        var pack = ProviderPackCatalog.GetRequired("OpenAI").DirectApi!;
        foreach (var (modelKey, facts) in pack.ModelFacts
                     .Where(entry => entry.Value.SupportsChat == true
                                     && entry.Value.InputCostPer1K.HasValue
                                     && entry.Value.OutputCostPer1K.HasValue))
        {
            foreach (var label in ReviewedPricingLabels(modelKey, facts))
            {
                if (result.ContainsKey(modelKey)) break;
                TryParseOpenAiModel(html, label, modelKey, result);
            }
        }

        return result;
    }

    private void TryParseOpenAiModel(string html, string label, string modelKey,
        Dictionary<string, ModelPricingEntry> result)
    {
        try
        {
            // Look for the model label followed by input/output prices
            var escapedLabel = Regex.Escape(label);
            var rx = new Regex(
                $@"{escapedLabel}[^$]{{0,500}}?\$(?<input>\d+(?:\.\d+)?)[^$]{{0,200}}?\$(?<cached>\d+(?:\.\d+)?)[^$]{{0,200}}?\$(?<output>\d+(?:\.\d+)?)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            var match = rx.Match(html);
            if (!match.Success) return;

            if (TryParseDecimal(match.Groups["input"].Value, out var inputPerM) &&
                TryParseDecimal(match.Groups["output"].Value, out var outputPerM))
            {
                // Convert per-1M to per-1K
                var inputPer1K = inputPerM / 1000m;
                var outputPer1K = outputPerM / 1000m;
                result[modelKey] = new ModelPricingEntry(inputPer1K, outputPer1K, null, "scraped:openai", DateTime.UtcNow);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse OpenAI pricing for {Model}", modelKey);
        }
    }

    /// <summary>
    /// Scrapes Anthropic Claude pricing from their models documentation page.
    /// Looks for model names with per-MTok prices.
    /// </summary>
    private async Task<Dictionary<string, ModelPricingEntry>> FetchClaudePricingAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, ModelPricingEntry>(StringComparer.OrdinalIgnoreCase);
        var html = await TryDownloadAsync("https://docs.anthropic.com/en/docs/about-claude/models", ct);

        if (html is null)
        {
            _logger.LogWarning("Could not download Anthropic pricing page");
            return result;
        }

        // Anthropic pricing format: "$X / MTok input" and "$X / MTok output"
        // or in tables: model name | input price | output price
        foreach (var modelKey in ProviderPackCatalog.GetRequired("Claude").DirectApi!.ModelFacts
                     .Where(entry => entry.Value.SupportsChat == true
                                     && entry.Value.InputCostPer1K.HasValue
                                     && entry.Value.OutputCostPer1K.HasValue)
                     .Select(entry => entry.Key))
        {
            TryParseClaudeModel(html, modelKey, result);
        }

        return result;
    }

    private void TryParseClaudeModel(string html, string modelKey,
        Dictionary<string, ModelPricingEntry> result)
    {
        try
        {
            // Pattern: model name followed by $X.XX / MTok input ... $X.XX / MTok output
            var escapedModel = Regex.Escape(modelKey);
            var rx = new Regex(
                $@"{escapedModel}[^$]{{0,500}}?\$(?<input>\d+(?:\.\d+)?)\s*/\s*MTok[^$]{{0,300}}?\$(?<output>\d+(?:\.\d+)?)\s*/\s*MTok",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            var match = rx.Match(html);
            if (!match.Success) return;

            if (TryParseDecimal(match.Groups["input"].Value, out var inputPerM) &&
                TryParseDecimal(match.Groups["output"].Value, out var outputPerM))
            {
                // Convert per-1M to per-1K
                var inputPer1K = inputPerM / 1000m;
                var outputPer1K = outputPerM / 1000m;
                result[modelKey] = new ModelPricingEntry(inputPer1K, outputPer1K, null, "scraped:anthropic", DateTime.UtcNow);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse Claude pricing for {Model}", modelKey);
        }
    }

    /// <summary>
    /// Scrapes Google Gemini pricing from their pricing page.
    /// Handles the _input/_output suffix format common in Gemini pricing.
    /// </summary>
    private async Task<Dictionary<string, ModelPricingEntry>> FetchGeminiPricingAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, ModelPricingEntry>(StringComparer.OrdinalIgnoreCase);
        var html = await TryDownloadAsync("https://ai.google.dev/pricing", ct);

        if (html is null)
        {
            _logger.LogWarning("Could not download Google AI pricing page");
            return result;
        }

        // Gemini pricing format: model name with input/output prices per 1M tokens
        var pack = ProviderPackCatalog.GetRequired("Gemini").DirectApi!;
        foreach (var (modelKey, facts) in pack.ModelFacts
                     .Where(entry => entry.Value.SupportsChat == true
                                     && entry.Value.InputCostPer1K.HasValue
                                     && entry.Value.OutputCostPer1K.HasValue))
        {
            foreach (var label in ReviewedPricingLabels(modelKey, facts))
            {
                if (result.ContainsKey(modelKey)) break;
                TryParseGeminiModel(html, label, modelKey, result);
            }
        }

        return result;
    }

    private void TryParseGeminiModel(string html, string label, string modelKey,
        Dictionary<string, ModelPricingEntry> result)
    {
        try
        {
            // Pattern: model label near input/output prices
            var escapedLabel = Regex.Escape(label);
            var rx = new Regex(
                $@"{escapedLabel}[^$]{{0,800}}?\$(?<input>\d+(?:\.\d+)?)[^$]{{0,300}}?\$(?<output>\d+(?:\.\d+)?)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            var match = rx.Match(html);
            if (!match.Success) return;

            if (TryParseDecimal(match.Groups["input"].Value, out var inputPerM) &&
                TryParseDecimal(match.Groups["output"].Value, out var outputPerM))
            {
                // Convert per-1M to per-1K
                var inputPer1K = inputPerM / 1000m;
                var outputPer1K = outputPerM / 1000m;
                result[modelKey] = new ModelPricingEntry(inputPer1K, outputPer1K, null, "scraped:google", DateTime.UtcNow);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse Gemini pricing for {Model}", modelKey);
        }
    }

    private async Task<string?> TryDownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            var http = _httpClientFactory.CreateClient("ModelPricingFetcher");
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; SwarmCore/1.0)");
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("HTTP {StatusCode} from {Url}", response.StatusCode, url);
                return null;
            }

            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to download {Url}", url);
            return null;
        }
    }

    private static bool TryParseDecimal(string value, out decimal result)
    {
        return decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out result);
    }

    private static IEnumerable<string> ReviewedPricingLabels(
        string modelKey,
        ProviderModelFacts facts)
    {
        yield return modelKey;
        if (!string.IsNullOrWhiteSpace(facts.DisplayName)
            && !facts.DisplayName.Equals(modelKey, StringComparison.OrdinalIgnoreCase))
        {
            yield return facts.DisplayName;
        }
    }

}
