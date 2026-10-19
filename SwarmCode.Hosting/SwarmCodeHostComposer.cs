using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmCode.Hosting.Agents;
using SwarmCode.Hosting.Llm;
using SwarmCode.Hosting.Memory;
using SwarmCode.Hosting.Secrets;
using SwarmCode.Hosting.Web;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Runtime;
using SwarmCore.Agent.ToolUseEngine;
using SwarmCore.Coding;
using SwarmCore.Coding.Computer;
using SwarmCore.Coding.Routing;
using SwarmCore.Coding.Tools.BackgroundShell;
using SwarmCore.LlmRouting;
using SwarmCore.LlmRouting.Decisions;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.Secrets;
using SwarmCore.Services.Abstractions;

namespace SwarmCode.Hosting;

/// <summary>What a SwarmCode host (console or desktop) needs to compose the runtime for one repository.</summary>
public sealed class SwarmCodeHostOptions
{
    public required string RepositoryRoot { get; init; }
    public required SwarmCodeDataPaths DataPaths { get; init; }

    /// <summary>Configuration carrying Coding, CodingLanes, LlmProviders and Logging sections. Null means defaults only.</summary>
    public IConfiguration? Configuration { get; init; }

    /// <summary>Factory for the permission broker; defaults to <see cref="UiPermissionBroker"/> with auto-approve.</summary>
    public Func<IServiceProvider, IPermissionBroker>? PermissionBroker { get; init; }

    /// <summary>
    /// Factory for the broker behind <c>user.ask</c> (the agent asking the person a question mid-turn); defaults to
    /// <see cref="UiUserQuestionBroker"/>, which the session runners subscribe to. The console supplies
    /// <see cref="ConsoleUserQuestionBroker"/>.
    /// </summary>
    public Func<IServiceProvider, SwarmCore.Coding.Interaction.IUserQuestionBroker>? UserQuestionBroker { get; init; }

    /// <summary>
    /// Host registrations applied before the shared composition, so a host can pre-register
    /// its own instances (session catalog, permission broker, preferences, OS keystore, vault)
    /// and the composer's TryAdd defaults defer to them.
    /// </summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }

    /// <summary>
    /// Register the self-improvement loop's reporter, backlog and <c>harness.request_feature</c> tool. A developer feature:
    /// the SwarmCode hosts set it only in Debug builds, so a Release build carries none of it and settings cannot turn it on.
    /// </summary>
    public bool SelfImprovement { get; init; }
}

public static class SwarmCodeHostComposer
{
    /// <summary>
    /// Entropy label for the Windows DPAPI keystore. A persisted protocol identifier shared by
    /// the console and the desktop app so both read the same vault; changing it strands secrets.
    /// </summary>
    public const string KeyStoreApplicationScope = "SwarmCode";

    /// <summary>Builds a configuration from appsettings.json beside the executable, the user settings file, and SWARMCODE_ environment variables.</summary>
    public static IConfiguration BuildConfiguration(SwarmCodeDataPaths paths, IDictionary<string, string?>? overrides = null)
    {
        var builder = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true, reloadOnChange: false)
            .AddJsonFile(paths.UserConfigPath, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("SWARMCODE_");
        if (overrides is not null) builder.AddInMemoryCollection(overrides);
        return builder.Build();
    }

    /// <summary>
    /// Registers the secret vault stack on its own: OS keystore (DPAPI on Windows, authenticated
    /// bundle broker on Mac unless the host pre-registered Keychain), envelope protector, SQLite
    /// vault at <see cref="SwarmCodeDataPaths.SecretsDbPath"/>, the <see cref="ICredentialStore"/>
    /// seam over it, the vault-backed API key provider, chat intake, and CSV import. Hosts that
    /// need the vault before any repository is open (Settings, /secret at the prompt) call this
    /// directly; <see cref="AddSwarmCodeHost"/> calls it too and defers to pre-registered instances.
    /// </summary>
    public static IServiceCollection AddSwarmCodeSecrets(this IServiceCollection services, SwarmCodeDataPaths paths)
    {
        services.TryAddSingleton(paths);
        services.TryAddSingleton<IOsKeyStore>(sp => OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()
            ? MacVaultBridge.CreateHelperKeyStore(paths)
            : KeyStoreFactory.Create(sp.GetRequiredService<ILoggerFactory>(), KeyStoreApplicationScope));
        services.TryAddSingleton<ISecretProtector>(sp => CreateVaultProtector(sp.GetRequiredService<IOsKeyStore>(),
            sp.GetRequiredService<ILogger<EnvelopeSecretProtector>>(), OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()));
        services.TryAddSingleton<ISecretVault>(sp => new SqliteSecretVault(sp.GetRequiredService<SwarmCodeDataPaths>().SecretsDbPath, sp.GetRequiredService<ISecretProtector>(), sp.GetRequiredService<ILogger<SqliteSecretVault>>()));
        services.TryAddSingleton<ICredentialStore>(sp => new VaultCredentialStore(sp.GetRequiredService<ISecretVault>()));
        services.TryAddSingleton<VaultApiKeyProvider>();
        services.AddSwarmCodeWebSearchCredentialIntake();
        services.TryAddSingleton<SecretIntake>();
        services.TryAddSingleton<PasswordCsvImporter>();
        return services;
    }

    internal static ISecretProtector CreateVaultProtector(IOsKeyStore keyStore, ILogger<EnvelopeSecretProtector> logger, bool requireEncryption)
    {
        var envelope = new EnvelopeSecretProtector(keyStore, logger);
        return requireEncryption ? new EncryptionRequiredSecretProtector(envelope) : envelope;
    }

    /// <summary>
    /// Registers everything a SwarmCode host needs: data paths, the protected secret vault and
    /// BYOK keys, LLM routing with a durable audit store, file-backed conversation and transcript
    /// stores, the permission broker, the coding harness, and lane seeding. Logging and the host
    /// lifetime remain the caller's job.
    /// </summary>
    public static IServiceCollection AddSwarmCodeHost(this IServiceCollection services, SwarmCodeHostOptions options)
    {
        var configuration = options.Configuration ?? new ConfigurationBuilder().Build();
        var repoRoot = Path.GetFullPath(options.RepositoryRoot);

        services.AddSingleton(options.DataPaths);
        services.AddSingleton(configuration);
        services.AddSingleton(new ModelSourceEnvironmentKeys(repoRoot, new ModelSourcesStore(options.DataPaths).Read().Sources));
        services.AddHttpClient();
        options.ConfigureServices?.Invoke(services);
        services.AddSwarmCoreDecisions();
        services.TryAddSingleton<SwarmCore.Abstractions.LlmRouting.Decisions.IDecisionCredentials, VaultDecisionCredentials>();
        services.TryAddSingleton<DecisionSettingsStore>();
        services.TryAddSingleton<DecisionSessionCoordinator>();

        var providerOptions = new LlmProvidersOptions();
        configuration.GetSection(LlmProvidersOptions.SectionName).Bind(providerOptions);
        services.AddSingleton(providerOptions);
        // Provider clients and their tool-use translators consume typed options. Bind the
        // same host configuration used for lane seeding, including custom endpoints.
        services.Configure<OpenAIProviderOptions>(configuration.GetSection("LlmProviders:OpenAI"));
        services.Configure<ClaudeProviderOptions>(configuration.GetSection("LlmProviders:Claude"));
        services.Configure<GeminiProviderOptions>(configuration.GetSection("LlmProviders:Gemini"));
        services.Configure<DeepSeekProviderOptions>(configuration.GetSection("LlmProviders:DeepSeek"));
        services.Configure<OpenRouterProviderOptions>(configuration.GetSection("LlmProviders:OpenRouter"));
        services.Configure<GrokProviderOptions>(configuration.GetSection("LlmProviders:Grok"));
        services.Configure<HuggingFaceProviderOptions>(configuration.GetSection("LlmProviders:HuggingFace"));
        services.Configure<VeniceProviderOptions>(configuration.GetSection("LlmProviders:Venice"));
        services.Configure<OllamaProviderOptions>(configuration.GetSection("LlmProviders:Ollama"));
        services.Configure<LMStudioProviderOptions>(configuration.GetSection("LlmProviders:LMStudio"));

        services.AddSwarmCodeSecrets(options.DataPaths);
        services.TryAddSingleton<SettingsApiKeyProvider>();
        services.TryAddSingleton<SwarmCodeApiKeyProvider>();

        // Registered before AddSwarmCoreLlmRouting so the fail-closed default audit store is not used.
        services.AddSingleton<ILlmRoutingAuditStore>(sp => new JsonLlmRoutingAuditStore(sp.GetRequiredService<SwarmCodeDataPaths>()));

        services.AddSwarmCoreLlmRouting(o =>
        {
            o.UseSettingsProvider(sp => new JsonLlmSettingsProvider(sp.GetRequiredService<SwarmCodeDataPaths>()));
            o.UseUsageTracker(sp => new JsonLlmUsageTracker(sp.GetRequiredService<SwarmCodeDataPaths>()));
            o.UseApiKeyProvider(sp => sp.GetRequiredService<SwarmCodeApiKeyProvider>());
            o.UseCapabilitiesService<SwarmCodeLlmCapabilitiesService>(ServiceLifetime.Singleton);
            o.UseCostService<SwarmCodeLlmCostService>(ServiceLifetime.Singleton);

            o.CloudProviders = LlmCloudProviders.OpenAI
                | LlmCloudProviders.Claude
                | LlmCloudProviders.Gemini
                | LlmCloudProviders.DeepSeek
                | LlmCloudProviders.OpenRouter
                | LlmCloudProviders.Grok
                | LlmCloudProviders.HuggingFace
                | LlmCloudProviders.Venice;
            o.Ollama = OllamaRegistration.ProviderWithAutoConfiguration;
            o.IncludeLMStudio = true;
            o.IncludeSubscriptionCliProviders = true;
            o.IncludeDynamicRoutingAdvisors = true;
            o.IncludeToolUseProviders = true; // required: without it the agent loop has no tool-use translators
        });

        // Personal memory (facts about the user under SWARMCODE_HOME) and repository memory (MEMORY.md index, topic search,
        // reviewable proposals). Registered before AddSwarmCoreCoding so the durable proposal store wins.
        services.AddSwarmCodePersonalMemory(options.DataPaths, configuration);

        // Durable stores must be registered before AddSwarmCoreCoding, whose defaults are TryAdd in-memory.
        services.TryAddSingleton<IConversationStore, FileConversationStore>();
        services.TryAddSingleton<FileAgentSessionStore>();
        services.TryAddSingleton<IAgentSessionStore>(sp => sp.GetRequiredService<FileAgentSessionStore>());
        services.TryAddSingleton<ITranscriptRewriter>(sp => sp.GetRequiredService<FileAgentSessionStore>());
        // Checkpointing is what makes the engine persist receipts at every durable boundary, so a cancelled turn keeps its work.
        services.TryAddSingleton<IWorkingContextCheckpointStore>(sp => new FileWorkingContextCheckpointStore(sp.GetRequiredService<SwarmCodeDataPaths>(), sp.GetService<WorkingContextRetentionPolicy>(), logger: sp.GetRequiredService<ILogger<FileWorkingContextCheckpointStore>>()));
        services.TryAddSingleton<IWorkingContextSourceValidator, CodingWorkingContextSourceValidator>();
        services.TryAddSingleton<WorkingContextCheckpointManager>();
        // Model-backed compaction summaries that replay the last request's cached prefix (system prompt,
        // tools, leading messages) and append only the instruction, so compaction does not cold-start the cache.
        services.TryAddScoped<IWorkingContextSummarizer, PrefixReplayWorkingContextSummarizer>();
        // The tool-call journal makes a tool request durable before it runs and records which calls
        // started, so a crash mid-tool is closed on the next turn with "never ran" or "outcome unknown".
        services.TryAddSingleton<IPendingToolCallJournal, FilePendingToolCallJournal>();
        // Append-only presentation journal: the runner records each finalized entry (tool cards, notices,
        // hand-offs, answers) so a reopened session replays exactly what the user saw.
        services.TryAddSingleton<FileTranscriptJournal>();
        services.TryAddSingleton<WorkspaceSessionCatalog>();
        services.TryAddSingleton<ConversationForkService>();
        // Saved sessions across every repository (index + turns journals) for history search and work summaries.
        services.TryAddSingleton<SwarmCore.Coding.History.IConversationHistorySource, ConversationHistorySource>();
        // Conversations work in the open repository, except an isolated agent's session, which works in its own worktree.
        services.TryAddSingleton<SwarmCore.Coding.ICodingWorkspaceResolver, SessionCodingWorkspaceResolver>();
        services.TryAddSingleton<FileConversationJobStore>();
        // The session's task list (plan.update, mirrored native todo lists).
        services.TryAddSingleton<FileConversationPlanStore>();
        services.TryAddSingleton<SwarmCore.Coding.Planning.IConversationPlanStore>(sp => sp.GetRequiredService<FileConversationPlanStore>());
        services.TryAddSingleton<AttachmentStore>();
        services.AddCodingComputer(o =>
        {
            o.BrowserProfileDirectory = Path.Combine(options.DataPaths.RootDirectory, "browser", "chrome");
            configuration.GetSection(CodingComputerOptions.SectionName).Bind(o);
        });

        if (options.PermissionBroker is not null)
            services.AddSingleton(options.PermissionBroker);
        else
            services.TryAddSingleton<IPermissionBroker>(_ => new UiPermissionBroker { AutoApprove = true });
        // user.ask: the desktop renders question cards through the runners; the console reads a line; a host may pre-register its own.
        if (options.UserQuestionBroker is not null)
            services.AddSingleton(options.UserQuestionBroker);
        else
            services.TryAddSingleton<SwarmCore.Coding.Interaction.IUserQuestionBroker>(sp => new UiUserQuestionBroker(sp.GetService<WorkspaceSessionCatalog>()));
        services.TryAddSingleton<AppPreferencesStore>();
        services.TryAddSingleton<UserSettingsFile>();
        services.TryAddSingleton<Settings.SettingsActivation>();
        services.TryAddSingleton<SwarmCore.Coding.Settings.IAgentSettingsHost, Settings.SwarmCodeSettingsHost>();
        services.AddHostedService<Settings.SwarmCodeSettingsToolRegistration>();
        services.PostConfigure<CodingOptions>(coding => coding.AdditionalInstructions =
            (coding.AdditionalInstructions ?? "") + Environment.NewLine + SwarmCore.Coding.Settings.AgentSettingsTools.Guidance);
        services.TryAddScoped<ICodingAcceptanceVerifier, ReloadingCodingAcceptanceVerifier>();
        services.TryAddScoped<ICodingFallbackDiscovery, CatalogCodingFallbackDiscovery>();
        services.TryAddSingleton<IModelResponseMemory, SqliteModelResponseMemory>();
        // Repository hook approvals live in the data directory; registered before the harness so its fail-closed default is unused.
        Extensions.SwarmCodeExtensions.AddSwarmCodeExtensionStores(services, options.DataPaths);

        services.AddSwarmCoreCoding(
            coding =>
            {
                configuration.GetSection(CodingOptions.SectionName).Bind(coding);
                coding.RepositoryRoot = repoRoot;
            },
            lanes => configuration.GetSection(CodingLaneOptions.SectionName).Bind(lanes));
        services.AddSingleton<IPostConfigureOptions<CodingLaneOptions>, DefaultLaneSeeder>();
        // user.ask: the agent asks the person a question mid-turn (native lanes reach it as user_ask through the bridge).
        SwarmCore.Coding.Interaction.UserQuestionServiceCollectionExtensions.AddSwarmCoreUserQuestions(services);
        services.PostConfigure<CodingOptions>(coding => coding.AdditionalInstructions = string.IsNullOrWhiteSpace(coding.AdditionalInstructions)
            ? SwarmCore.Coding.Interaction.UserQuestionPrompts.Guidance
            : coding.AdditionalInstructions.Trim() + Environment.NewLine + SwarmCore.Coding.Interaction.UserQuestionPrompts.Guidance);
        services.AddSwarmCodeModelSources(options.DataPaths);
        var learningOptions = new SwarmCore.HarnessLearning.HarnessLearningOptions();
        configuration.GetSection("HarnessLearning").Bind(learningOptions);
        SwarmCore.Agent.Learning.HarnessLearningRegistration.AddHarnessLearning(services,
            _ => new(Path.Combine(options.DataPaths.RootDirectory, "harness-learning"), "code", () => true), learningOptions);

        services.AddSwarmCodeExternalMcp();
        // Agents that sessions start (agents.*).
        services.AddSwarmCodeAgents(options.DataPaths, configuration);
        Peers.SwarmCodePeers.AddSwarmCodePeers(services, options.DataPaths, configuration);
        // Native lanes (Claude Code, Codex, Gemini CLI) reach every tool above through one host tool bridge: the
        // conversation's own profile, run here under its turn (personal memory included), with the plan subset in plan turns.
        SwarmCore.Coding.Native.HostToolBridgeServiceCollectionExtensions.AddSwarmCoreHostToolBridge(services, (sp, conversationId) => sp.GetService<Memory.SwarmCodeMemoryTurns>()?.BeginBridgedCall(conversationId));
        // Web search (Brave, key in the vault) and readable page fetch for API lanes.
        services.AddSwarmCodeWebTools(configuration);
        Services.SwarmCodeServiceCatalog.AddSwarmCodeServiceCatalog(services, options.DataPaths);
        // Adversarial review across the configured models (review.panel; /review in the hosts): answers, then blind ranking.
        SwarmCore.Coding.Review.ModelPanelReviewServiceCollectionExtensions.AddSwarmCoreModelPanelReview(services,
            o => configuration.GetSection(SwarmCore.Coding.Review.ModelPanelReviewOptions.SectionName).Bind(o));
        services.PostConfigure<CodingOptions>(coding => coding.AdditionalInstructions = string.IsNullOrWhiteSpace(coding.AdditionalInstructions)
            ? SwarmCore.Coding.Review.ModelPanelReviewPrompts.Guidance
            : coding.AdditionalInstructions.Trim() + Environment.NewLine + SwarmCore.Coding.Review.ModelPanelReviewPrompts.Guidance);
        // Saved-session history for the agent (history.search, history.summary; /history and /summary in the app).
        SwarmCore.Coding.History.ConversationHistoryServiceCollectionExtensions.AddSwarmCoreConversationHistory(services,
            o => configuration.GetSection(SwarmCore.Coding.History.WorkSummaryOptions.SectionName).Bind(o));
        services.PostConfigure<CodingOptions>(coding => coding.AdditionalInstructions = string.IsNullOrWhiteSpace(coding.AdditionalInstructions)
            ? SwarmCore.Coding.History.WorkSummaryPrompts.Guidance
            : coding.AdditionalInstructions.Trim() + Environment.NewLine + SwarmCore.Coding.History.WorkSummaryPrompts.Guidance);
        // Background shells (shell.background.*).
        services.AddSwarmCoreBackgroundShell(o => configuration.GetSection(BackgroundShellOptions.SectionName).Bind(o));
        // schedules.* when the host keeps a scheduler above its workspaces.
        SwarmCore.Coding.Scheduling.ScheduleServiceCollectionExtensions.AddSwarmCoreCodingScheduleTools(services);
        // Operating the person's apps (Talavine, the CRM, any MCP server) by prompt.
        services.TryAddSingleton(sp => new Apps.ConnectedApps(options.DataPaths, sp.GetRequiredService<SwarmCore.Secrets.ISecretVault>(), sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()?.CreateLogger("SwarmCode.Apps")));
        services.AddHostedService<Apps.ConnectedAppsToolRegistration>();
        // GitHub / pull-request tools (opt-in forge registrar; never part of CodingToolRegistrar.CodingToolIds).
        services.AddSwarmCoreCodingForge(configuration);
        services.PostConfigure<CodingOptions>(coding => coding.AdditionalInstructions = string.IsNullOrWhiteSpace(coding.AdditionalInstructions)
            ? SwarmCore.Coding.Forge.ForgeGuidance.Text
            : coding.AdditionalInstructions.Trim() + Environment.NewLine + SwarmCore.Coding.Forge.ForgeGuidance.Text);
        services.PostConfigure<CodingOptions>(coding => coding.AdditionalInstructions = string.IsNullOrWhiteSpace(coding.AdditionalInstructions)
            ? Apps.ConnectedAppsTools.Guidance
            : coding.AdditionalInstructions.Trim() + Environment.NewLine + Apps.ConnectedAppsTools.Guidance);
        // Self-improvement (Debug hosts only): defects and capability gaps observed in turns go to the backlog; harness.request_feature for the model.
        if (options.SelfImprovement) Improvement.SwarmCodeSelfImprovement.AddSwarmCodeSelfImprovement(services, options.DataPaths, configuration);
        // Custom commands, skills and hooks. Last: the hook decorators wrap whichever guard and profile resolver are registered.
        Extensions.SwarmCodeExtensions.AddSwarmCodeExtensions(services, options.DataPaths);
        return services;
    }

    /// <summary>
    /// External MCP servers (<see cref="SwarmCodeDataPaths.McpServersPath"/> plus
    /// <c>Coding:NativeAgents:ExternalMcpServers</c>): native lanes mount them through
    /// <see cref="NativeAgentOptions.ExternalMcpServers"/>, and API lanes get their tools as
    /// <c>mcp.&lt;server&gt;.&lt;tool&gt;</c> catalog entries through SwarmCore's connector manager,
    /// published before the profile resolves. Publication grants no authority.
    /// </summary>
    private static IServiceCollection AddSwarmCodeExternalMcp(this IServiceCollection services)
    {
        services.TryAddSingleton<Mcp.ExternalMcpServersFile>();
        services.AddSingleton<IPostConfigureOptions<CodingOptions>, Mcp.ExternalMcpNativeOptions>();
        services.TryAddSingleton<SwarmCore.Mcp.Abstractions.IMcpConnectorManager, SwarmCore.Mcp.Core.McpConnectorManager>();
        services.TryAddSingleton<Mcp.ExternalMcpToolCatalog>();

        // Decorate whichever profile resolver the harness (or the host) registered.
        var existing = services.LastOrDefault(d => d.ServiceType == typeof(IProfileResolver));
        if (existing is null) return services;
        services.Remove(existing);
        services.Add(ServiceDescriptor.Describe(
            typeof(IProfileResolver),
            sp => new Mcp.ExternalMcpProfileResolver(
                (IProfileResolver)(existing.ImplementationInstance
                    ?? existing.ImplementationFactory?.Invoke(sp)
                    ?? ActivatorUtilities.CreateInstance(sp, existing.ImplementationType!)),
                sp.GetRequiredService<Mcp.ExternalMcpToolCatalog>()),
            existing.Lifetime));
        return services;
    }
}
