using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Culture;
using SwarmCore.Agent.CodingTasks;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;
using SwarmCore.Agent.ToolUseEngine;
using SwarmCore.Agent.InstructionChat;
using SwarmCore.Agent.Interactions;
using SwarmCore.Abstractions.CodingTasks;
using SwarmCore.Abstractions.LlmRouting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace SwarmCore.Agent;

/// <summary>
/// Extension methods for registering SwarmCore.Agent services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the default SwarmCore.Agent stack: capability catalog,
    /// tool-use engine, turn-event mapper, context-injector registry,
    /// permission broker, and the turn runner. The host must still provide:
    /// <list type="bullet">
    ///   <item><see cref="IProfileResolver"/> — resolves which profile drives a turn.</item>
    ///   <item><see cref="IConversationStore"/> — UI-facing turn history.</item>
    ///   <item><see cref="IAgentSessionStore"/> — agent transcript persistence.</item>
    ///   <item>An <c>ILlmRouter</c> (from SwarmCore.LlmRouting) and at least one provider.</item>
    /// </list>
    /// Optional: <see cref="IInjectorListProvider"/> to enable context
    /// injectors per profile, and <see cref="IAgentTelemetrySink"/> for
    /// per-turn telemetry. <see cref="IPermissionBroker"/> defaults to the
    /// SwarmCore.Skills consent-service wrapper; if the host has not
    /// registered <c>ISkillPermissionConsentService</c>, supply your own
    /// broker via <c>TryAddSingleton</c>.
    /// </summary>
    public static IServiceCollection AddSwarmCoreAgent(this IServiceCollection services)
    {
        services.TryAddSingleton<ConversationChangeFeed>();
        services.TryAddSingleton<AgentConversationExecutionGate>();
        services.TryAddSingleton<ICapabilityCatalog, InMemoryCapabilityCatalog>();
        services.TryAddSingleton<IAgentSessionStore, InMemoryAgentSessionStore>();
        services.AddDurableAgentSessions();
        services.TryAddSingleton<IAgentTurnSteeringCoordinator, AgentTurnSteeringCoordinator>();
        services.TryAddSingleton<IAgentTurnSteeringService>(sp =>
            sp.GetRequiredService<IAgentTurnSteeringCoordinator>());
        services.TryAddSingleton<IInteractionRequestStore, InMemoryInteractionRequestStore>();
        services.TryAddSingleton<IInteractionRequestService>(sp =>
            new InteractionRequestService(sp.GetRequiredService<IInteractionRequestStore>()));
        // The engine captures ILlmRouter, which is scoped because it owns scoped
        // settings and Model Scout state. Do not promote that graph to the root.
        services.TryAddScoped<IWorkingContextCompactor, SemanticWorkingContextCompactor>();
        services.TryAddSingleton<IContextInjectorRegistry, ContextInjectorRegistry>();
        services.TryAddSingleton<IAgentToolRouter, NoopAgentToolRouter>();
        services.TryAddSingleton<IPermissionBroker, PermissionBroker>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<SwarmCore.Skills.Abstractions.IPermissionConsentPolicy, MissionPermissionConsentPolicy>());
        services.TryAddSingleton<ICurrentViewContext, CurrentViewContext>();
        services.TryAddSingleton<ICurrentTurnContextAccessor, AsyncLocalCurrentTurnContextAccessor>();
        services.TryAddScoped<IAgentTurnRunner, AgentTurnRunner>();
        services.TryAddScoped<IAgentInstructionAmendmentService, AgentInstructionAmendmentService>();
        return services;
    }

    /// <summary>
    /// Register the shared durable boundary for hosts that build custom
    /// <see cref="AgentSession"/> instances instead of using the higher-level
    /// conversation/profile turn runner. Production hosts should replace the
    /// in-memory ledger with their persistent adapter before building the
    /// provider; the adapter itself remains identical across hosts.
    /// </summary>
    public static IServiceCollection AddDurableAgentSessions(this IServiceCollection services)
    {
        services.TryAddSingleton<IAgentThreadEventStore, InMemoryAgentThreadEventStore>();
        services.TryAddScoped<IToolUseAgentEngine, ToolUseAgentEngine>();
        services.TryAddSingleton<ITurnEventMapper, TurnEventMapper>();
        services.TryAddScoped<IDurableAgentSessionRunner, DurableAgentSessionRunner>();
        return services;
    }

    /// <summary>
    /// Adds the provider-neutral continual-objective domain services. This is
    /// opt-in because every host must supply its own
    /// <see cref="IContinualObjectiveStore"/> and one or more named
    /// <see cref="IContinualObjectiveExecutor"/> adapters. No background
    /// supervisor is registered by this method.
    /// </summary>
    public static IServiceCollection AddSwarmCoreContinualAgent(
        this IServiceCollection services,
        Action<ContinualObjectiveOptions>? configure = null)
        => AddSwarmCoreContinualAgent(services, registerDefaultProcessor: true, configure);

    /// <summary>
    /// Hosts with explicitly bound processors can share domain services without
    /// registering a default processor against an unrelated or absent store.
    /// </summary>
    public static IServiceCollection AddSwarmCoreContinualAgent(
        this IServiceCollection services,
        bool registerDefaultProcessor,
        Action<ContinualObjectiveOptions>? configure = null)
    {
        var options = new ContinualObjectiveOptions();
        configure?.Invoke(options);
        options.Validate();

        services.TryAddSingleton(options);
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<ContinualObjectiveStateMachine>();
        services.TryAddScoped<IContinualObjectiveAcceptanceService, ContinualObjectiveAcceptanceService>();
        services.TryAddScoped<IContinualObjectiveExecutorRegistry, ContinualObjectiveExecutorRegistry>();
        if (registerDefaultProcessor)
            services.TryAddScoped<IContinualObjectiveProcessor, ContinualObjectiveProcessor>();
        services.TryAddSingleton<IContinualObjectiveActivitySink, NullContinualObjectiveActivitySink>();
        services.TryAddSingleton<IContinualObjectiveTextSanitizer, CredentialScrubbingContinualObjectiveTextSanitizer>();
        services.TryAddSingleton<IContinualObjectiveContinuationPromptBuilder, ContinualObjectiveContinuationPromptBuilder>();
        services.TryAddSingleton<IContinualObjectiveExecutionControl, ContinualObjectiveExecutionControl>();
        return services;
    }

    /// <summary>
    /// Connect the reusable Model Scout status, recommendation, explanation, and
    /// pin controls to this host's agent capability catalog. Call after (or before)
    /// <see cref="AddSwarmCoreAgent"/>; the registrations are order-independent.
    /// The host must register <c>IModelScout</c>, <c>ILlmSettingsProvider</c>, and
    /// <c>ILlmProviderRegistry</c>, <c>IRoutingPolicyStore</c>, and a durable
    /// <c>ILlmRoutingAuditStore</c>.
    /// Startup fails if the audit store is absent so control-plane writes cannot
    /// silently become unaudited.
    /// </summary>
    public static IServiceCollection AddModelScoutChatTools(this IServiceCollection services)
    {
        services.TryAddSingleton<IModelScoutHostControl, UnavailableModelScoutHostControl>();
        services.AddHostedService<ModelScoutChatToolRegistrationService>();
        return services;
    }

    /// <summary>
    /// Register the coding-task engine (<see cref="ICodingTaskService"/>):
    /// automated fix attempts that run an agentic coding CLI (Claude Code /
    /// Codex) inside an isolated git worktree + branch, producing a local
    /// diff for HUMAN review — never merging, never pushing, never touching
    /// the user's working tree. Hosts bind <see cref="CodingTaskOptions"/>
    /// (typically from the <c>CodingTasks</c> configuration section) via
    /// <paramref name="configure"/> or their own options pipeline.
    /// </summary>
    public static IServiceCollection AddSwarmCoreCodingTasks(
        this IServiceCollection services,
        Action<CodingTaskOptions>? configure = null)
    {
        services.AddOptions<CodingTaskOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<ICodingTaskProcessRunner, DefaultCodingTaskProcessRunner>();
        services.TryAddSingleton<ICodingTaskCliLocator, SubscriptionCliCodingTaskCliLocator>();
        services.TryAddSingleton<ICodingTaskService, CodingTaskService>();
        return services;
    }

    /// <summary>
    /// Register the shared working culture: the <see cref="WorkingCultureContextInjector"/>
    /// that appends <see cref="AgentCultureCharter"/> text to every turn, with a
    /// no-op store the host replaces (<see cref="FileAgentCultureStore"/> for a
    /// profile directory). Hosts also add the injector's name
    /// (<see cref="AgentCultureCharter.InjectorName"/>) to their
    /// <see cref="IInjectorListProvider"/> list.
    /// </summary>
    public static IServiceCollection AddAgentWorkingCulture(this IServiceCollection services)
    {
        services.TryAddSingleton<IAgentCultureStore, NullAgentCultureStore>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IContextInjector, WorkingCultureContextInjector>());
        return services;
    }
}
