using SwarmCore.Abstractions.LlmRouting;

using SwarmCore.LlmRouting.ProviderPacks;

namespace SwarmCore.LlmRouting.Configuration;

/// <summary>
/// Configuration options for LLM providers.
/// This can be bound from appsettings.json or configured in code.
/// </summary>
public class LlmProvidersOptions
{
    public const string SectionName = "LlmProviders";

    /// <summary>
    /// Default provider to use when none specified
    /// </summary>
    public string DefaultProvider { get; set; } = "Ollama";

    /// <summary>
    /// Default model to use when none specified for the default provider
    /// </summary>
    public string DefaultModel { get; set; } = "llama3.2";

    /// <summary>
    /// OpenAI provider configuration
    /// </summary>
    public OpenAIProviderOptions OpenAI { get; set; } = new();

    /// <summary>
    /// Claude/Anthropic provider configuration
    /// </summary>
    public ClaudeProviderOptions Claude { get; set; } = new();

    /// <summary>
    /// Google Gemini provider configuration
    /// </summary>
    public GeminiProviderOptions Gemini { get; set; } = new();

    /// <summary>
    /// xAI Grok provider configuration
    /// </summary>
    public GrokProviderOptions Grok { get; set; } = new();

    /// <summary>
    /// DeepSeek provider configuration
    /// </summary>
    public DeepSeekProviderOptions DeepSeek { get; set; } = new();

    /// <summary>
    /// OpenRouter provider configuration
    /// </summary>
    public OpenRouterProviderOptions OpenRouter { get; set; } = new();

    /// <summary>
    /// Hugging Face Inference Router provider configuration
    /// </summary>
    public HuggingFaceProviderOptions HuggingFace { get; set; } = new();

    /// <summary>Venice AI OpenAI-compatible provider configuration.</summary>
    public VeniceProviderOptions Venice { get; set; } = new();

    /// <summary>
    /// LM Studio local OpenAI-compatible server configuration.
    /// </summary>
    public LMStudioProviderOptions LMStudio { get; set; } = new();
}

/// <summary>
/// Base configuration for an LLM provider
/// </summary>
public abstract class LlmProviderOptionsBase
{
    /// <summary>
    /// Whether this provider is enabled
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// API key for authentication. Can also be set via environment variable.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Environment variable name that contains the API key (alternative to ApiKey)
    /// </summary>
    public string? ApiKeyEnvironmentVariable { get; set; }

    /// <summary>
    /// Base endpoint URL (optional, uses default if not set)
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Default model for this provider
    /// </summary>
    public string? DefaultModel { get; set; }

    /// <summary>
    /// Default embedding model for this provider
    /// </summary>
    public string? DefaultEmbeddingModel { get; set; }

    /// <summary>
    /// Maximum retries for failed requests
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Timeout in seconds for API requests
    /// </summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Priority for provider selection (lower = higher priority)
    /// </summary>
    public int Priority { get; set; } = 100;

    /// <summary>
    /// Get the effective API key, checking environment variable if direct key not set
    /// </summary>
    public string? GetEffectiveApiKey()
    {
        if (!string.IsNullOrEmpty(ApiKey))
            return ApiKey;

        if (!string.IsNullOrEmpty(ApiKeyEnvironmentVariable))
            return Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);

        return null;
    }
}

/// <summary>
/// OpenAI-specific configuration
/// </summary>
public class OpenAIProviderOptions : LlmProviderOptionsBase
{
    public OpenAIProviderOptions()
    {
        var pack = ProviderPackCatalog.GetRequired("OpenAI").DirectApi!;
        ApiKeyEnvironmentVariable = "OPENAI_API_KEY";
        DefaultModel = pack.DefaultModel;
        DefaultEmbeddingModel = pack.DefaultEmbeddingModel;
        Endpoint = pack.BaseUrl;
        Priority = 10;
    }

    /// <summary>
    /// Organization ID for OpenAI API (optional)
    /// </summary>
    public string? OrganizationId { get; set; }
}

/// <summary>
/// Anthropic Claude-specific configuration
/// </summary>
public class ClaudeProviderOptions : LlmProviderOptionsBase
{
    public ClaudeProviderOptions()
    {
        var pack = ProviderPackCatalog.GetRequired("Claude").DirectApi!;
        ApiKeyEnvironmentVariable = "ANTHROPIC_API_KEY";
        DefaultModel = pack.DefaultModel;
        Endpoint = pack.BaseUrl;
        Priority = 20;
    }
}

/// <summary>
/// Google Gemini-specific configuration
/// </summary>
public class GeminiProviderOptions : LlmProviderOptionsBase
{
    public GeminiProviderOptions()
    {
        var pack = ProviderPackCatalog.GetRequired("Gemini").DirectApi!;
        ApiKeyEnvironmentVariable = "GOOGLE_AI_API_KEY";
        DefaultModel = pack.DefaultModel;
        DefaultEmbeddingModel = pack.DefaultEmbeddingModel;
        Endpoint = pack.BaseUrl;
        Priority = 30;
    }
}

/// <summary>
/// xAI Grok-specific configuration
/// </summary>
public class GrokProviderOptions : LlmProviderOptionsBase
{
    public GrokProviderOptions()
    {
        var pack = ProviderPackCatalog.GetRequired("Grok").DirectApi!;
        ApiKeyEnvironmentVariable = "XAI_API_KEY";
        DefaultModel = pack.DefaultModel;
        Endpoint = pack.BaseUrl;
        Priority = 40;
    }
}

/// <summary>
/// DeepSeek-specific configuration
/// </summary>
public class DeepSeekProviderOptions : LlmProviderOptionsBase
{
    public DeepSeekProviderOptions()
    {
        var pack = ProviderPackCatalog.GetRequired("DeepSeek").DirectApi!;
        ApiKeyEnvironmentVariable = "DEEPSEEK_API_KEY";
        DefaultModel = pack.DefaultModel;
        Endpoint = pack.BaseUrl;
        Priority = 50;
    }
}

/// <summary>
/// OpenRouter configuration. OpenRouter exposes an OpenAI-compatible API at
/// <c>https://openrouter.ai/api/v1</c> and routes requests based on provider/model ids.
/// </summary>
public class OpenRouterProviderOptions : LlmProviderOptionsBase
{
    public OpenRouterProviderOptions()
    {
        var providerPack = ProviderPackCatalog.GetRequired("OpenRouter").OpenRouter!;
        ApiKeyEnvironmentVariable = "OPENROUTER_API_KEY";
        DefaultModel = providerPack.DefaultModel;
        Endpoint = providerPack.BaseUrl;
        Priority = 45;
    }

    /// <summary>
    /// Optional OpenRouter attribution header. Defaults to Talavine's public site.
    /// </summary>
    public string? SiteUrl { get; set; } = "https://swarmmarshal.com";

    /// <summary>
    /// Optional OpenRouter title header shown in OpenRouter analytics.
    /// </summary>
    public string? ApplicationTitle { get; set; } = "Talavine";
}

/// <summary>
/// Hugging Face Inference Router configuration. The router exposes an
/// OpenAI-compatible chat completions endpoint at <c>https://router.huggingface.co/v1</c>
/// and fans requests out to hosted Inference Providers (together.ai, fireworks,
/// hyperbolic, etc.). Users supply a Hugging Face token (<c>hf_…</c>) and a
/// <c>namespace/model</c> id.
/// </summary>
public class HuggingFaceProviderOptions : LlmProviderOptionsBase
{
    public HuggingFaceProviderOptions()
    {
        var pack = ProviderPackCatalog.GetRequired("HuggingFace").DirectApi!;
        ApiKeyEnvironmentVariable = "HF_TOKEN";
        DefaultModel = pack.DefaultModel;
        Endpoint = pack.BaseUrl;
        Priority = 60;
    }
}

/// <summary>Venice AI chat completions configuration.</summary>
public class VeniceProviderOptions : LlmProviderOptionsBase
{
    public VeniceProviderOptions()
    {
        ApiKeyEnvironmentVariable = "VENICE_API_KEY";
        Endpoint = "https://api.venice.ai/api/v1";
        DefaultModel = "zai-org-glm-5-2";
        Priority = 65;
    }

    /// <summary>
    /// The Venice model a host offers as the route to try first when another model declines a request: Venice's
    /// catalog marks it with the <c>most_uncensored</c> trait (<c>/models</c>, <c>model_spec.traits</c>), so it answers
    /// requests other models refuse on policy grounds. Empty turns that lane off. Configuration:
    /// <c>LlmProviders:Venice:DeclineFallbackModel</c>.
    /// </summary>
    public string? DeclineFallbackModel { get; set; } = "venice-uncensored-1-2";
}

/// <summary>
/// LM Studio local server configuration. LM Studio exposes an OpenAI-compatible
/// API when its local server is enabled, usually at http://127.0.0.1:1234/v1.
/// </summary>
public class LMStudioProviderOptions : LlmProviderOptionsBase
{
    public LMStudioProviderOptions()
    {
        Enabled = true;
        Endpoint = "http://127.0.0.1:1234/v1";
        DefaultModel = ModelDefaults.ForProvider("LMStudio");
        Priority = 5;
    }
}
