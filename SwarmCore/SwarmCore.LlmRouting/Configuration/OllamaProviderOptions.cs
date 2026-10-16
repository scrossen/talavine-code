namespace SwarmCore.LlmRouting.Configuration;

/// <summary>
/// Options for configuring the Ollama LLM provider
/// </summary>
public class OllamaProviderOptions
{
    /// <summary>
    /// The base URL for the Ollama API (default: http://localhost:11434)
    /// </summary>
    public string Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>
    /// Optional list of Ollama nodes for load balancing/failover.
    /// If omitted, the single Endpoint value above is used.
    /// </summary>
    public List<OllamaNodeOptions> Nodes { get; set; } = new();

    /// <summary>
    /// Default model for chat completions (will auto-detect if not specified)
    /// </summary>
    public string? DefaultModel { get; set; }

    /// <summary>
    /// Pinned model for embeddings. Runtime routing ignores external overrides
    /// because embedding vectors are only comparable within one model space.
    /// </summary>
    public string DefaultEmbeddingModel { get; set; } = SwarmCore.Abstractions.LlmRouting.EmbeddingModelDefaults.Model;

    /// <summary>
    /// Timeout for API requests in seconds.
    /// Default is 600 (10 minutes) to accommodate slow GPUs during inference.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 600;

    /// <summary>
    /// Maximum time to wait for a local node slot before failing this attempt.
    /// Keep this high for non-time-critical local workloads.
    /// </summary>
    public int QueueWaitTimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// How long a node stays in cooldown after transient failures before retrying it.
    /// </summary>
    public int NodeFailureCooldownSeconds { get; set; } = 20;

    /// <summary>
    /// When streaming responses, maximum seconds with no data received before declaring
    /// the node hung. Generous default accommodates reasoning models (e.g. Qwen) that
    /// pause between thinking phases. Set to 0 to disable streaming (fall back to
    /// non-streaming requests with the original TimeoutSeconds behaviour).
    /// </summary>
    public int StreamInactivityTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Whether to auto-pull missing embedding models on startup.
    /// When true, the first preferred embedding model will be downloaded automatically.
    /// Default: true for seamless first-time user experience.
    /// </summary>
    public bool AutoPullEmbeddingModel { get; set; } = true;

    /// <summary>
    /// Whether to auto-pull missing chat/vision models when Ollama returns model-not-found.
    /// Default: true for self-healing local deployments.
    /// </summary>
    public bool AutoPullMissingModels { get; set; } = true;

    /// <summary>
    /// Preferred chat models in order of preference (first available will be used)
    /// </summary>
    public List<string> PreferredModels { get; set; } = new()
    {
        "qwen3-coder:30b",
        "gpt-oss:20b",
        "phi4-mini",
        "qwen2.5:14b",
        "phi4",
        "llama3.3",
        "qwen2.5",
        "llama3.1",
        "llama3.2",
        "mistral",
        "phi3"
    };

    /// <summary>
    /// The single allowed embedding model. Kept as a list for compatibility with
    /// existing configuration binding, but runtime code only accepts this entry.
    /// </summary>
    public List<string> PreferredEmbeddingModels { get; set; } = new()
    {
        SwarmCore.Abstractions.LlmRouting.EmbeddingModelDefaults.Model
    };
}

/// <summary>
/// Configuration for one Ollama node in a multi-node pool.
/// </summary>
public class OllamaNodeOptions
{
    /// <summary>
    /// Friendly name for logs/diagnostics.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Node API endpoint (for example: http://192.168.1.20:11434).
    /// </summary>
    public string Endpoint { get; set; } = "";

    /// <summary>
    /// Per-node local concurrency cap.
    /// </summary>
    public int MaxConcurrentRequests { get; set; } = 1;
}
