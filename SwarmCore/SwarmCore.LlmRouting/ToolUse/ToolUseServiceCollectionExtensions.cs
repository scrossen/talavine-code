using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Configuration;

namespace SwarmCore.LlmRouting.ToolUse;

/// <summary>
/// DI registration for the native tool-use translators. Each provider gets
/// its own <see cref="IToolUseProvider"/> registration keyed by provider
/// name; the router resolves the matching instance per request.
/// </summary>
public static class ToolUseServiceCollectionExtensions
{
    /// <summary>
    /// Register all tool-use translators: OpenAI, Anthropic/Claude, LM Studio,
    /// Grok (xAI), DeepSeek, OpenRouter, Hugging Face, Venice, and Gemini, plus the
    /// registry that also resolves provider-owned translators such as Ollama's.
    /// Call once from application startup after the standard LLM routing has
    /// been registered.
    /// </summary>
    public static IServiceCollection AddToolUseProviders(this IServiceCollection services)
    {
        // OpenAI — reuses ILlmApiKeyProvider for the bearer token.
        services.AddSingleton<IToolUseProvider>(sp =>
        {
            var endpoint = sp.GetRequiredService<IOptions<OpenAIProviderOptions>>().Value.Endpoint;
            var baseUrl = string.IsNullOrWhiteSpace(endpoint)
                ? ProviderPacks.ProviderPackCatalog.GetRequired("OpenAI").DirectApi!.BaseUrl
                : endpoint.Trim().TrimEnd('/');
            return OpenAIStyleToolUseProvider.ForOpenAICompatible("OpenAI", $"{baseUrl}/chat/completions",
                sp.GetRequiredService<ILlmApiKeyProvider>(), sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<OpenAIStyleToolUseProvider>());
        });

        // Anthropic (Claude).
        services.AddSingleton<IToolUseProvider, AnthropicToolUseProvider>();

        // Ollama has no registration here. This machine's daemon and every
        // Ollama@node serve tool use natively through their own registered
        // provider instance (IToolUseProviderSource), which the registry
        // resolves at call time.

        // LM Studio — local OpenAI-compatible endpoint, no auth.
        services.AddSingleton<IToolUseProvider>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<LMStudioProviderOptions>>().Value;
            var baseUrl = string.IsNullOrWhiteSpace(opts.Endpoint)
                ? "http://127.0.0.1:1234/v1"
                : opts.Endpoint.TrimEnd('/');
            if (!baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                baseUrl = $"{baseUrl}/v1";
            }

            return new OpenAIStyleToolUseProvider(
                providerName: "LMStudio",
                chatCompletionsUrl: $"{baseUrl}/chat/completions",
                authFactory: _ => Task.FromResult<System.Net.Http.Headers.AuthenticationHeaderValue?>(null),
                httpClientFactory: sp.GetRequiredService<IHttpClientFactory>(),
                logger: sp.GetRequiredService<ILoggerFactory>().CreateLogger<OpenAIStyleToolUseProvider>());
        });

        // Grok (xAI) — OpenAI-compatible.
        services.AddSingleton<IToolUseProvider>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<GrokProviderOptions>>().Value;
            var baseUrl = string.IsNullOrWhiteSpace(opts.Endpoint)
                ? "https://api.x.ai/v1"
                : opts.Endpoint.TrimEnd('/');
            return OpenAIStyleToolUseProvider.ForOpenAICompatible(
                providerName: "Grok",
                chatCompletionsUrl: $"{baseUrl}/chat/completions",
                apiKeyProvider: sp.GetRequiredService<ILlmApiKeyProvider>(),
                httpClientFactory: sp.GetRequiredService<IHttpClientFactory>(),
                logger: sp.GetRequiredService<ILoggerFactory>().CreateLogger<OpenAIStyleToolUseProvider>());
        });

        // DeepSeek — OpenAI-compatible.
        services.AddSingleton<IToolUseProvider>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<DeepSeekProviderOptions>>().Value;
            var baseUrl = string.IsNullOrWhiteSpace(opts.Endpoint)
                ? "https://api.deepseek.com/v1"
                : opts.Endpoint.TrimEnd('/');
            return OpenAIStyleToolUseProvider.ForOpenAICompatible(
                providerName: "DeepSeek",
                chatCompletionsUrl: $"{baseUrl}/chat/completions",
                apiKeyProvider: sp.GetRequiredService<ILlmApiKeyProvider>(),
                httpClientFactory: sp.GetRequiredService<IHttpClientFactory>(),
                logger: sp.GetRequiredService<ILoggerFactory>().CreateLogger<OpenAIStyleToolUseProvider>());
        });

        // OpenRouter — OpenAI-compatible.
        services.AddSingleton<IToolUseProvider>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<OpenRouterProviderOptions>>().Value;
            var baseUrl = NormalizeOpenRouterBaseUrl(opts.Endpoint);
            return OpenAIStyleToolUseProvider.ForOpenAICompatible(
                providerName: "OpenRouter",
                chatCompletionsUrl: $"{baseUrl}/chat/completions",
                apiKeyProvider: sp.GetRequiredService<ILlmApiKeyProvider>(),
                httpClientFactory: sp.GetRequiredService<IHttpClientFactory>(),
                logger: sp.GetRequiredService<ILoggerFactory>().CreateLogger<OpenAIStyleToolUseProvider>());
        });

        // Hugging Face Inference Router — OpenAI-compatible.
        services.AddSingleton<IToolUseProvider>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<HuggingFaceProviderOptions>>().Value;
            var baseUrl = string.IsNullOrWhiteSpace(opts.Endpoint)
                ? "https://router.huggingface.co/v1"
                : opts.Endpoint.TrimEnd('/');
            return OpenAIStyleToolUseProvider.ForOpenAICompatible(
                providerName: "HuggingFace",
                chatCompletionsUrl: $"{baseUrl}/chat/completions",
                apiKeyProvider: sp.GetRequiredService<ILlmApiKeyProvider>(),
                httpClientFactory: sp.GetRequiredService<IHttpClientFactory>(),
                logger: sp.GetRequiredService<ILoggerFactory>().CreateLogger<OpenAIStyleToolUseProvider>());
        });

        // Venice AI — OpenAI-compatible.
        services.AddSingleton<IToolUseProvider>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<VeniceProviderOptions>>().Value;
            var baseUrl = string.IsNullOrWhiteSpace(opts.Endpoint)
                ? "https://api.venice.ai/api/v1"
                : opts.Endpoint.TrimEnd('/');
            return OpenAIStyleToolUseProvider.ForOpenAICompatible(
                providerName: "Venice",
                chatCompletionsUrl: $"{baseUrl}/chat/completions",
                apiKeyProvider: sp.GetRequiredService<ILlmApiKeyProvider>(),
                httpClientFactory: sp.GetRequiredService<IHttpClientFactory>(),
                logger: sp.GetRequiredService<ILoggerFactory>().CreateLogger<OpenAIStyleToolUseProvider>(),
                streamRequiredToolCalls: false);
        });

        // Gemini — native generateContent translator with functionCall /
        // functionResponse + systemInstruction, thinking-budget aware.
        services.AddSingleton<IToolUseProvider, GeminiToolUseProvider>();

        // Subscription-backed local CLIs. The SwarmCore engine still executes
        // tools; the CLI model chooses the next tool call through a strict JSON
        // protocol, letting users spend Claude Code/Codex subscription allowance.
        // Only register a CLI translator when its backing provider is present: a host that doesn't
        // register the subscription CLIs (e.g. the multi-tenant CRM) must not get a translator that
        // can't resolve its provider — that would break resolving IEnumerable<IToolUseProvider>.
        if (services.Any(d => d.ServiceType == typeof(SwarmCore.LlmRouting.Providers.ClaudeCodeLlmProvider)))
            services.AddSingleton<IToolUseProvider, ClaudeCodeToolUseProvider>();
        if (services.Any(d => d.ServiceType == typeof(SwarmCore.LlmRouting.Providers.CodexCliLlmProvider)))
            services.AddSingleton<IToolUseProvider, CodexCliToolUseProvider>();

        // Registry that resolves providers by name.
        services.TryAddSingleton<IToolUseProviderRegistry, ToolUseProviderRegistry>();
        services.TryAddSingleton<IToolUseRouteSupport>(sp =>
            sp.GetRequiredService<IToolUseProviderRegistry>());

        // On-demand capability proving: lets routing answer "can this model
        // call tools?" by asking the model, instead of refusing every route
        // nobody has measured.
        services.TryAddSingleton<SwarmCore.LlmRouting.Services.IToolUseProviderRegistryAccessor>(sp =>
            new ToolUseProviderRegistryAccessor(sp.GetRequiredService<IToolUseProviderRegistry>()));
        services.TryAddSingleton<IToolUseCapabilityProver, SwarmCore.LlmRouting.Services.ToolUseCapabilityProver>();

        // Replace the ILlmRouter registration with one that attaches the
        // tool-use registry after construction. Preserves the scoped lifetime.
        var existing = services.FirstOrDefault(s => s.ServiceType == typeof(ILlmRouter));
        var lifetime = existing?.Lifetime ?? ServiceLifetime.Scoped;
        services.Replace(new ServiceDescriptor(typeof(ILlmRouter), sp =>
        {
            var router = ActivatorUtilities.CreateInstance<LlmRouter>(sp);
            router.AttachToolUseRegistry(sp.GetRequiredService<IToolUseProviderRegistry>());
            if (sp.GetService<IToolUseCapabilityProver>() is { } prover)
            {
                router.AttachToolUseProver(prover);
            }
            return router;
        }, lifetime));

        return services;
    }

    private static string NormalizeOpenRouterBaseUrl(string? endpoint)
    {
        var normalized = string.IsNullOrWhiteSpace(endpoint)
            ? "https://openrouter.ai/api/v1"
            : endpoint!.Trim().TrimEnd('/');

        foreach (var suffix in new[] { "/chat/completions", "/models" })
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[..^suffix.Length].TrimEnd('/');
            }
        }

        if (normalized.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        if (normalized.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            return $"{normalized}/v1";
        }

        return $"{normalized}/api/v1";
    }
}

/// <summary>
/// Looks up a <see cref="IToolUseProvider"/> by provider name.
/// </summary>
public interface IToolUseProviderRegistry : IToolUseRouteSupport
{
    IToolUseProvider Resolve(string providerName);
}

public sealed class ToolUseProviderRegistry : IToolUseProviderRegistry
{
    private readonly Dictionary<string, IToolUseProvider> _byName;
    private readonly ILlmProviderRegistry? _llmProviders;

    public ToolUseProviderRegistry(IEnumerable<IToolUseProvider> providers)
    {
        _byName = providers.ToDictionary(p => p.ProviderName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The provider-registry overload is deliberately separate so small hosts
    /// and direct unit tests that only register static translators keep their
    /// existing construction contract. In a full host the provider registry is
    /// consulted at resolve time: a provider that serves tool use itself
    /// (<see cref="IToolUseProviderSource"/>), such as this machine's Ollama or
    /// a LAN node that may appear, disappear, or change endpoint after DI has
    /// built this singleton, supplies the translator bound to its own instance.
    /// </summary>
    public ToolUseProviderRegistry(
        IEnumerable<IToolUseProvider> providers,
        ILlmProviderRegistry llmProviders)
        : this(providers)
    {
        _llmProviders = llmProviders;
    }

    public bool Supports(string providerName)
        => _byName.ContainsKey(providerName) || TryResolveProviderOwned(providerName, out _);

    public IToolUseProvider Resolve(string providerName)
    {
        if (_byName.TryGetValue(providerName, out var provider))
        {
            return provider;
        }

        if (TryResolveProviderOwned(providerName, out provider))
        {
            return provider;
        }

        throw new NotSupportedException(
            $"No tool-use translator registered for provider '{providerName}'. " +
            "Call AddToolUseProviders() during startup.");
    }

    private bool TryResolveProviderOwned(string providerName, out IToolUseProvider provider)
    {
        provider = null!;
        // The name must match exactly: a provider registered under an alias
        // must not lend its node to another provider's routes.
        if (_llmProviders?.GetProvider(providerName) is not { } registered
            || !string.Equals(registered.ProviderName, providerName, StringComparison.OrdinalIgnoreCase)
            || registered is not IToolUseProviderSource { ToolUseProvider: { } owned })
        {
            return false;
        }

        provider = owned;
        return true;
    }
}

/// <summary>
/// Adapts <see cref="IToolUseProviderRegistry"/> to the narrow accessor the
/// capability prover depends on, so the prover does not need the registry type.
/// </summary>
internal sealed class ToolUseProviderRegistryAccessor
    : SwarmCore.LlmRouting.Services.IToolUseProviderRegistryAccessor
{
    private readonly IToolUseProviderRegistry _registry;

    public ToolUseProviderRegistryAccessor(IToolUseProviderRegistry registry) => _registry = registry;

    public SwarmCore.Abstractions.LlmRouting.IToolUseProvider? Resolve(string providerName)
        => _registry.Supports(providerName) ? _registry.Resolve(providerName) : null;
}
