namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// LLM response with token usage information
/// </summary>
public class LlmResponse
{
    public required string Content { get; set; }
    public required string Provider { get; set; }
    public required string Model { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalTokens { get; set; }

    /// <summary>
    /// True only when the provider response supplied complete billable input and
    /// output token counts. False means one or both values were estimated and a
    /// hard spend reservation must remain conservatively charged.
    /// </summary>
    public bool UsageMeasurementComplete { get; set; }

    /// <summary>
    /// Input tokens served from the provider's prompt cache (applies to
    /// Anthropic's ephemeral cache and OpenAI's automatic prefix cache).
    /// Zero when the provider doesn't surface cache metrics or when the
    /// call missed the cache entirely. Included in <see cref="InputTokens"/>
    /// so downstream total-token math stays consistent; the cost
    /// calculator uses this field to apply the ~10% cache-hit discount.
    /// </summary>
    public int CacheReadInputTokens { get; set; }

    /// <summary>
    /// Input tokens written into the provider's prompt cache on this call
    /// (Anthropic charges a 25% premium on these; subsequent calls within
    /// the TTL get <see cref="CacheReadInputTokens"/> instead). Zero on
    /// providers without explicit cache-creation billing.
    /// </summary>
    public int CacheCreationInputTokens { get; set; }

    public decimal EstimatedCost { get; set; }

    /// <summary>
    /// Billed cost reported by the provider itself for this exact call
    /// (OpenRouter returns it when usage accounting is requested). Null when
    /// the provider does not report real cost; consumers fall back to
    /// <see cref="EstimatedCost"/>.
    /// </summary>
    public decimal? ProviderReportedCostUsd { get; set; }

    public int ResponseTimeMs { get; set; }
    public bool WasSuccessful { get; set; } = true;
    public LlmFailureCategory? FailureCategory { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// HTTP status code of the provider reply that produced this response, when
    /// the provider had one. Failure classification prefers this structured
    /// status over sniffing digit runs out of <see cref="ErrorMessage"/> (a
    /// context-length error mentioning "130429 tokens" is not a 429). Null when
    /// the failure was not an HTTP status (network error, local runtime, CLI).
    /// </summary>
    public int? HttpStatusCode { get; set; }

    /// <summary>
    /// Server-reported prompt-prefill time in milliseconds (Ollama
    /// prompt_eval_duration and equivalents). Null when the provider does
    /// not surface serving-layer timings. A prefill duration near the full
    /// prompt size divided by the model's prefill rate — on a prompt whose
    /// prefix should be cached — is the primary KV-cache-miss signal.
    /// </summary>
    public int? PrefillDurationMs { get; set; }

    /// <summary>
    /// Server-reported token-generation time in milliseconds (Ollama
    /// eval_duration). With <see cref="OutputTokens"/> this yields the
    /// decode rate, whose drift indicates GPU contention.
    /// </summary>
    public int? DecodeDurationMs { get; set; }

    /// <summary>
    /// Server-reported model (re)load time in milliseconds (Ollama
    /// load_duration). Frequent nonzero values mean keep_alive is too
    /// short or the model is being evicted by memory pressure.
    /// </summary>
    public int? ModelLoadDurationMs { get; set; }

    /// <summary>
    /// Length in characters of the stable prompt prefix the caller declared
    /// via the PromptCaching boundary marker; 0 when no marker was present.
    /// Lets serving-health analysis distinguish "full prefill was expected"
    /// from "full prefill despite a cacheable prefix".
    /// </summary>
    public int DeclaredCachePrefixChars { get; set; }

    /// <summary>
    /// Provider-side hidden reasoning/thinking text when the provider exposes it
    /// separately from visible assistant content.
    /// </summary>
    public string? ReasoningContent { get; set; }

    /// <summary>
    /// The physical request that produced this response explicitly asked the
    /// provider to suppress hidden reasoning. This is evidence of an attempted
    /// recovery setting, not a claim that the provider honored it. A router
    /// should not repeat that same repair for another thinking-only response.
    /// </summary>
    public bool HiddenReasoningSuppressionRequested { get; set; }

    /// <summary>
    /// Machine-readable response-shape notes for router recovery and diagnostics.
    /// </summary>
    public List<string> Diagnostics { get; set; } = [];
}

/// <summary>
/// Embedding response from a provider
/// </summary>
public class LlmEmbeddingResponse
{
    public bool Success { get; set; }
    public float[]? Vector { get; set; }
    public int Dimensions => Vector?.Length ?? 0;
    public required string Provider { get; set; }
    public required string Model { get; set; }
    public int TokenCount { get; set; }
    public decimal EstimatedCost { get; set; }
    public int ResponseTimeMs { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Interface for individual LLM providers (OpenAI, Claude, Gemini, etc.)
/// </summary>
public interface ILlmProvider
{
    /// <summary>
    /// The name of this provider (e.g., "OpenAI", "Claude", "Gemini")
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Generate a response from the LLM
    /// </summary>
    Task<LlmResponse> GenerateResponseAsync(string prompt, string model, Dictionary<string, object>? executionSettings = null);

    /// <summary>
    /// Generate a vision response from the LLM
    /// </summary>
    Task<LlmResponse> GenerateVisionResponseAsync(string prompt, string imageBase64, string model, 
        Dictionary<string, object>? executionSettings = null);

    /// <summary>
    /// Check if this provider supports a specific model
    /// </summary>
    bool SupportsModel(string model);

    /// <summary>
    /// Check if a specific model supports vision
    /// </summary>
    bool SupportsVision(string model);

    /// <summary>
    /// Get all available models for this provider
    /// </summary>
    Task<List<string>> GetAvailableModelsAsync();

    /// <summary>
    /// Test connectivity to this provider
    /// </summary>
    Task<(bool IsHealthy, string Message)> TestConnectivityAsync();

    /// <summary>
    /// Generate a streaming response from the LLM
    /// </summary>
    /// <param name="prompt">The prompt to send</param>
    /// <param name="model">The model to use</param>
    /// <param name="onChunk">Callback invoked for each chunk of the response</param>
    /// <param name="executionSettings">Optional execution settings</param>
    /// <returns>The complete response when finished</returns>
    Task<LlmResponse> GenerateStreamingResponseAsync(string prompt, string model, Func<string, Task> onChunk,
        Dictionary<string, object>? executionSettings = null);

    /// <summary>
    /// Check if this provider supports streaming
    /// </summary>
    bool SupportsStreaming { get; }

    /// <summary>
    /// Check if this provider supports embeddings
    /// </summary>
    bool SupportsEmbeddings { get; }

    /// <summary>
    /// Get the default embedding model for this provider
    /// </summary>
    string? DefaultEmbeddingModel { get; }

    /// <summary>
    /// Generate embeddings for text
    /// </summary>
    /// <param name="text">The text to generate embeddings for</param>
    /// <param name="model">The embedding model to use</param>
    /// <returns>The embedding response with vector</returns>
    Task<LlmEmbeddingResponse> GenerateEmbeddingAsync(
        string text,
        string model,
        Dictionary<string, object>? executionSettings = null);

    /// <summary>
    /// Generate embeddings for multiple texts in a batch
    /// </summary>
    /// <param name="texts">The texts to generate embeddings for</param>
    /// <param name="model">The embedding model to use</param>
    /// <returns>The embedding responses with vectors</returns>
    Task<List<LlmEmbeddingResponse>> GenerateEmbeddingsAsync(
        IList<string> texts,
        string model,
        Dictionary<string, object>? executionSettings = null);
}

/// <summary>
/// Optional contract for an LLM provider that serves native tool use itself.
/// Its translator sends each tool turn through this provider instance: the
/// endpoint the instance was registered with and the runtime state it keeps
/// per model. A dynamically registered provider such as a user-owned LAN Ollama
/// node therefore gets tool use bound to exactly that node, and tool turns
/// share the state the provider's text requests use.
/// </summary>
public interface IToolUseProviderSource
{
    /// <summary>The tool-use translator bound to this provider instance, or null when it has none.</summary>
    IToolUseProvider? ToolUseProvider { get; }
}

/// <summary>
/// Optional readiness contract for providers backed by a separately installed
/// local runtime. Routers use this before acquiring a visible local-work slot so
/// an absent runtime is reported as unavailable instead of looking like active
/// inference in the UI.
/// </summary>
public interface ILlmProviderRuntimeReadiness
{
    Task<(bool IsReady, string Message)> CheckRuntimeReadinessAsync(
        string model,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Registry for managing multiple LLM providers
/// </summary>
public interface ILlmProviderRegistry
{
    /// <summary>
    /// Register a provider
    /// </summary>
    void RegisterProvider(ILlmProvider provider);

    /// <summary>
    /// Remove a provider registered at runtime (for example an Ollama@node LAN
    /// provider whose node was deleted). Returns false when no provider had that name.
    /// </summary>
    bool UnregisterProvider(string providerName);

    /// <summary>
    /// Get a provider by name
    /// </summary>
    ILlmProvider? GetProvider(string providerName);

    /// <summary>
    /// Get all registered providers
    /// </summary>
    IEnumerable<ILlmProvider> GetAllProviders();

    /// <summary>
    /// Check if a provider is registered
    /// </summary>
    bool HasProvider(string providerName);

    /// <summary>
    /// Get providers that support vision
    /// </summary>
    IEnumerable<ILlmProvider> GetVisionCapableProviders();

    /// <summary>
    /// Get providers that support embeddings
    /// </summary>
    IEnumerable<ILlmProvider> GetEmbeddingCapableProviders();
}

/// <summary>
/// A provider that needs no API key because it authenticates through its own channel: an administrator-approved
/// compute node, an operator-run model server, a paired peer. The router treats it as available while it currently
/// serves models (or, for an explicit pin, while it is registered), never by looking for a key it will not have.
/// </summary>
public interface ICredentialFreeLlmProvider
{
}
