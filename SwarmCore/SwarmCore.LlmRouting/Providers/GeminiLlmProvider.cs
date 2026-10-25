using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Abstractions.Serialization;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.ProviderPacks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SwarmCore.LlmRouting.Providers;

/// <summary>
/// Google Gemini LLM provider implementation for SwarmCore.
/// </summary>
public class GeminiLlmProvider : LlmProviderBase, ILlmModelCatalogProvider
{
    private static readonly DirectApiProviderPack Pack =
        ProviderPackCatalog.GetRequired("Gemini").DirectApi!;
    private readonly GeminiProviderOptions _options;
    private List<string>? _cachedModels;

    public override string ProviderName => "Gemini";
    public override bool SupportsStreaming => true;
    public override bool SupportsEmbeddings => true;
    public override string? DefaultEmbeddingModel => _options.DefaultEmbeddingModel ?? Pack.DefaultEmbeddingModel;

    protected override IReadOnlyList<string> DefaultModels => Pack.DefaultModels;

    protected override string DefaultModel => _options.DefaultModel ?? Pack.DefaultModel;

    protected override IReadOnlyList<string> VisionModels => Pack.VisionModelPatterns;

    public GeminiLlmProvider(
        ILogger<GeminiLlmProvider> logger,
        ILlmApiKeyProvider apiKeyProvider,
        IHttpClientFactory httpClientFactory,
        IOptions<GeminiProviderOptions> options)
        : base(logger, apiKeyProvider, httpClientFactory)
    {
        _options = options.Value;
    }

    public override Task<List<string>> GetAvailableModelsAsync() => DiscoverModelsAsync(false, CancellationToken.None);

    public async Task<LlmModelCatalog> GetModelCatalogAsync(CancellationToken cancellationToken = default)
        => new(await DiscoverModelsAsync(true, cancellationToken).ConfigureAwait(false), "Provider API catalog");

    private async Task<List<string>> DiscoverModelsAsync(bool strict, CancellationToken ct)
    {
        var apiKey = await ApiKeyProvider.GetApiKeyAsync(ProviderName);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Logger.LogTrace("Gemini model discovery skipped because no API key is configured; using defaults");
            if (strict) throw new InvalidOperationException("The provider model catalog is unavailable. Check the connection and refresh, or enter a custom model ID.");
            return _cachedModels ?? DefaultModels.ToList();
        }

        try
        {
            using var client = HttpClientFactory.CreateClient();
            ApplyApiKeyHeader(client, apiKey);
            var models = new List<string>();
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            string? token = null;
            do
            {
                var url = BuildApiUrl("models" + (token is null ? "" : "?pageToken=" + Uri.EscapeDataString(token)));
                using var response = await client.GetAsync(url, ct);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(ct);
                var page = JsonSerializer.Deserialize<GeminiModelsResponse>(json, SwarmJsonOptions.ExternalApi);
                if (page?.Models is null) throw new InvalidDataException("The provider returned an invalid model catalog.");
                models.AddRange(page.Models.Select(m => m.Name).OfType<string>().Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n.StartsWith("models/", StringComparison.Ordinal) ? n[7..] : n));
                token = string.IsNullOrEmpty(page.NextPageToken) ? null : page.NextPageToken;
                if (token is not null && !tokens.Add(token)) throw new InvalidDataException("The provider returned an invalid model catalog cursor.");
            } while (token is not null);
            _cachedModels = models.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
            Logger.LogInformation("Gemini: fetched {Count} models from API", _cachedModels.Count);
            return _cachedModels;
        }
        catch (Exception ex)
        {
            if (strict) throw new InvalidOperationException("Could not load the provider model catalog. Check the connection and refresh, or enter a custom model ID.", ex);
            Logger.LogDebug(ex, "Failed to fetch Gemini model list from API; using defaults");
        }

        if (strict) throw new InvalidOperationException("The provider model catalog is unavailable. Check the connection and refresh, or enter a custom model ID.");
        return _cachedModels ?? DefaultModels.ToList();
    }

    public override bool SupportsModel(string model)
    {
        if (_cachedModels is not null)
            return _cachedModels.Contains(model, StringComparer.OrdinalIgnoreCase);
        return base.SupportsModel(model);
    }

    public override async Task<LlmResponse> GenerateResponseAsync(string prompt, string model,
        Dictionary<string, object>? executionSettings = null)
    {
        var requestCancellation =
            LlmReservedExecutionSettings.GetRequestCancellationToken(executionSettings);
        requestCancellation.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var apiKey = await GetApiKeyAsync();
            var effectiveModel = string.IsNullOrEmpty(model) ? DefaultModel : model;

            using var client = HttpClientFactory.CreateClient();
            ApplyApiKeyHeader(client, apiKey);
            var url = BuildApiUrl($"models/{Uri.EscapeDataString(effectiveModel)}:generateContent");

            // Gemini doesn't expose explicit cache-control directives the way
            // Anthropic does, so strip the shared boundary marker rather than
            // leaking it into the prompt.
            var strippedPrompt = PromptCaching.Strip(prompt);

            var request = new GeminiRequest
            {
                Contents = new List<GeminiContent>
                {
                    new() { Parts = new List<GeminiPart> { new() { Text = strippedPrompt } } }
                }
            };

            ApplyExecutionSettings(request, executionSettings);

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync(url, content, requestCancellation);
            var responseContent = await response.Content.ReadAsStringAsync(requestCancellation);

            stopwatch.Stop();

            if (response.IsSuccessStatusCode)
            {
                var geminiResponse = JsonSerializer.Deserialize<GeminiResponse>(responseContent, SwarmJsonOptions.ExternalApi);
                var responseText = CandidateText(geminiResponse);
                var usage = geminiResponse?.UsageMetadata;

                return new LlmResponse
                {
                    Content = responseText,
                    Provider = ProviderName,
                    Model = NormalizeReturnedModel(geminiResponse?.ModelVersion),
                    InputTokens = usage?.PromptTokenCount ?? EstimateTokens(prompt),
                    OutputTokens = GetBillableOutputTokens(usage, EstimateTokens(responseText)),
                    TotalTokens = usage?.TotalTokenCount ?? 0,
                    UsageMeasurementComplete = HasCompleteUsageMeasurement(usage),
                    ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
                    WasSuccessful = true
                };
            }

            Logger.LogError("Gemini API error: {StatusCode} - {Content}", response.StatusCode, responseContent);
            var apiError = TryExtractGeminiErrorMessage(responseContent);
            return CreateErrorResponse(effectiveModel, $"API error: {response.StatusCode}" +
                (string.IsNullOrWhiteSpace(apiError) ? string.Empty : $" - {apiError}"),
                (int)stopwatch.ElapsedMilliseconds, response.StatusCode);
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Logger.LogError(ex, "Error generating response with Gemini");
            return CreateErrorResponse(model, ex.Message, (int)stopwatch.ElapsedMilliseconds);
        }
    }

    public override async Task<LlmResponse> GenerateVisionResponseAsync(string prompt, string imageBase64, string model,
        Dictionary<string, object>? executionSettings = null)
    {
        var requestCancellation =
            LlmReservedExecutionSettings.GetRequestCancellationToken(executionSettings);
        requestCancellation.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var apiKey = await GetApiKeyAsync();
            var effectiveModel = string.IsNullOrEmpty(model) ? DefaultModel : model;
            var mimeType = DetectMediaType(imageBase64);

            using var client = HttpClientFactory.CreateClient();
            ApplyApiKeyHeader(client, apiKey);
            var url = BuildApiUrl($"models/{Uri.EscapeDataString(effectiveModel)}:generateContent");

            var request = new GeminiRequest
            {
                Contents = new List<GeminiContent>
                {
                    new()
                    {
                        Parts = new List<GeminiPart>
                        {
                            new() { Text = prompt },
                            new() { InlineData = new GeminiInlineData { MimeType = mimeType, Data = imageBase64 } }
                        }
                    }
                }
            };

            ApplyExecutionSettings(request, executionSettings);

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync(url, content, requestCancellation);
            var responseContent = await response.Content.ReadAsStringAsync(requestCancellation);

            stopwatch.Stop();

            if (response.IsSuccessStatusCode)
            {
                var geminiResponse = JsonSerializer.Deserialize<GeminiResponse>(responseContent, SwarmJsonOptions.ExternalApi);
                var responseText = CandidateText(geminiResponse);
                var usage = geminiResponse?.UsageMetadata;

                return new LlmResponse
                {
                    Content = responseText,
                    Provider = ProviderName,
                    Model = NormalizeReturnedModel(geminiResponse?.ModelVersion),
                    InputTokens = usage?.PromptTokenCount ?? 0,
                    OutputTokens = GetBillableOutputTokens(usage, 0),
                    TotalTokens = usage?.TotalTokenCount ?? 0,
                    UsageMeasurementComplete = HasCompleteUsageMeasurement(usage),
                    ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
                    WasSuccessful = true
                };
            }

            Logger.LogError("Gemini vision API error: {StatusCode} - {Content}", response.StatusCode, responseContent);
            var apiError = TryExtractGeminiErrorMessage(responseContent);
            return CreateErrorResponse(effectiveModel, $"API error: {response.StatusCode}" +
                (string.IsNullOrWhiteSpace(apiError) ? string.Empty : $" - {apiError}"),
                (int)stopwatch.ElapsedMilliseconds, response.StatusCode);
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Logger.LogError(ex, "Error generating vision response with Gemini");
            return CreateErrorResponse(model, ex.Message, (int)stopwatch.ElapsedMilliseconds);
        }
    }

    public override async Task<LlmResponse> GenerateStreamingResponseAsync(string prompt, string model,
        Func<string, Task> onChunk, Dictionary<string, object>? executionSettings = null)
    {
        var requestCancellation =
            LlmReservedExecutionSettings.GetRequestCancellationToken(executionSettings);
        requestCancellation.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        var effectiveModel = string.IsNullOrEmpty(model) ? DefaultModel : model;

        try
        {
            var apiKey = await GetApiKeyAsync();
            using var client = HttpClientFactory.CreateClient();
            ApplyApiKeyHeader(client, apiKey);
            if (requestCancellation.CanBeCanceled)
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
            }

            var url = BuildApiUrl($"models/{Uri.EscapeDataString(effectiveModel)}:streamGenerateContent?alt=sse");
            var request = new GeminiRequest
            {
                Contents = new List<GeminiContent>
                {
                    new() { Parts = new List<GeminiPart> { new() { Text = PromptCaching.Strip(prompt) } } }
                }
            };
            ApplyExecutionSettings(request, executionSettings);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json")
            };
            using var response = await client.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                requestCancellation);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(requestCancellation);
                stopwatch.Stop();
                Logger.LogError("Gemini streaming API error: {StatusCode} - {Content}", response.StatusCode, errorBody);
                var apiError = TryExtractGeminiErrorMessage(errorBody);
                return CreateErrorResponse(effectiveModel, $"API error: {response.StatusCode}" +
                    (string.IsNullOrWhiteSpace(apiError) ? string.Empty : $" - {apiError}"),
                    (int)stopwatch.ElapsedMilliseconds, response.StatusCode);
            }

            // With alt=sse each data record is a complete GenerateContentResponse
            // carrying incremental parts; usageMetadata on the last record is
            // cumulative for the whole generation.
            await using var body = await response.Content.ReadAsStreamAsync(requestCancellation);
            var content = new StringBuilder();
            string? responseModel = null;
            GeminiUsageMetadata? usage = null;

            await foreach (var sse in ToolUse.SseParser.ParseAsync(body, requestCancellation))
            {
                GeminiResponse? chunk;
                try { chunk = JsonSerializer.Deserialize<GeminiResponse>(sse.Data, SwarmJsonOptions.ExternalApi); }
                catch (JsonException) { continue; }
                if (chunk is null) continue;

                responseModel ??= chunk.ModelVersion;
                if (chunk.UsageMetadata is not null)
                {
                    usage = chunk.UsageMetadata;
                }

                var parts = chunk.Candidates?.FirstOrDefault()?.Content?.Parts;
                if (parts is null) continue;
                foreach (var part in parts)
                {
                    if (part.Thought == true || string.IsNullOrEmpty(part.Text)) continue;
                    content.Append(part.Text);
                    await onChunk(part.Text).ConfigureAwait(false);
                }
            }

            stopwatch.Stop();
            var responseText = content.ToString();
            return new LlmResponse
            {
                Content = responseText,
                Provider = ProviderName,
                Model = NormalizeReturnedModel(responseModel),
                InputTokens = usage?.PromptTokenCount ?? EstimateTokens(prompt),
                OutputTokens = GetBillableOutputTokens(usage, EstimateTokens(responseText)),
                TotalTokens = usage?.TotalTokenCount ?? 0,
                UsageMeasurementComplete = HasCompleteUsageMeasurement(usage),
                ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
                WasSuccessful = true
            };
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Logger.LogError(ex, "Error generating streaming response with Gemini");
            return CreateErrorResponse(effectiveModel, ex.Message, (int)stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Gemini accepts the API key as the <c>x-goog-api-key</c> header. Sending
    /// it there instead of as a <c>?key=</c> query parameter keeps the secret out
    /// of URL logs, proxies, and sanitized diagnostics.
    /// </summary>
    private static void ApplyApiKeyHeader(HttpClient client, string apiKey)
    {
        client.DefaultRequestHeaders.Remove("x-goog-api-key");
        client.DefaultRequestHeaders.Add("x-goog-api-key", apiKey);
    }

    public override async Task<(bool IsHealthy, string Message)> TestConnectivityAsync()
    {
        try
        {
            var apiKey = await GetApiKeyAsync();
            using var client = HttpClientFactory.CreateClient();
            ApplyApiKeyHeader(client, apiKey);
            var url = BuildApiUrl("models");

            var response = await client.GetAsync(url);

            if (response.IsSuccessStatusCode)
                return (true, "Gemini API is reachable");

            return (false, $"Gemini API returned {response.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, $"Error connecting to Gemini: {ex.Message}");
        }
    }

    private static void ApplyExecutionSettings(GeminiRequest request, Dictionary<string, object>? executionSettings)
    {
        if (executionSettings == null) return;

        request.GenerationConfig ??= new GeminiGenerationConfig();

        if (executionSettings.TryGetValue("temperature", out var temp))
            request.GenerationConfig.Temperature = Convert.ToDouble(temp);

        if (executionSettings.TryGetValue("max_tokens", out var maxTokens))
            request.GenerationConfig.MaxOutputTokens = Convert.ToInt32(maxTokens);

        if (executionSettings.TryGetValue("thinking_budget", out var thinkingBudget))
        {
            request.GenerationConfig.ThinkingConfig = new GeminiThinkingConfig
            {
                ThinkingBudget = Convert.ToInt32(thinkingBudget)
            };
        }
    }

    /// <summary>
    /// The answer text of the first candidate. Gemini can split a reply across
    /// several parts and put a function-call or code part first; every visible
    /// text part counts, as in the streaming path.
    /// </summary>
    private static string CandidateText(GeminiResponse? response)
        => string.Concat(response?.Candidates?.FirstOrDefault()?.Content?.Parts?
            .Where(part => part.Thought != true)
            .Select(part => part.Text) ?? []);

    private static int GetBillableOutputTokens(GeminiUsageMetadata? usage, int fallback)
    {
        if (usage?.PromptTokenCount is { } prompt
            && usage.TotalTokenCount is { } total
            && total >= prompt)
        {
            // totalTokenCount includes hidden thought tokens while
            // candidatesTokenCount covers visible candidate text only.
            return Math.Max(usage.CandidatesTokenCount ?? 0, total - prompt);
        }

        return usage?.CandidatesTokenCount ?? fallback;
    }

    private static bool HasCompleteUsageMeasurement(GeminiUsageMetadata? usage)
        => usage is
           {
               PromptTokenCount: { } prompt,
               CandidatesTokenCount: { } candidates,
               TotalTokenCount: { } total
           }
           && total >= prompt + candidates;

    private static string? TryExtractGeminiErrorMessage(string? responseContent)
    {
        if (string.IsNullOrWhiteSpace(responseContent))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseContent);
            if (doc.RootElement.TryGetProperty("error", out var errorElement)
                && errorElement.TryGetProperty("message", out var messageElement))
            {
                return messageElement.GetString();
            }
        }
        catch
        {
            // Ignore JSON parsing failures; keep generic status message.
        }

        return null;
    }

    private static string NormalizeReturnedModel(string? model) =>
        string.IsNullOrWhiteSpace(model) ? string.Empty : model.Trim();

    private static string DetectMediaType(string base64Data)
    {
        if (base64Data.StartsWith("/9j/")) return "image/jpeg";
        if (base64Data.StartsWith("iVBORw")) return "image/png";
        if (base64Data.StartsWith("R0lGOD")) return "image/gif";
        if (base64Data.StartsWith("UklGR")) return "image/webp";
        if (base64Data.StartsWith("AAAA")) return "video/mp4";
        return "image/jpeg";
    }

    private string BuildApiUrl(string relative)
    {
        var baseUrl = string.IsNullOrWhiteSpace(_options.Endpoint)
            ? Pack.BaseUrl
            : _options.Endpoint!.Trim();
        return $"{baseUrl.TrimEnd('/')}/{relative.TrimStart('/')}";
    }
}

#region Gemini API Models

internal class GeminiRequest
{
    [JsonPropertyName("contents")]
    public List<GeminiContent> Contents { get; set; } = new();

    [JsonPropertyName("generationConfig")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeminiGenerationConfig? GenerationConfig { get; set; }
}

internal class GeminiContent
{
    [JsonPropertyName("parts")]
    public List<GeminiPart> Parts { get; set; } = new();
}

internal class GeminiPart
{
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonPropertyName("inline_data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeminiInlineData? InlineData { get; set; }

    /// <summary>Set on thought-summary parts, which are not part of the answer.</summary>
    [JsonPropertyName("thought")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Thought { get; set; }
}

internal class GeminiInlineData
{
    [JsonPropertyName("mime_type")]
    public string MimeType { get; set; } = "";

    [JsonPropertyName("data")]
    public string Data { get; set; } = "";
}

internal class GeminiGenerationConfig
{
    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; set; }

    [JsonPropertyName("maxOutputTokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxOutputTokens { get; set; }

    [JsonPropertyName("thinkingConfig")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GeminiThinkingConfig? ThinkingConfig { get; set; }
}

internal class GeminiThinkingConfig
{
    [JsonPropertyName("thinkingBudget")]
    public int ThinkingBudget { get; set; }
}

internal class GeminiResponse
{
    [JsonPropertyName("modelVersion")]
    public string? ModelVersion { get; set; }

    [JsonPropertyName("candidates")]
    public List<GeminiCandidate>? Candidates { get; set; }

    [JsonPropertyName("usageMetadata")]
    public GeminiUsageMetadata? UsageMetadata { get; set; }
}

internal class GeminiCandidate
{
    [JsonPropertyName("content")]
    public GeminiContent? Content { get; set; }
}

internal class GeminiUsageMetadata
{
    [JsonPropertyName("promptTokenCount")]
    public int? PromptTokenCount { get; set; }

    [JsonPropertyName("candidatesTokenCount")]
    public int? CandidatesTokenCount { get; set; }

    [JsonPropertyName("totalTokenCount")]
    public int? TotalTokenCount { get; set; }

    [JsonPropertyName("thoughtsTokenCount")]
    public int? ThoughtsTokenCount { get; set; }
}

internal class GeminiModelsResponse
{
    [JsonPropertyName("nextPageToken")] public string? NextPageToken { get; set; }
    [JsonPropertyName("models")]
    public List<GeminiModelEntry>? Models { get; set; }
}

internal class GeminiModelEntry
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

#endregion
