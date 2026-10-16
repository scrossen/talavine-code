using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SwarmCore.LlmRouting;

/// <summary>
/// Cloud providers the shared composition root
/// (<see cref="ServiceCollectionExtensions.AddSwarmCoreLlmRouting"/>) can register.
/// </summary>
[Flags]
public enum LlmCloudProviders
{
    None = 0,
    OpenAI = 1 << 0,
    Claude = 1 << 1,
    Gemini = 1 << 2,
    DeepSeek = 1 << 3,
    OpenRouter = 1 << 4,
    Grok = 1 << 5,
    HuggingFace = 1 << 6,
    Venice = 1 << 7,
}

/// <summary>
/// How the local Ollama provider should be registered, if at all.
/// </summary>
public enum OllamaRegistration
{
    /// <summary>Do not register Ollama.</summary>
    None,

    /// <summary>
    /// Register the provider (as both <see cref="Providers.OllamaLlmProvider"/> and
    /// <see cref="ILlmProvider"/>) plus the local-model compatibility probe. The host
    /// owns detection/configuration through its own settings surface.
    /// </summary>
    Provider,

    /// <summary>
    /// <see cref="Provider"/> plus the <see cref="IOllamaModelWarmup"/> seam and the
    /// hosted auto-configuration service that detects a local Ollama on startup.
    /// </summary>
    ProviderWithAutoConfiguration,
}

/// <summary>
/// Options for <see cref="ServiceCollectionExtensions.AddSwarmCoreLlmRouting"/>, the shared
/// LLM-routing composition root. Hosts pick their provider set with the flags below and plug
/// their storage seams in via the Use* methods.
///
/// The five seams (settings, API keys, usage tracking, cost, capabilities) have no defaults in
/// the core stack — the router requires all of them. The Use* methods exist so a host's seam
/// choices are visible in one place; registering the same interfaces on the IServiceCollection
/// before calling AddSwarmCoreLlmRouting is equally valid.
/// </summary>
public sealed class SwarmCoreLlmRoutingOptions
{
    internal List<ServiceDescriptor> SeamDescriptors { get; } = new();

    /// <summary>Cloud providers to register. Defaults to none; each host opts in.</summary>
    public LlmCloudProviders CloudProviders { get; set; } = LlmCloudProviders.None;

    /// <summary>Local Ollama registration mode. Defaults to none.</summary>
    public OllamaRegistration Ollama { get; set; } = OllamaRegistration.None;

    /// <summary>Register LM Studio as a local OpenAI-compatible provider.</summary>
    public bool IncludeLMStudio { get; set; }

    /// <summary>
    /// Register the subscription-backed local CLI providers (Claude Code, Codex CLI,
    /// Gemini CLI) so selected functions can spend subscription allowance instead of
    /// API-key credit.
    /// </summary>
    public bool IncludeSubscriptionCliProviders { get; set; }

    /// <summary>
    /// Register the built-in dynamic routing advisors (urgency, budget pressure,
    /// time-of-day, subscription quota).
    /// </summary>
    public bool IncludeDynamicRoutingAdvisors { get; set; }

    /// <summary>
    /// Register the native tool-use translators and attach the tool-use registry to the
    /// router. Required for the agentic tool-use loop; applied after the providers so the
    /// subscription-CLI translators can detect their backing providers.
    /// </summary>
    public bool IncludeToolUseProviders { get; set; }

    /// <summary>
    /// Add a hosted service that eagerly registers every <see cref="ILlmProvider"/> with the
    /// provider registry at startup (and logs each one). Without it the registry is built
    /// lazily on first use; both are functionally equivalent because the registry
    /// auto-registers all providers on construction.
    /// </summary>
    public bool RegisterProvidersAtStartup { get; set; }

    /// <summary>Supplies the host's <see cref="ILlmSettingsProvider"/> (model/function preferences).</summary>
    public SwarmCoreLlmRoutingOptions UseSettingsProvider<T>(ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where T : class, ILlmSettingsProvider
        => AddSeam(ServiceDescriptor.Describe(typeof(ILlmSettingsProvider), typeof(T), lifetime));

    /// <summary>Supplies the host's <see cref="ILlmSettingsProvider"/> from a factory.</summary>
    public SwarmCoreLlmRoutingOptions UseSettingsProvider(
        Func<IServiceProvider, ILlmSettingsProvider> factory,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
        => AddSeam(ServiceDescriptor.Describe(typeof(ILlmSettingsProvider), factory, lifetime));

    /// <summary>
    /// Supplies the host's <see cref="ILlmApiKeyProvider"/>. Always singleton: it is injected
    /// into the singleton <see cref="ILlmProvider"/> implementations, so a shorter lifetime
    /// would be a captive dependency.
    /// </summary>
    public SwarmCoreLlmRoutingOptions UseApiKeyProvider<T>()
        where T : class, ILlmApiKeyProvider
        => AddSeam(ServiceDescriptor.Singleton(typeof(ILlmApiKeyProvider), typeof(T)));

    /// <summary>Supplies the host's <see cref="ILlmApiKeyProvider"/> from a factory (singleton).</summary>
    public SwarmCoreLlmRoutingOptions UseApiKeyProvider(Func<IServiceProvider, ILlmApiKeyProvider> factory)
        => AddSeam(ServiceDescriptor.Singleton(typeof(ILlmApiKeyProvider), factory));

    /// <summary>Supplies the host's <see cref="ILlmUsageTracker"/> (per-call usage rows).</summary>
    public SwarmCoreLlmRoutingOptions UseUsageTracker<T>(ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where T : class, ILlmUsageTracker
        => AddSeam(ServiceDescriptor.Describe(typeof(ILlmUsageTracker), typeof(T), lifetime));

    /// <summary>Supplies the host's <see cref="ILlmUsageTracker"/> from a factory.</summary>
    public SwarmCoreLlmRoutingOptions UseUsageTracker(
        Func<IServiceProvider, ILlmUsageTracker> factory,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
        => AddSeam(ServiceDescriptor.Describe(typeof(ILlmUsageTracker), factory, lifetime));

    /// <summary>Supplies the host's <see cref="ILlmCostService"/> (per-model price table).</summary>
    public SwarmCoreLlmRoutingOptions UseCostService<T>(ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where T : class, ILlmCostService
        => AddSeam(ServiceDescriptor.Describe(typeof(ILlmCostService), typeof(T), lifetime));

    /// <summary>Supplies the host's <see cref="ILlmCostService"/> from a factory.</summary>
    public SwarmCoreLlmRoutingOptions UseCostService(
        Func<IServiceProvider, ILlmCostService> factory,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
        => AddSeam(ServiceDescriptor.Describe(typeof(ILlmCostService), factory, lifetime));

    /// <summary>Supplies the host's <see cref="ILlmCapabilitiesService"/> (vision/tool-use capability lookups).</summary>
    public SwarmCoreLlmRoutingOptions UseCapabilitiesService<T>(ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where T : class, ILlmCapabilitiesService
        => AddSeam(ServiceDescriptor.Describe(typeof(ILlmCapabilitiesService), typeof(T), lifetime));

    /// <summary>Supplies the host's <see cref="ILlmCapabilitiesService"/> from a factory.</summary>
    public SwarmCoreLlmRoutingOptions UseCapabilitiesService(
        Func<IServiceProvider, ILlmCapabilitiesService> factory,
        ServiceLifetime lifetime = ServiceLifetime.Singleton)
        => AddSeam(ServiceDescriptor.Describe(typeof(ILlmCapabilitiesService), factory, lifetime));

    private SwarmCoreLlmRoutingOptions AddSeam(ServiceDescriptor descriptor)
    {
        SeamDescriptors.Add(descriptor);
        return this;
    }
}
