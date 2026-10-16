namespace SwarmCore.LlmRouting.Configuration;

/// <summary>One atomic credential selection. Never log or persist its secret value.</summary>
public sealed record LlmCredential([property: System.Text.Json.Serialization.JsonIgnore] string? Key, string? Reference)
{
    public override string ToString() => "LlmCredential(redacted)";
}

/// <summary>
/// Interface for providing API keys to LLM providers.
/// Implementations can retrieve keys from environment variables, configuration, database, etc.
/// </summary>
public interface ILlmApiKeyProvider
{
    /// <summary>
    /// Get the API key for a specific provider
    /// </summary>
    /// <param name="providerName">Name of the provider (e.g., "OpenAI", "Claude")</param>
    /// <returns>The API key, or null if not found</returns>
    Task<string?> GetApiKeyAsync(string providerName);

    /// <summary>Nonsecret account/credential revision used to bind retained protocol state.
    /// Null means the host cannot verify durable opaque replay; ordinary execution remains available.</summary>
    Task<string?> GetCredentialReferenceAsync(string providerName) => Task.FromResult<string?>(null);
    async Task<LlmCredential> GetCredentialAsync(string providerName) => new(await GetApiKeyAsync(providerName), null);

    /// <summary>
    /// Set the API key for a specific provider
    /// </summary>
    /// <param name="providerName">Name of the provider</param>
    /// <param name="apiKey">The API key to store</param>
    Task SetApiKeyAsync(string providerName, string apiKey);

    /// <summary>
    /// Check if an API key exists for a provider
    /// </summary>
    /// <param name="providerName">Name of the provider</param>
    /// <returns>True if a key exists</returns>
    Task<bool> HasApiKeyAsync(string providerName);
}

/// <summary>
/// Default implementation that reads from LlmProviderOptions (IOptions pattern).
/// Falls back to environment variables if not found in configuration.
/// </summary>
public class OptionsBasedApiKeyProvider : ILlmApiKeyProvider
{
    private readonly LlmProvidersOptions _options;

    public OptionsBasedApiKeyProvider(LlmProvidersOptions options)
    {
        _options = options;
    }

    public Task<string?> GetApiKeyAsync(string providerName)
    {
        var key = providerName.ToUpperInvariant() switch
        {
            "OPENAI" => _options.OpenAI.GetEffectiveApiKey(),
            "CLAUDE" or "ANTHROPIC" => _options.Claude.GetEffectiveApiKey(),
            "GEMINI" or "GOOGLE" => _options.Gemini.GetEffectiveApiKey(),
            "GROK" or "XAI" => _options.Grok.GetEffectiveApiKey(),
            "DEEPSEEK" => _options.DeepSeek.GetEffectiveApiKey(),
            "OPENROUTER" => _options.OpenRouter.GetEffectiveApiKey(),
            "HUGGINGFACE" or "HF" => _options.HuggingFace.GetEffectiveApiKey(),
            "VENICE" => _options.Venice.GetEffectiveApiKey(),
            _ => null
        };

        return Task.FromResult(key);
    }

    public Task SetApiKeyAsync(string providerName, string apiKey)
    {
        // Options-based provider is read-only
        // If you need to set keys, use a database-backed implementation
        throw new NotSupportedException(
            "OptionsBasedApiKeyProvider is read-only. Use a database-backed implementation for key management.");
    }

    public Task<bool> HasApiKeyAsync(string providerName)
    {
        return GetApiKeyAsync(providerName).ContinueWith(t => !string.IsNullOrEmpty(t.Result));
    }
}

/// <summary>
/// Composite API key provider that tries multiple providers in order.
/// Useful for layered configuration (try database, then config, then env vars).
/// </summary>
public class CompositeApiKeyProvider : ILlmApiKeyProvider
{
    private readonly IEnumerable<ILlmApiKeyProvider> _providers;

    public CompositeApiKeyProvider(IEnumerable<ILlmApiKeyProvider> providers)
    {
        _providers = providers;
    }

    public async Task<string?> GetApiKeyAsync(string providerName)
    {
        foreach (var provider in _providers)
        {
            var key = await provider.GetApiKeyAsync(providerName);
            if (!string.IsNullOrEmpty(key))
                return key;
        }
        return null;
    }

    public async Task<LlmCredential> GetCredentialAsync(string providerName)
    {
        foreach (var provider in _providers)
        {
            var credential = await provider.GetCredentialAsync(providerName);
            if (!string.IsNullOrEmpty(credential.Key)) return credential;
        }
        return new(null, null);
    }

    public async Task<string?> GetCredentialReferenceAsync(string providerName)
        => (await GetCredentialAsync(providerName)).Reference;

    public async Task SetApiKeyAsync(string providerName, string apiKey)
    {
        // Try to set on the first provider that supports it
        foreach (var provider in _providers)
        {
            try
            {
                await provider.SetApiKeyAsync(providerName, apiKey);
                return;
            }
            catch (NotSupportedException)
            {
                // This provider doesn't support setting keys, try next
                continue;
            }
        }

        throw new NotSupportedException("No API key provider supports setting keys.");
    }

    public async Task<bool> HasApiKeyAsync(string providerName)
    {
        foreach (var provider in _providers)
        {
            if (await provider.HasApiKeyAsync(providerName))
                return true;
        }
        return false;
    }
}

