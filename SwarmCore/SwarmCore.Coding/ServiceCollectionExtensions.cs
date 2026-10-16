using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SwarmCore.Agent;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Prompting;
using SwarmCore.Coding.Routing;
using SwarmCore.Coding.Tools;
using SwarmCore.Agent.Git;
using SwarmCore.Missions;

namespace SwarmCore.Coding;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers only the host-neutral coding harness: the coding tools and their catalog
    /// registrar, <see cref="RepoInstructionsInjector"/>, the multi-lane
    /// <see cref="ICodingTurnOrchestrator"/> with lane health and the native lane runners
    /// (Claude Code, Codex, Gemini), <see cref="RepeatedToolCallGuard"/> as a concrete
    /// service, the model catalog, and the command acceptance verifier. It also makes sure
    /// the SwarmCore agent runtime and mission execution primitives are present (both
    /// registrations are idempotent, so a host that already called
    /// <c>AddSwarmCoreAgent</c> and <c>AddSwarmCoreMissionExecution</c> or
    /// <c>AddSwarmCoreMissionTools</c> gets no duplicates).
    /// <para>
    /// It deliberately registers none of the host's policy or state: no
    /// <see cref="IProfileResolver"/>, <see cref="IInjectorListProvider"/>,
    /// <see cref="IToolInvocationGuard"/>, <see cref="IPermissionBroker"/>,
    /// <see cref="IAgentToolProgressBroker"/>, conversation or session store, credential vault,
    /// <see cref="ICodingWorkspaceResolver"/>, or sandbox root. The repository is chosen per
    /// conversation through <see cref="CodingWorkspace"/> and fenced per turn, so one process
    /// can serve many conversations in different repositories. Authority comes from the
    /// host on every request (<see cref="CodingTurnRequest.CommunicationScope"/>).
    /// </para>
    /// <para>
    /// Registration order: seams the harness never registers win with a host TryAdd in either order.
    /// <c>AddSwarmCoreAgent</c> (called here) TryAdds runtime defaults such as
    /// <see cref="IPermissionBroker"/>, <see cref="IAgentSessionStore"/> and
    /// <see cref="ICapabilityCatalog"/>; a host registers its own before calling this method (or
    /// before its own <c>AddSwarmCoreAgent</c>), or uses <c>Replace</c> afterwards. A host TryAdd of
    /// those after the harness silently loses to the default.
    /// </para>
    /// <para>
    /// Harness defaults a host may replace: <see cref="ICodingLaneSource"/>
    /// (<see cref="OptionsCodingLaneSource"/> over the <c>CodingLanes</c> section) and
    /// <see cref="ICodingAcceptanceVerifier"/>. Both are TryAdd: register the host's
    /// implementation before calling this method, or replace the descriptor afterwards.
    /// </para>
    /// </summary>
    public static IServiceCollection AddSwarmCoreCodingHarness(
        this IServiceCollection services,
        Action<CodingOptions>? configureCoding = null,
        Action<CodingLaneOptions>? configureLanes = null)
    {
        services.AddOptions<CodingOptions>();
        services.AddOptions<CodingLaneOptions>();
        if (configureCoding is not null) services.Configure(configureCoding);
        if (configureLanes is not null) services.Configure(configureLanes);
        // The configured git applies to every harness git call (GitProcess is static), so the resolved options set it.
        if (!services.Any(d => d.ImplementationInstance is GitExecutableOptionsHook))
            services.AddSingleton<IPostConfigureOptions<CodingOptions>>(new GitExecutableOptionsHook());

        services.AddSwarmCoreAgent();
        services.AddSwarmCoreMissionExecution();

        services.TryAddSingleton<EditFileTool>();
        services.TryAddSingleton<GrepTool>();
        services.TryAddSingleton<GlobTool>();
        services.TryAddSingleton<GitStatusTool>();
        services.TryAddSingleton<GitDiffTool>();
        // Read-only Changes-panel view over git.status/git.diff (hosts pass the conversation's workspace root).
        services.TryAddSingleton<WorkingTreeChangesReader>();
        // Per-turn working-tree checkpoints (git trees under a private ref namespace) for a host's undo.
        services.TryAddSingleton(sp => new Checkpoints.TurnCheckpointService(sp.GetRequiredService<IOptions<CodingOptions>>()));
        services.TryAddSingleton<GitCommitTool>();
        services.TryAddSingleton<GitLogTool>();
        services.TryAddSingleton<CredentialsListTool>();
        services.TryAddSingleton<HarnessDeclineTool>();
        services.TryAddSingleton<NotebookReadTool>();
        services.TryAddSingleton<NotebookEditTool>();

        // The repeated-call guard is offered as a concrete service. Which IToolInvocationGuard the
        // engine runs is host policy (Talavine already runs FenceTaintToolGuard), so it is not bound here.
        services.TryAddSingleton<RepeatedToolCallGuard>();
        services.TryAddSingleton<LaneHealthTracker>();
        services.TryAddSingleton<ActiveCodingWorkspaces>();
        // A request's permission policy (CodingTurnRequest.Permissions) applies through this guard; a host lists it in its guard chain.
        services.TryAddSingleton<Permissions.ActiveCodingPermissions>();
        services.TryAddSingleton<Permissions.CodingPermissionGuard>();
        // Repository settings allow rules apply only after approval; a host replaces the in-memory default with a durable store.
        services.TryAddSingleton<Permissions.ICodingRepositorySettingsApprovalStore, Permissions.InMemoryCodingRepositorySettingsApprovalStore>();
        services.TryAddSingleton<CodingCredentialIntakeContext>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentTurnToolFilter, NativeCommitToolFilter>());
        // Task list (plan.update / plan.read) state; a host replaces the in-memory default with a durable store.
        services.TryAddSingleton<Planning.IConversationPlanStore, Planning.InMemoryConversationPlanStore>();
        // Plan mode (CodingTurnRequest.PlanMode): engine turns keep only read-only tools and the task list.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentTurnToolFilter, Planning.CodingPlanModeToolFilter>());
        // Agent roles (agents/*.md): an engine turn started as a role keeps only the tools the role allows.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentTurnToolFilter, Agents.CodingAgentToolFilter>());
        services.TryAddSingleton<ICodingLaneSource, OptionsCodingLaneSource>();

        // Native coding-agent lanes: the external CLI owns its tools and loop; SwarmCore persists the receipts.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<INativeLaneRunner, Native.ClaudeCodeNativeLaneRunner>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<INativeLaneRunner, Native.CodexNativeLaneRunner>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<INativeLaneRunner, Native.GeminiCliNativeLaneRunner>());

        // Available to any host's injector list under RepoInstructionsInjector.InjectorName.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IContextInjector, RepoInstructionsInjector>());
        // Repository memory: the bounded MEMORY.md index (listed by name, so inert for hosts that do not ask for it) and a
        // default in-process proposal store a host replaces with a durable one.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IContextInjector, Memory.RepoMemoryIndexInjector>());
        services.TryAddSingleton<Memory.IRepoMemoryProposalStore, Memory.InMemoryRepoMemoryProposalStore>();
        // Skills index (listed by name, so inert unless a host asks for it) and the hook runtime. Repository hooks stay
        // off until the host's approval store approves the exact hooks.json content; AddCodingHooks wires the engine.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IContextInjector, Extensions.CodingSkillsInjector>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IContextInjector, Agents.AgentDefinitionsInjector>());
        services.TryAddSingleton<Extensions.CodingHookRunner>();
        services.TryAddSingleton<Extensions.ICodingHookApprovalStore, Extensions.NoCodingHookApprovals>();
        services.TryAddSingleton<Extensions.ICodingHookSource, Extensions.CodingHookSource>();
        services.TryAddSingleton<Extensions.CodingHooks>();
        services.TryAddSingleton<ICodingTurnOrchestrator, CodingTurnOrchestrator>();
        services.TryAddScoped<CodingModelCatalog>();
        services.TryAddScoped<ICodingAcceptanceVerifier, CodingCommandAcceptanceVerifier>();
        // CompatibilityAttempt coding tasks (SwarmCore.Agent.CodingTasks) run their CLI through the native lanes.
        services.TryAddSingleton<SwarmCore.Agent.CodingTasks.ICodingTaskAgentRunner, Native.NativeLaneCodingTaskAgentRunner>();

        if (!services.Any(d => d.ImplementationType == typeof(CodingToolRegistrar)))
            services.AddHostedService<CodingToolRegistrar>();
        return services;
    }

    /// <summary>
    /// The SwarmCode host composition: <see cref="AddSwarmCoreCodingHarness"/> plus the
    /// single-repository policy a SwarmCode process uses. The repository is
    /// <see cref="CodingOptions.RepositoryRoot"/> (also the configured sandbox root, and what
    /// <see cref="OptionsCodingWorkspaceResolver"/> returns for every conversation),
    /// <see cref="CodingProfileResolver"/> builds the one coding profile,
    /// <see cref="CodingInjectorListProvider"/> gives it the repository-instruction injector,
    /// <see cref="Permissions.CodingPermissionGuard"/> then <see cref="RepeatedToolCallGuard"/> are the engine's invocation guard, and tool progress is
    /// tracked in memory so long builds are not cut off as silent. The host still supplies
    /// logging, HttpClient, LLM routing (with a durable <c>ILlmRoutingAuditStore</c>),
    /// <see cref="IConversationStore"/>, <see cref="IPermissionBroker"/>, optionally a secret vault
    /// (<c>SwarmCore.Secrets.ISecretVault</c> / <c>ICredentialStore</c>) for <c>credentials.list</c>
    /// and <c>{{secret:name}}</c> references, an <see cref="ITranscriptRewriter"/> so a refusal is
    /// not replayed to the next lane, and the communication scope on each request.
    /// </summary>
    public static IServiceCollection AddSwarmCoreCoding(
        this IServiceCollection services,
        Action<CodingOptions>? configureCoding = null,
        Action<CodingLaneOptions>? configureLanes = null)
    {
        services.AddSwarmCoreCodingHarness(configureCoding, configureLanes);

        // One repository per SwarmCode process: it is the configured sandbox root for work outside a
        // coding turn, and every conversation's workspace. Turns still carry it explicitly.
        services.AddOptions<SandboxOptions>().PostConfigure<IOptions<CodingOptions>>(
            (sandbox, coding) => sandbox.Root = Path.GetFullPath(coding.Value.RepositoryRoot));
        services.TryAddSingleton<ICodingWorkspaceResolver, OptionsCodingWorkspaceResolver>();

        // Robustness, not gates: tools report liveness so silent builds are not killed, and a
        // model repeating the same failing call is told to change approach (then handed off).
        services.TryAddSingleton<IAgentToolProgressBroker, InMemoryAgentToolProgressBroker>();
        // The person's permission policy runs first, so a deny rule wins over everything after it.
        services.TryAddSingleton<IToolInvocationGuard>(sp => new CompositeToolInvocationGuard(
            sp.GetRequiredService<Permissions.CodingPermissionGuard>(), sp.GetRequiredService<RepeatedToolCallGuard>()));

        services.TryAddSingleton<IProfileResolver, CodingProfileResolver>();
        services.TryAddSingleton<IInjectorListProvider, CodingInjectorListProvider>();
        return services;
    }
}

public static class AgentTeamServiceCollectionExtensions
{
    /// <summary>
    /// Agents that coding turns start (<c>agents.*</c>): the <see cref="Agents.AgentTeam"/> coordinator (TryAdd, so a
    /// host that shares one team across workspaces pre-registers its instance), a default in-memory
    /// <see cref="Agents.IAgentTeamStore"/>, and the catalog registration for engine lanes. The host supplies the
    /// <see cref="Agents.IAgentTeamHost"/> that runs agent conversations (<see cref="Agents.AgentTeam.AttachHost"/>) and
    /// adds <see cref="Agents.AgentTeamPrompts.Guidance"/> to its instructions.
    /// </summary>
    public static IServiceCollection AddSwarmCoreAgentTeam(this IServiceCollection services, Action<Agents.AgentTeamOptions>? configure = null)
    {
        services.AddOptions<Agents.AgentTeamOptions>();
        if (configure is not null) services.Configure(configure);
        services.TryAddSingleton<Agents.IAgentTeamStore, Agents.InMemoryAgentTeamStore>();
        services.TryAddSingleton<Agents.IAgentDefinitionSource, Agents.CodingAgentDefinitionSource>();
        services.TryAddSingleton(sp => new Agents.AgentTeam(sp.GetRequiredService<Agents.IAgentTeamStore>(),
            sp.GetRequiredService<IOptionsMonitor<Agents.AgentTeamOptions>>(), sp.GetService<Microsoft.Extensions.Logging.ILogger<Agents.AgentTeam>>(),
            definitions: sp.GetService<Agents.IAgentDefinitionSource>()));
        if (!services.Any(d => d.ImplementationType == typeof(Agents.AgentTeamToolRegistration)))
            services.AddHostedService<Agents.AgentTeamToolRegistration>();
        return services;
    }
}

public static class WebToolsServiceCollectionExtensions
{
    /// <summary>
    /// <c>web.search</c> and <c>web.fetch</c> for API lanes: <see cref="Web.WebTools"/>, the Brave Search provider (TryAdd, so a
    /// host may register another <see cref="Web.IWebSearchProvider"/> first), the guarded fetch client (public addresses only,
    /// checked per connection and redirect hop), and the catalog registration. The host supplies the key through
    /// <see cref="Web.IWebSearchKeySource"/> (TryAdd default: none, so search reports it is not configured) and adds
    /// <see cref="Web.WebTools.Guidance"/> to its instructions.
    /// </summary>
    public static IServiceCollection AddSwarmCoreWebTools(this IServiceCollection services, Action<Web.WebToolsOptions>? configure = null)
    {
        services.AddSwarmCoreWebSearch(configure);
        if (!services.Any(d => d.ImplementationType == typeof(Web.WebToolRegistration)))
            services.AddHostedService<Web.WebToolRegistration>();
        return services;
    }

    /// <summary>
    /// The web search and fetch services without catalog publication, for a process that exposes them some other way
    /// (an MCP bridge): options, the named HTTP clients, the key-source default, the Brave provider and <see cref="Web.WebTools"/>.
    /// </summary>
    public static IServiceCollection AddSwarmCoreWebSearch(this IServiceCollection services, Action<Web.WebToolsOptions>? configure = null)
    {
        services.AddOptions<Web.WebToolsOptions>();
        if (configure is not null) services.Configure(configure);
        services.AddHttpClient(Web.BraveWebSearchProvider.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All });
        services.AddHttpClient(Web.WebTools.FetchHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                var handler = SwarmCore.Abstractions.Networking.PublicNetworkAddress.CreateGuardedHandler(allowLoopback: false, allowAutoRedirect: true);
                handler.AutomaticDecompression = System.Net.DecompressionMethods.All;
                return handler;
            });
        services.TryAddSingleton<Web.IWebSearchKeySource, Web.NoWebSearchKeySource>();
        services.TryAddSingleton<Web.IWebSearchProvider, Web.BraveWebSearchProvider>();
        services.TryAddSingleton<Web.WebTools>();
        return services;
    }
}

/// <summary>Applies <see cref="CodingOptions.GitExecutable"/> to <see cref="GitExecutable"/> whenever the options are resolved.</summary>
internal sealed class GitExecutableOptionsHook : IPostConfigureOptions<CodingOptions>
{
    public void PostConfigure(string? name, CodingOptions options) => GitExecutable.Configure(options.GitExecutable);
}
