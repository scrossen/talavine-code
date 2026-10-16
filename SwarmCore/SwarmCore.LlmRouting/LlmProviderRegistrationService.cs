using SwarmCore.Abstractions.LlmRouting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SwarmCore.LlmRouting;

/// <summary>
/// Hosted service that eagerly registers all ILlmProvider instances with the
/// ILlmProviderRegistry at startup. Opt-in via
/// <see cref="SwarmCoreLlmRoutingOptions.RegisterProvidersAtStartup"/> — the registry also
/// auto-registers providers on construction, so this mainly forces construction early and
/// logs each provider. Providers themselves initialize lazily on first use, so the app can
/// start even if a provider's API is temporarily unavailable.
/// </summary>
internal class LlmProviderRegistrationService : IHostedService
{
    private readonly ILlmProviderRegistry _registry;
    private readonly IEnumerable<ILlmProvider> _providers;
    private readonly ILogger<LlmProviderRegistrationService> _logger;

    public LlmProviderRegistrationService(
        ILlmProviderRegistry registry,
        IEnumerable<ILlmProvider> providers,
        ILogger<LlmProviderRegistrationService> logger)
    {
        _registry = registry;
        _providers = providers;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var provider in _providers)
        {
            try
            {
                _registry.RegisterProvider(provider);
                _logger.LogInformation("Registered LLM provider: {ProviderName}", provider.ProviderName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to register LLM provider: {ProviderName}", provider.ProviderName);
            }
        }

        _logger.LogInformation("LLM provider registration complete. {Count} providers registered.",
            _providers.Count());

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
