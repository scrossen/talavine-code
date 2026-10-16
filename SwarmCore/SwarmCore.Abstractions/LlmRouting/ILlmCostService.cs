namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Model cost information (price per 1K tokens)
/// </summary>
public class LlmModelCost
{
    public required string ModelKey { get; set; }
    public decimal InputCostPer1K { get; set; }
    public decimal OutputCostPer1K { get; set; }
    public decimal? EmbeddingCostPer1K { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Model capability information
/// </summary>
public class LlmModelCapability
{
    public required string Provider { get; set; }
    public required string Model { get; set; }
    public bool SupportsVision { get; set; }
    public bool SupportsFunctionCalling { get; set; }
    public bool SupportsStructuredOutput { get; set; }
    public bool SupportsStreaming { get; set; }
    public bool SupportsEmbeddings { get; set; }
    public int? MaxContextTokens { get; set; }
    public int? MaxOutputTokens { get; set; }
    public int? EmbeddingDimensions { get; set; }
}

/// <summary>
/// Service for managing LLM model costs.
/// Applications implement this to provide cost data from their preferred source.
/// </summary>
public interface ILlmCostService
{
    /// <summary>
    /// Get costs for all models
    /// </summary>
    Task<Dictionary<string, (decimal Input, decimal Output)>> GetCostsAsync();

    /// <summary>
    /// Refresh costs from the source (e.g., API or database)
    /// </summary>
    Task<Dictionary<string, (decimal Input, decimal Output)>> RefreshAllCostsAsync();

    /// <summary>
    /// Get cost for a specific model
    /// </summary>
    (decimal Input, decimal Output) GetModelCost(string modelKey);

    /// <summary>
    /// Calculate cost for a request
    /// </summary>
    decimal CalculateCost(string modelKey, int inputTokens, int outputTokens);

    /// <summary>
    /// Cache-aware cost calculation. <paramref name="freshInputTokens"/> are
    /// input tokens that didn't hit the provider's prompt cache;
    /// <paramref name="cacheReadTokens"/> are discounted cache hits
    /// (Anthropic charges 10% of the list input rate);
    /// <paramref name="cacheCreationTokens"/> are cache writes
    /// (Anthropic charges 125% of the list input rate for the 5-minute
    /// ephemeral cache). OpenAI's automatic caching surfaces only
    /// cache reads; its creation bucket is zero.
    /// </summary>
    decimal CalculateCost(
        string modelKey,
        int freshInputTokens,
        int cacheReadTokens,
        int cacheCreationTokens,
        int outputTokens);

    /// <summary>
    /// Calculate cost for embedding generation
    /// </summary>
    decimal CalculateEmbeddingCost(string modelKey, int tokens);

    /// <summary>
    /// Reload costs from external settings/configuration
    /// </summary>
    Task ReloadCostsAsync();
}

/// <summary>
/// Service for tracking model capabilities.
/// Applications implement this to provide capability data from their preferred source.
/// </summary>
public interface ILlmCapabilitiesService
{
    /// <summary>
    /// Check if a model supports vision
    /// </summary>
    bool SupportsVision(string provider, string model);

    /// <summary>
    /// Get all vision-capable models for a provider
    /// </summary>
    IEnumerable<string> GetVisionCapableModels(string provider);

    /// <summary>
    /// Get all providers that support vision
    /// </summary>
    IEnumerable<string> GetVisionCapableProviders();

    /// <summary>
    /// Refresh capabilities from provider APIs
    /// </summary>
    Task RefreshCapabilitiesAsync();

    /// <summary>
    /// Get capability info for a specific model
    /// </summary>
    LlmModelCapability? GetModelCapability(string provider, string model);
}

