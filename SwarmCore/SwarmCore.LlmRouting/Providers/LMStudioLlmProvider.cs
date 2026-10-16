using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Abstractions.Serialization;
using SwarmCore.LlmRouting.Configuration;

namespace SwarmCore.LlmRouting.Providers;

/// <summary>
/// LM Studio provider for the local OpenAI-compatible server. No API key is
/// required; users must have LM Studio running with the server enabled.
/// </summary>
public sealed class LMStudioLlmProvider : ILlmProvider, ILlmProviderExecutionContract
{
    private readonly OpenAICompatibleExecutionContract _executionContract =
        new(OpenAICompatibleContractFlavor.LMStudio);
    private static readonly TimeSpan ModelCacheDuration = TimeSpan.FromMinutes(2);

    private readonly ILogger<LMStudioLlmProvider> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LMStudioProviderOptions _options;
    private List<string>? _cachedModels;
    private DateTime _lastModelCheck = DateTime.MinValue;

    public LMStudioLlmProvider(
        ILogger<LMStudioLlmProvider> logger,
        IHttpClientFactory httpClientFactory,
        IOptions<LMStudioProviderOptions> options)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    public string ProviderName => "LMStudio";
    public LlmProviderExecutionContractDescriptor ExecutionContract => _executionContract.Descriptor;
    public LlmProviderExecutionTranslation TranslateExecution(
        string model,
        LlmGenerationParameters parameters,
        LlmRequestControl control,
        IReadOnlyDictionary<string, object>? providerExperimentalSettings = null)
        => _executionContract.Translate(model, parameters, control, providerExperimentalSettings);
    public bool SupportsStreaming => false;
    public bool SupportsEmbeddings => false;
    public string? DefaultEmbeddingModel => null;

    private string BaseUrl => NormalizeBaseUrl(_options.Endpoint);
    private string DefaultModel => string.IsNullOrWhiteSpace(_options.DefaultModel) ? "local-model" : _options.DefaultModel!;

    public bool SupportsModel(string model) => true;

    public bool SupportsVision(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        var lower = model.ToLowerInvariant();
        return lower.Contains("vision")
               || lower.Contains("llava")
               || lower.Contains("vl")
               || lower.Contains("moondream")
               || lower.Contains("minicpm-v");
    }

    public async Task<List<string>> GetAvailableModelsAsync()
    {
        if (_cachedModels is not null && DateTime.UtcNow - _lastModelCheck < ModelCacheDuration)
        {
            return _cachedModels.ToList();
        }

        if (IsLocalEndpointUnavailable())
        {
            _logger.LogDebug("LM Studio model discovery skipped; no local listener at {BaseUrl}", BaseUrl);
            return _cachedModels?.ToList() ?? [];
        }

        try
        {
            using var client = CreateClient(TimeSpan.FromSeconds(3));
            using var response = await client.GetAsync($"{BaseUrl}/models").ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("LM Studio /models returned {Status}", response.StatusCode);
                return _cachedModels?.ToList() ?? [];
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var modelResponse = JsonSerializer.Deserialize<LMStudioModelsResponse>(json, SwarmJsonOptions.ExternalApi);
            var models = modelResponse?.Data?
                .Select(m => m.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];

            _cachedModels = models!;
            _lastModelCheck = DateTime.UtcNow;
            return _cachedModels.ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogDebug(ex, "LM Studio model discovery failed");
            return _cachedModels?.ToList() ?? [];
        }
    }

    public async Task<LlmResponse> GenerateResponseAsync(
        string prompt,
        string model,
        Dictionary<string, object>? executionSettings = null)
    {
        var requestCancellation =
            LlmReservedExecutionSettings.GetRequestCancellationToken(executionSettings);
        requestCancellation.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        var effectiveModel = await ResolveModelAsync(model).ConfigureAwait(false);

        try
        {
            using var client = CreateClient(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 5, 3600)));
            var request = new LMStudioChatRequest
            {
                Model = effectiveModel,
                Messages =
                [
                    new() { Role = "user", Content = PromptCaching.Strip(prompt) }
                ]
            };
            ApplyExecutionSettings(request, executionSettings);

            var json = JsonSerializer.Serialize(request);
            using var response = await client.PostAsync(
                $"{BaseUrl}/chat/completions",
                new StringContent(json, Encoding.UTF8, "application/json"),
                requestCancellation).ConfigureAwait(false);

            var responseContent = await response.Content
                .ReadAsStringAsync(requestCancellation)
                .ConfigureAwait(false);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return CreateErrorResponse(
                    effectiveModel,
                    $"LM Studio API error: {(int)response.StatusCode} {response.StatusCode} - {responseContent}",
                    (int)stopwatch.ElapsedMilliseconds);
            }

            var completion = JsonSerializer.Deserialize<LMStudioCompletionResponse>(responseContent, SwarmJsonOptions.ExternalApi);
            var text = completion?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
            return new LlmResponse
            {
                Content = text,
                Provider = ProviderName,
                Model = effectiveModel,
                InputTokens = completion?.Usage?.PromptTokens ?? EstimateTokens(prompt),
                OutputTokens = completion?.Usage?.CompletionTokens ?? EstimateTokens(text),
                TotalTokens = completion?.Usage?.TotalTokens ?? 0,
                ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
                WasSuccessful = true
            };
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "LM Studio completion failed");
            return CreateErrorResponse(effectiveModel, ex.Message, (int)stopwatch.ElapsedMilliseconds);
        }
    }

    public async Task<LlmResponse> GenerateVisionResponseAsync(
        string prompt,
        string imageBase64,
        string model,
        Dictionary<string, object>? executionSettings = null)
    {
        var requestCancellation =
            LlmReservedExecutionSettings.GetRequestCancellationToken(executionSettings);
        requestCancellation.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        var effectiveModel = await ResolveModelAsync(model, preferVision: true).ConfigureAwait(false);
        if (!SupportsVision(effectiveModel))
        {
            return CreateErrorResponse(effectiveModel, "No LM Studio vision-capable model is loaded.", 0);
        }

        try
        {
            using var client = CreateClient(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 5, 3600)));
            var imageUrl = imageBase64.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? imageBase64
                : $"data:image/jpeg;base64,{CleanBase64(imageBase64)}";

            var request = new
            {
                model = effectiveModel,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "text", text = prompt },
                            new { type = "image_url", image_url = new { url = imageUrl } }
                        }
                    }
                },
                max_tokens = 4096
            };

            var json = JsonSerializer.Serialize(request);
            using var response = await client.PostAsync(
                $"{BaseUrl}/chat/completions",
                new StringContent(json, Encoding.UTF8, "application/json"),
                requestCancellation).ConfigureAwait(false);

            var responseContent = await response.Content
                .ReadAsStringAsync(requestCancellation)
                .ConfigureAwait(false);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return CreateErrorResponse(effectiveModel, $"LM Studio vision API error: {response.StatusCode}", (int)stopwatch.ElapsedMilliseconds);
            }

            var completion = JsonSerializer.Deserialize<LMStudioCompletionResponse>(responseContent, SwarmJsonOptions.ExternalApi);
            var text = completion?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
            return new LlmResponse
            {
                Content = text,
                Provider = ProviderName,
                Model = effectiveModel,
                InputTokens = completion?.Usage?.PromptTokens ?? 0,
                OutputTokens = completion?.Usage?.CompletionTokens ?? EstimateTokens(text),
                TotalTokens = completion?.Usage?.TotalTokens ?? 0,
                ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
                WasSuccessful = true
            };
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            stopwatch.Stop();
            return CreateErrorResponse(effectiveModel, ex.Message, (int)stopwatch.ElapsedMilliseconds);
        }
    }

    public async Task<(bool IsHealthy, string Message)> TestConnectivityAsync()
    {
        if (IsLocalEndpointUnavailable())
        {
            return (false, $"LM Studio is not listening at {BaseUrl}.");
        }

        try
        {
            using var client = CreateClient(TimeSpan.FromSeconds(3));
            using var response = await client.GetAsync($"{BaseUrl}/models").ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (false, $"LM Studio returned {response.StatusCode}");
            }

            var models = await GetAvailableModelsAsync().ConfigureAwait(false);
            return models.Count > 0
                ? (true, $"LM Studio is reachable with {models.Count} loaded model(s)")
                : (false, "LM Studio is reachable, but no models are loaded.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (false, $"Error connecting to LM Studio: {ex.Message}");
        }
    }

    public Task<LlmResponse> GenerateStreamingResponseAsync(
        string prompt,
        string model,
        Func<string, Task> onChunk,
        Dictionary<string, object>? executionSettings = null)
        => throw new NotSupportedException("LM Studio streaming is not supported by this provider.");

    public Task<LlmEmbeddingResponse> GenerateEmbeddingAsync(
        string text,
        string model,
        Dictionary<string, object>? executionSettings = null)
        => throw new NotSupportedException("LM Studio embeddings are not supported by this provider.");

    public Task<List<LlmEmbeddingResponse>> GenerateEmbeddingsAsync(
        IList<string> texts,
        string model,
        Dictionary<string, object>? executionSettings = null)
        => throw new NotSupportedException("LM Studio embeddings are not supported by this provider.");

    private async Task<string> ResolveModelAsync(string model, bool preferVision = false)
    {
        if (!string.IsNullOrWhiteSpace(model) && !model.Equals("local-model", StringComparison.OrdinalIgnoreCase))
            return model;

        var models = await GetAvailableModelsAsync().ConfigureAwait(false);
        if (preferVision)
        {
            var vision = models.FirstOrDefault(SupportsVision);
            if (!string.IsNullOrWhiteSpace(vision)) return vision;
        }

        return models.FirstOrDefault() ?? DefaultModel;
    }

    private HttpClient CreateClient(TimeSpan timeout)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = timeout;
        return client;
    }

    internal static void ApplyExecutionSettings(LMStudioChatRequest request, Dictionary<string, object>? executionSettings)
    {
        if (executionSettings is null) return;

        if (executionSettings.TryGetValue("temperature", out var temp))
            request.Temperature = Convert.ToDouble(temp);
        if (executionSettings.TryGetValue("top_p", out var topP))
            request.TopP = Convert.ToDouble(topP);
        if (executionSettings.TryGetValue("max_tokens", out var maxTokens))
            request.MaxTokens = Convert.ToInt32(maxTokens);
        if (executionSettings.TryGetValue("response_format", out var responseFormat))
            request.ResponseFormat = responseFormat;
    }

    private static string NormalizeBaseUrl(string? endpoint)
    {
        var value = string.IsNullOrWhiteSpace(endpoint)
            ? "http://127.0.0.1:1234/v1"
            : endpoint.Trim().TrimEnd('/');
        return value.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? value
            : $"{value}/v1";
    }

    private bool IsLocalEndpointUnavailable()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || !IsLoopbackHost(uri.Host))
        {
            return false;
        }

        var port = uri.IsDefaultPort
            ? string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80
            : uri.Port;

        try
        {
            return !IPGlobalProperties
                .GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(endpoint => endpoint.Port == port && IsLoopbackListener(endpoint.Address));
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "LM Studio listener probe failed; falling back to HTTP discovery");
            return false;
        }
    }

    private static bool IsLoopbackHost(string host)
    {
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
               || string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
               || string.Equals(host, "::1", StringComparison.Ordinal);
    }

    private static bool IsLoopbackListener(IPAddress address)
    {
        return IPAddress.IsLoopback(address)
               || address.Equals(IPAddress.Any)
               || address.Equals(IPAddress.IPv6Any);
    }

    private static string CleanBase64(string value)
    {
        var comma = value.IndexOf(',');
        return comma >= 0 ? value[(comma + 1)..] : value;
    }

    private static int EstimateTokens(string text) => string.IsNullOrEmpty(text) ? 0 : text.Length / 4;

    private LlmResponse CreateErrorResponse(string model, string errorMessage, int responseTimeMs) => new()
    {
        Content = string.Empty,
        Provider = ProviderName,
        Model = model,
        WasSuccessful = false,
        ErrorMessage = errorMessage,
        ResponseTimeMs = responseTimeMs
    };
}

internal sealed class LMStudioChatRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("messages")]
    public List<LMStudioMessage> Messages { get; set; } = [];

    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TopP { get; set; }

    [JsonPropertyName("max_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("response_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? ResponseFormat { get; set; }
}

internal sealed class LMStudioMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
}

internal sealed class LMStudioCompletionResponse
{
    [JsonPropertyName("choices")]
    public List<LMStudioChoice>? Choices { get; set; }

    [JsonPropertyName("usage")]
    public LMStudioUsage? Usage { get; set; }
}

internal sealed class LMStudioChoice
{
    [JsonPropertyName("message")]
    public LMStudioMessage? Message { get; set; }
}

internal sealed class LMStudioUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }
}

internal sealed class LMStudioModelsResponse
{
    [JsonPropertyName("data")]
    public List<LMStudioModelEntry>? Data { get; set; }
}

internal sealed class LMStudioModelEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}
