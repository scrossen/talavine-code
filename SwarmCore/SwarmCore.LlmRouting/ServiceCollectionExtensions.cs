using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Benchmarking;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.Pricing;
using SwarmCore.LlmRouting.Providers;
using SwarmCore.LlmRouting.Services;
using SwarmCore.LlmRouting.ToolUse;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace SwarmCore.LlmRouting;

/// <summary>
/// Extension methods for registering LLM routing services with dependency injection
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// The shared LLM-routing composition root used by the Talavine app, Vibes, and
    /// SwarmCRM. Registers, in order:
    ///
    ///  1. the host's storage seams (settings / API keys / usage / cost / capabilities),
    ///     ahead of everything else so TryAdd defaults inside the core stack defer to them;
    ///  2. the core router stack (<see cref="AddSwarmCoreRouting(IServiceCollection)"/>);
    ///  3. the built-in dynamic routing advisors (opt-in);
    ///  4. the selected cloud and local providers, each with a default options registration
    ///     (real endpoints/keys are hydrated at runtime through the host's seams);
    ///  5. the native tool-use translators (opt-in; after the providers so the
    ///     subscription-CLI translators can detect their backing providers);
    ///  6. a hosted service that eagerly registers every provider with the registry at
    ///     startup (opt-in).
    ///
    /// Host-specific overrides follow normal DI rules around this call: a registration that
    /// must beat a TryAdd default in the core stack (e.g. a custom
    /// <see cref="IModelPricingFetcher"/>) goes BEFORE it; a last-wins replacement of a core
    /// default (e.g. a persistent <see cref="IRoutingPolicyStore"/>) goes AFTER it.
    /// </summary>
    public static IServiceCollection AddSwarmCoreLlmRouting(
        this IServiceCollection services,
        Action<SwarmCoreLlmRoutingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SwarmCoreLlmRoutingOptions();
        configure(options);

        // 1. Host seams. The core stack has no defaults for these; the router requires them.
        foreach (var seam in options.SeamDescriptors)
        {
            services.Add(seam);
        }

        // 2. Core router, provider registry, governor, and TryAdd'd routing-input defaults.
        services.AddSwarmCoreRouting();

        // 3. Built-in dynamic routing advisors.
        if (options.IncludeDynamicRoutingAdvisors)
        {
            services.AddDynamicRoutingAdvisors();
        }

        // 4. Providers. Each cloud provider gets an empty options registration so
        //    IOptions<TProviderOptions> resolves; the providers read endpoints and keys
        //    through the host's ILlmSettingsProvider / ILlmApiKeyProvider seams at runtime.
        if (options.CloudProviders.HasFlag(LlmCloudProviders.OpenAI))
        {
            services.Configure<OpenAIProviderOptions>(_ => { });
            services.AddSingleton<ILlmProvider, OpenAILlmProvider>();
        }

        if (options.CloudProviders.HasFlag(LlmCloudProviders.Claude))
        {
            services.Configure<ClaudeProviderOptions>(_ => { });
            services.AddSingleton<ILlmProvider, ClaudeLlmProvider>();
        }

        if (options.CloudProviders.HasFlag(LlmCloudProviders.Gemini))
        {
            services.Configure<GeminiProviderOptions>(_ => { });
            services.AddSingleton<ILlmProvider, GeminiLlmProvider>();
        }

        if (options.CloudProviders.HasFlag(LlmCloudProviders.DeepSeek))
        {
            services.Configure<DeepSeekProviderOptions>(_ => { });
            services.AddSingleton<ILlmProvider, DeepSeekLlmProvider>();
        }

        if (options.CloudProviders.HasFlag(LlmCloudProviders.OpenRouter))
        {
            services.Configure<OpenRouterProviderOptions>(_ => { });
            services.AddSingleton<ILlmProvider, OpenRouterLlmProvider>();
        }

        if (options.CloudProviders.HasFlag(LlmCloudProviders.Grok))
        {
            services.Configure<GrokProviderOptions>(_ => { });
            services.AddSingleton<ILlmProvider, GrokLlmProvider>();
        }

        if (options.CloudProviders.HasFlag(LlmCloudProviders.HuggingFace))
        {
            services.Configure<HuggingFaceProviderOptions>(_ => { });
            services.AddSingleton<ILlmProvider, HuggingFaceLlmProvider>();
        }

        if (options.CloudProviders.HasFlag(LlmCloudProviders.Venice))
        {
            services.Configure<VeniceProviderOptions>(_ => { });
            services.AddSingleton<ILlmProvider, VeniceLlmProvider>();
        }

        switch (options.Ollama)
        {
            case OllamaRegistration.Provider:
                // Concrete registration allows direct access for Ollama-specific
                // operations; the interface registration feeds the provider registry.
                services.Configure<OllamaProviderOptions>(_ => { });
                services.AddSingleton<OllamaLlmProvider>();
                services.AddSingleton<ILlmProvider>(sp => sp.GetRequiredService<OllamaLlmProvider>());
                // A server change behind the loopback endpoint makes the provider forget that server's context windows.
                services.AddSingleton<ILocalRuntimeServerChangeObserver>(sp => sp.GetRequiredService<OllamaLlmProvider>());
                services.AddSingleton<ILocalLlmCompatibilityProbe, LocalLlmCompatibilityProbe>();
                break;
            case OllamaRegistration.ProviderWithAutoConfiguration:
                services.AddOllamaProvider();
                break;
        }

        if (options.IncludeLMStudio)
        {
            services.AddLMStudioProvider();
        }

        if (options.IncludeSubscriptionCliProviders)
        {
            services.AddSubscriptionCliProviders();
        }

        // 5. Native tool-use translators + registry attachment on the router.
        if (options.IncludeToolUseProviders)
        {
            services.AddToolUseProviders();
        }

        // 6. Eager provider registration at startup.
        if (options.RegisterProvidersAtStartup)
        {
            services.AddHostedService<LlmProviderRegistrationService>();
        }

        return services;
    }

    /// <summary>
    /// Adds the core LLM routing services to the service collection.
    /// You still need to register implementations for:
    /// - ILlmSettingsProvider (application-specific settings storage)
    /// - ILlmUsageTracker (application-specific usage storage)
    /// - ILlmCostService (cost calculation)
    /// - ILlmCapabilitiesService (model capabilities)
    /// - Individual ILlmProvider implementations
    /// </summary>
    public static IServiceCollection AddSwarmCoreRouting(this IServiceCollection services)
    {
        // Register the router as scoped (recommended for most applications)
        services.AddScoped<ILlmRouter, LlmRouter>();

        // Register the provider registry as singleton (providers don't change)
        services.AddSingleton<ILlmProviderRegistry, LlmProviderRegistry>();
        services.TryAddSingleton(_ => LlmProviderDiscoveryCoordinator.Shared);
        services.TryAddSingleton<LlmModelValidationCoordinator>();
        services.AddSingleton<LlmRoutingBenchmarkService>();
        services.AddSingleton<ILlmRoutingOptimizationService, LlmRoutingOptimizationService>();
        services.TryAddScoped<ILlmRoutingGovernor, LlmRoutingGovernor>();

        // Dynamic-routing inputs. Apps can override any of these with their own
        // platform-specific or DB-backed implementations.
        services.TryAddSingleton<ISystemActivitySignal, DefaultSystemActivitySignal>();
        services.TryAddSingleton<IRoutingPolicyStore, InMemoryRoutingPolicyStore>();
        services.TryAddScoped<IBudgetPressureSource, UsageBackedBudgetPressureSource>();
        services.TryAddSingleton<ISubscriptionQuotaTracker, InMemorySubscriptionQuotaTracker>();
        // Process-wide: every router scope and Model Scout read the same counts.
        services.TryAddSingleton<ILlmRouteLoadTracker, LlmRouteLoadTracker>();
        services.TryAddSingleton<ILlmRouteContextWindowObservations>(_ => new LlmRouteContextWindowObservations());
        services.TryAddScoped<ILlmRoutingAuditStore, MissingLlmRoutingAuditStore>();
        // Carries "this turn is being served by a weaker route than the safety
        // policy wanted" from route selection to whoever renders the answer.
        // Singleton: routing and the agent engine live in different scopes.
        services.TryAddSingleton<ILlmDegradedRouteNoticeSink, InMemoryLlmDegradedRouteNoticeSink>();

        // Tool-use validation catalog + service. Validator defaults to a no-op so the
        // service can short-circuit when an app doesn't supply real canonical schemas.
        services.TryAddSingleton<IToolUseCalibrationCatalog, InMemoryToolUseCalibrationCatalog>();
        services.TryAddSingleton<IToolUseValidator>(NullToolUseValidator.Instance);
        services.TryAddScoped<IToolUseCalibrationService, DefaultToolUseCalibrationService>();

        // Configurator agent — reads providers + budget + calibration + hardware and
        // produces a proposed function-preference matrix the user reviews via UI.
        services.TryAddScoped<IRoutingProfileBuilder, DefaultRoutingProfileBuilder>();

        // Single-process local runtime manager. Singleton so all routers share
        // one local model admission queue and one hot-model policy.
        services.TryAddSingleton<ILocalLlmRuntimeManager, LocalLlmRuntimeManager>();
        services.AddOptions<LlmDebugLoggerOptions>();

        // Register the debug logger (only active in DEBUG builds)
#if DEBUG
        services.AddSingleton<ILlmDebugLogger, FileLlmDebugLogger>();
#else
        services.AddSingleton<ILlmDebugLogger>(NullLlmDebugLogger.Instance);
#endif

        // Register pricing fetcher (default implementation, can be overridden by consumer apps)
        services.TryAddSingleton<IModelPricingFetcher, ProviderPricingFetcher>();

        return services;
    }

    /// <summary>
    /// Adds the built-in dynamic routing advisors that read <see cref="RoutingPolicy"/>,
    /// <see cref="SystemActivitySnapshot"/>, and <see cref="BudgetPressureSnapshot"/>:
    ///
    /// - <see cref="UrgencyRoutingAdvisor"/> promotes routing when a caller marks a request
    ///   <see cref="LlmUrgency.Urgent"/>.
    /// - <see cref="BudgetPressureRoutingAdvisor"/> demotes to local/economy when monthly
    ///   spend approaches the user's cap.
    /// - <see cref="TimeOfDayRoutingAdvisor"/> shifts background work to local during the
    ///   user's quiet hours.
    ///
    /// Safe to call multiple times — uses TryAddEnumerable. Call after
    /// <see cref="AddSwarmCoreRouting(IServiceCollection)"/>.
    /// </summary>
    public static IServiceCollection AddDynamicRoutingAdvisors(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ILlmRouteAdvisor, UrgencyRoutingAdvisor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ILlmRouteAdvisor, BudgetPressureRoutingAdvisor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ILlmRouteAdvisor, TimeOfDayRoutingAdvisor>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ILlmRouteAdvisor, SubscriptionQuotaRoutingAdvisor>());
        return services;
    }

    /// <summary>
    /// Registers the reusable Model Scout recommendation engine over a
    /// host-supplied candidate source: load evidence-backed candidates, apply
    /// persisted policy weights, rank deterministically, and append a durable
    /// recommendation receipt. Hosts with richer evidence pipelines register
    /// their own <see cref="IModelScout"/> before calling this — the engine is
    /// added with TryAdd so an existing registration wins.
    /// Call after <see cref="AddSwarmCoreRouting(IServiceCollection)"/>; a
    /// durable <c>ILlmRoutingAuditStore</c> is required for audited
    /// recommendations.
    /// </summary>
    public static IServiceCollection AddSwarmCoreModelScout<TCandidateSource>(
        this IServiceCollection services)
        where TCandidateSource : class, IModelScoutCandidateSource
    {
        services.TryAddScoped<IModelScoutCandidateSource, TCandidateSource>();
        services.TryAddScoped<IModelScout>(sp => new CandidateSourceModelScout(
            sp.GetRequiredService<IModelScoutCandidateSource>(),
            sp.GetRequiredService<ILlmRoutingAuditStore>(),
            sp.GetService<IRoutingPolicyStore>(),
            sp.GetService<ILogger<CandidateSourceModelScout>>(),
            sp.GetService<ISubscriptionQuotaTracker>()));
        return services;
    }

    /// <summary>
    /// Registers the reusable pricing seed/refresh pipeline over the host's
    /// <see cref="IModelPricingStore"/>. The host schedules the passes (e.g.
    /// from its maintenance cycle) and may register an
    /// <see cref="IModelPricingDiscoverySource"/> to auto-price newly listed
    /// provider models.
    /// </summary>
    public static IServiceCollection AddSwarmCoreModelPricingRefresh<TPricingStore>(
        this IServiceCollection services)
        where TPricingStore : class, IModelPricingStore
    {
        services.TryAddScoped<IModelPricingStore, TPricingStore>();
        services.TryAddScoped(sp => new ModelPricingRefreshService(
            sp.GetRequiredService<IModelPricingFetcher>(),
            sp.GetRequiredService<IModelPricingStore>(),
            sp.GetRequiredService<ILogger<ModelPricingRefreshService>>(),
            sp.GetService<IModelPricingDiscoverySource>()));
        return services;
    }

    /// <summary>
    /// Registers the reusable Model Scout maintenance clock over the host's
    /// <see cref="IModelScoutMaintenancePass"/>. The scheduler owns pacing and
    /// failure isolation; the pass owns cycle content and durable cadence.
    /// </summary>
    public static IServiceCollection AddSwarmCoreModelScoutMaintenance<TPass>(
        this IServiceCollection services,
        ModelScoutMaintenanceSchedulerOptions? options = null)
        where TPass : class, IModelScoutMaintenancePass
    {
        services.TryAddScoped<IModelScoutMaintenancePass, TPass>();
        services.AddHostedService(sp => new ModelScoutMaintenanceScheduler(
            sp,
            sp.GetRequiredService<ILogger<ModelScoutMaintenanceScheduler>>(),
            options));
        return services;
    }

    /// <summary>
    /// Registers an LLM provider with the provider registry.
    /// Call this after AddSwarmCoreRouting.
    /// </summary>
    public static IServiceCollection AddLlmProvider<TProvider>(this IServiceCollection services)
        where TProvider : class, ILlmProvider
    {
        services.AddSingleton<ILlmProvider, TProvider>();
        return services;
    }

    /// <summary>
    /// Registers an LLM provider instance with the provider registry.
    /// Call this after AddSwarmCoreRouting.
    /// </summary>
    public static IServiceCollection AddLlmProvider(this IServiceCollection services, ILlmProvider provider)
    {
        services.AddSingleton(provider);
        return services;
    }

    /// <summary>
    /// Adds Ollama provider with auto-configuration.
    /// Ollama will be automatically detected and configured on startup if available.
    /// No API key required - just needs Ollama running locally.
    /// </summary>
    public static IServiceCollection AddOllamaProvider(this IServiceCollection services, Action<OllamaProviderOptions>? configure = null)
    {
        // Configure options
        if (configure != null)
        {
            services.Configure(configure);
        }
        else
        {
            services.Configure<OllamaProviderOptions>(_ => { });
        }

        // Register the Ollama provider
        services.AddSingleton<OllamaLlmProvider>();
        services.AddSingleton<ILlmProvider>(sp => sp.GetRequiredService<OllamaLlmProvider>());
        services.AddSingleton<IOllamaModelWarmup>(sp => sp.GetRequiredService<OllamaLlmProvider>());
        // A server change behind the loopback endpoint makes the provider forget that server's context windows.
        services.AddSingleton<ILocalRuntimeServerChangeObserver>(sp => sp.GetRequiredService<OllamaLlmProvider>());
        services.AddSingleton<ILocalLlmCompatibilityProbe, LocalLlmCompatibilityProbe>();

        // Register the auto-configuration service that checks for Ollama on startup
        services.AddHostedService<OllamaAutoConfigurationService>();

        return services;
    }

    /// <summary>
    /// Adds LM Studio as a local OpenAI-compatible provider. The user still
    /// needs LM Studio running with its local server enabled.
    /// </summary>
    public static IServiceCollection AddLMStudioProvider(this IServiceCollection services, Action<LMStudioProviderOptions>? configure = null)
    {
        if (configure != null)
        {
            services.Configure(configure);
        }
        else
        {
            services.Configure<LMStudioProviderOptions>(_ => { });
        }

        services.AddSingleton<ILlmProvider, LMStudioLlmProvider>();
        return services;
    }

    /// <summary>
    /// Adds subscription-backed local CLI providers. These launch installed,
    /// logged-in tools such as Claude Code and Codex CLI so the user can route
    /// selected functions through their subscription allowance instead of an API key.
    /// </summary>
    public static IServiceCollection AddSubscriptionCliProviders(
        this IServiceCollection services,
        Action<SubscriptionCliProviderOptions>? configure = null)
    {
        if (configure != null)
        {
            services.Configure(configure);
        }
        else
        {
            services.Configure<SubscriptionCliProviderOptions>(_ => { });
        }

        services.TryAddSingleton<ISubscriptionCliProcessRunner, DefaultSubscriptionCliProcessRunner>();
        services.TryAddSingleton<ISubscriptionCliSetupService, SubscriptionCliSetupService>();
        services.AddSingleton<ClaudeCodeLlmProvider>();
        services.AddSingleton<CodexCliLlmProvider>();
        services.AddSingleton<GeminiCliLlmProvider>();
        services.AddSingleton<ILlmProvider>(sp => sp.GetRequiredService<ClaudeCodeLlmProvider>());
        services.AddSingleton<ILlmProvider>(sp => sp.GetRequiredService<CodexCliLlmProvider>());
        services.AddSingleton<ILlmProvider>(sp => sp.GetRequiredService<GeminiCliLlmProvider>());
        return services;
    }

    /// <summary>
    /// Adds the provider subset used by the developer harnesses: OpenAI, Claude,
    /// Gemini and OpenRouter (cloud, API keys configured separately), Ollama
    /// (auto-configured when running locally), LM Studio, and the subscription
    /// CLIs. It does not register DeepSeek, Grok, HuggingFace or Venice; hosts
    /// choose cloud providers explicitly through
    /// <see cref="AddSwarmCoreLlmRouting(IServiceCollection, Action{SwarmCoreLlmRoutingOptions})"/>.
    /// </summary>
    public static IServiceCollection AddAllLlmProviders(this IServiceCollection services)
    {
        // Cloud providers (require API keys)
        services.Configure<OpenAIProviderOptions>(_ => { });
        services.Configure<ClaudeProviderOptions>(_ => { });
        services.Configure<GeminiProviderOptions>(_ => { });
        services.Configure<OpenRouterProviderOptions>(_ => { });
        services.Configure<LMStudioProviderOptions>(_ => { });

        services.AddSingleton<ILlmProvider, OpenAILlmProvider>();
        services.AddSingleton<ILlmProvider, ClaudeLlmProvider>();
        services.AddSingleton<ILlmProvider, GeminiLlmProvider>();
        services.AddSingleton<ILlmProvider, OpenRouterLlmProvider>();

        // Ollama (local, no API key needed, auto-configures on startup)
        services.AddOllamaProvider();
        services.AddLMStudioProvider();
        services.AddSubscriptionCliProviders();

        return services;
    }
}
