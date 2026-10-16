using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Configuration;
using Microsoft.Extensions.Logging;

namespace SwarmCore.LlmRouting.Providers;

/// <summary>
/// Base class for LLM providers with common functionality.
/// Does not depend on any specific database or persistence layer.
/// </summary>
public abstract class LlmProviderBase : ILlmProvider
{
    protected readonly ILogger Logger;
    protected readonly ILlmApiKeyProvider ApiKeyProvider;
    protected readonly IHttpClientFactory HttpClientFactory;

    public abstract string ProviderName { get; }
    public abstract bool SupportsStreaming { get; }
    public abstract bool SupportsEmbeddings { get; }
    public abstract string? DefaultEmbeddingModel { get; }

    /// <summary>
    /// Default models supported by this provider
    /// </summary>
    protected abstract IReadOnlyList<string> DefaultModels { get; }

    /// <summary>
    /// Default model to use when none specified
    /// </summary>
    protected abstract string DefaultModel { get; }

    /// <summary>
    /// Models that support vision/multimodal
    /// </summary>
    protected abstract IReadOnlyList<string> VisionModels { get; }

    /// <summary>
    /// Models in a vision-capable family that specifically do NOT support vision
    /// (e.g. gpt-5-nano substring-matches the "gpt-5" vision entry but can't take
    /// image inputs). Checked before the inclusion list so exclusions win.
    /// </summary>
    protected virtual IReadOnlyList<string> NonVisionModels { get; } = Array.Empty<string>();

    protected LlmProviderBase(
        ILogger logger,
        ILlmApiKeyProvider apiKeyProvider,
        IHttpClientFactory httpClientFactory)
    {
        Logger = logger;
        ApiKeyProvider = apiKeyProvider;
        HttpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Get the API key for this provider
    /// </summary>
    protected virtual bool RequiresApiKey => true;

    protected async Task<string> GetApiKeyAsync()
    {
        if (!RequiresApiKey) return string.Empty;
        var key = await ApiKeyProvider.GetApiKeyAsync(ProviderName);
        if (string.IsNullOrEmpty(key))
        {
            throw new InvalidOperationException($"No API key configured for provider {ProviderName}");
        }
        return key;
    }

    public virtual bool SupportsModel(string model)
    {
        return DefaultModels.Contains(model, StringComparer.OrdinalIgnoreCase);
    }

    public virtual bool SupportsVision(string model)
    {
        if (string.IsNullOrEmpty(model))
            return false;

        // Explicit non-vision exclusions (checked first — exclusions win over
        // the inclusion substring match).
        if (NonVisionModels.Any(nm => model.Contains(nm, StringComparison.OrdinalIgnoreCase)))
            return false;

        // Check explicit vision models list
        if (VisionModels.Any(vm => model.Contains(vm, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Use pattern matching for common vision model naming conventions
        var lowerModel = model.ToLowerInvariant();
        return lowerModel.Contains("vision") ||
               lowerModel.Contains("4o") ||           // OpenAI GPT-4o series
               lowerModel.Contains("o1") ||           // OpenAI o1 series
               lowerModel.Contains("o3") ||           // OpenAI o3 series
               lowerModel.Contains("claude-3") ||     // Claude 3 series
               lowerModel.Contains("claude-sonnet-4") ||  // Claude 4 sonnet
               lowerModel.Contains("claude-opus-4") ||    // Claude 4 opus
               lowerModel.Contains("claude-haiku-4") ||   // Claude 4 haiku
               lowerModel.Contains("gemini-1.5") ||   // Gemini 1.5 series
               lowerModel.Contains("gemini-2") ||     // Gemini 2.x series
               lowerModel.Contains("gemini-flash") || // Gemini Flash variants
               lowerModel.Contains("gemini-pro");     // Gemini Pro variants
    }

    public virtual Task<List<string>> GetAvailableModelsAsync()
    {
        return Task.FromResult(DefaultModels.ToList());
    }

    public abstract Task<LlmResponse> GenerateResponseAsync(string prompt, string model,
        Dictionary<string, object>? executionSettings = null);

    public abstract Task<LlmResponse> GenerateVisionResponseAsync(string prompt, string imageBase64, string model,
        Dictionary<string, object>? executionSettings = null);

    public abstract Task<(bool IsHealthy, string Message)> TestConnectivityAsync();

    public virtual Task<LlmResponse> GenerateStreamingResponseAsync(string prompt, string model,
        Func<string, Task> onChunk, Dictionary<string, object>? executionSettings = null)
    {
        throw new NotSupportedException($"Provider {ProviderName} does not support streaming");
    }

    public virtual Task<LlmEmbeddingResponse> GenerateEmbeddingAsync(
        string text,
        string model,
        Dictionary<string, object>? executionSettings = null)
    {
        throw new NotSupportedException($"Provider {ProviderName} does not support embeddings");
    }

    public virtual Task<List<LlmEmbeddingResponse>> GenerateEmbeddingsAsync(
        IList<string> texts,
        string model,
        Dictionary<string, object>? executionSettings = null)
    {
        throw new NotSupportedException($"Provider {ProviderName} does not support embeddings");
    }

    /// <summary>
    /// Create an error response
    /// </summary>
    protected LlmResponse CreateErrorResponse(string model, string errorMessage, int responseTimeMs = 0)
        => CreateErrorResponse(model, errorMessage, responseTimeMs, httpStatusCode: null);

    /// <summary>
    /// Create an error response that carries the provider's HTTP status
    /// structurally, so router classification never has to guess the status
    /// from digits embedded in the message text.
    /// </summary>
    protected LlmResponse CreateErrorResponse(
        string model,
        string errorMessage,
        int responseTimeMs,
        System.Net.HttpStatusCode? httpStatusCode)
    {
        return new LlmResponse
        {
            Content = string.Empty,
            Provider = ProviderName,
            Model = model,
            WasSuccessful = false,
            ErrorMessage = errorMessage,
            ResponseTimeMs = responseTimeMs,
            HttpStatusCode = httpStatusCode is { } status ? (int)status : null
        };
    }

    /// <summary>
    /// Estimate token count from text (rough approximation)
    /// </summary>
    protected static int EstimateTokens(string text)
    {
        // Rough estimate: ~4 characters per token for English
        return text.Length / 4;
    }
}

