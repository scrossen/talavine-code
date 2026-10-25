using System.Collections.Concurrent;
using SwarmCore.Abstractions.LlmRouting;
using Microsoft.Extensions.Logging;

namespace SwarmCore.LlmRouting;

/// <summary>
/// Default implementation of ILlmProviderRegistry.
/// Manages registration and lookup of LLM providers.
/// Auto-registers all ILlmProvider implementations from DI on construction.
/// </summary>
public class LlmProviderRegistry : ILlmProviderRegistry
{
    private readonly ConcurrentDictionary<string, ILlmProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<LlmProviderRegistry>? _logger;

    /// <summary>
    /// Creates a new provider registry with auto-registration from DI.
    /// All ILlmProvider implementations are registered automatically.
    /// </summary>
    public LlmProviderRegistry(
        IEnumerable<ILlmProvider> providers,
        ILogger<LlmProviderRegistry>? logger = null)
    {
        _logger = logger;

        foreach (var provider in providers)
        {
            _providers[provider.ProviderName] = provider;
            _logger?.LogInformation("Auto-registered LLM provider: {ProviderName} (Embeddings: {SupportsEmbeddings})",
                provider.ProviderName, provider.SupportsEmbeddings);
        }

        _logger?.LogInformation("LlmProviderRegistry initialized with {Count} provider(s)", _providers.Count);
    }

    public void RegisterProvider(ILlmProvider provider)
    {
        _providers[provider.ProviderName] = provider;
        _logger?.LogInformation("Manually registered LLM provider: {ProviderName}", provider.ProviderName);
    }

    public bool UnregisterProvider(string providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName)) return false;

        var removed = _providers.TryRemove(providerName, out _);
        if (removed)
            _logger?.LogInformation("Unregistered LLM provider: {ProviderName}", providerName);

        return removed;
    }

    public ILlmProvider? GetProvider(string providerName)
    {
        return _providers.TryGetValue(providerName, out var provider) ? provider : null;
    }

    public IEnumerable<ILlmProvider> GetAllProviders()
    {
        return _providers.Values;
    }

    public bool HasProvider(string providerName)
    {
        return _providers.ContainsKey(providerName);
    }

    public IEnumerable<ILlmProvider> GetEmbeddingCapableProviders()
    {
        return _providers.Values.Where(p => p.SupportsEmbeddings);
    }
}

