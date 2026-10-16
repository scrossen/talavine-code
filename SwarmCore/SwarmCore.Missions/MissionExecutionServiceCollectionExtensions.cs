using SwarmCore.Agent;
using SwarmCore.Abstractions.CodingTasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SwarmCore.Mcp.Abstractions;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;
using SwarmCore.Abstractions.Packaging;

namespace SwarmCore.Missions;

public static class MissionExecutionServiceCollectionExtensions
{
    /// <summary>Opt-in publication of the shared missions.* tools to the host catalog.</summary>
    public static IServiceCollection AddSwarmCoreMissionTools(this IServiceCollection services)
    {
        services.AddSwarmCoreMissions();
        services.AddHostedService<MissionToolRegistrationService>();
        return services;
    }

    /// <summary>Adds the durable mission lifecycle using host-provided IMissionStore,
    /// IAgentTurnRunner and IConversationStore implementations. The host owns scheduling.</summary>
    public static IServiceCollection AddSwarmCoreMissions(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(MissionService))) return services;
        services.AddSwarmCoreContinualAgent(registerDefaultProcessor: false);
        services.AddSwarmCoreMissionExecution();
        services.AddOptions<MissionRuntimeOptions>().Validate(o => !string.IsNullOrWhiteSpace(o.ExecutorKey));
        services.TryAddSingleton<MissionTurnObserver>();
        services.AddSingleton<IAgentTurnProgressObserver>(sp => sp.GetRequiredService<MissionTurnObserver>());
        services.TryAddScoped<MissionTurnExecutor>();
        services.TryAddScoped<MissionAssignmentInterpreter>();
        services.TryAddSingleton<PersonalRecurrenceInterpretationCache>();
        services.TryAddScoped<MissionProcessorHost>();
        services.TryAddScoped<MissionService>();
        services.TryAddScoped<SwarmCore.Abstractions.Security.ISecurityTaskService, SecurityTaskService>();
        services.TryAddSingleton<ICodingWorkspaceService, CodingWorkspaceService>();
        services.TryAddScoped<IVerifiedCodingTaskExecutor, VerifiedCodingTaskExecutor>();
        services.AddOptions<CodingTaskOptions>();
        services.TryAddScoped<MissionDelegationService>();
        services.TryAddSingleton<IMissionSteeringAuthority, MissionSteeringAuthority>();
        return services;
    }

    /// <summary>Registers reusable execution services. The host supplies browser/profile
    /// paths, mission store, lease context, and any optional audit/redaction integration.
    /// The primitive tools are published to the capability catalog by a hosted registrar
    /// (or <see cref="MissionPrimitiveTools.RegisterAll"/> for hosts without hosted services);
    /// publication grants no authority.</summary>
    /// <remarks>Idempotent: a second call (for example from a harness composed into a host that
    /// already registered mission tools) adds nothing, so named HTTP client configuration and
    /// other non-TryAdd registrations are never duplicated.</remarks>
    public static IServiceCollection AddSwarmCoreMissionExecution(this IServiceCollection services)
    {
        if (services.Any(d => d.ImplementationType == typeof(MissionPrimitiveToolRegistrationService)))
            return services;
        services.AddHostedService<MissionPrimitiveToolRegistrationService>();
        services.TryAddSingleton<OptionalHostCapabilityPolicy>();
        services.AddOptions<SandboxOptions>();
        services.AddOptions<MissionNetworkOptions>();
        services.AddOptions<MissionDeploymentOptions>().Validate(options => options.DevelopmentDirectories.All(pair =>
            !string.IsNullOrWhiteSpace(pair.Key) && !pair.Key.Any(char.IsControl)
            && Path.IsPathFullyQualified(pair.Value) && !pair.Value.Any(char.IsControl)
            && Path.GetFullPath(pair.Value).TrimEnd(Path.DirectorySeparatorChar) != Path.GetPathRoot(pair.Value)?.TrimEnd(Path.DirectorySeparatorChar)),
            "Mission development targets require nonempty names and absolute directories below a filesystem root.");
        services.AddOptions<HttpRequestToolOptions>();
        services.TryAddSingleton<ISandboxPathResolver, SandboxPathResolver>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpMissionBinding, MissionMcpBinding>());
        services.TryAddSingleton<MissionBrowserService>();
        services.TryAddScoped<MissionMcpProcessService>();
        services.TryAddScoped<MissionDeploymentService>();
        services.TryAddScoped<MissionDependencyService>();
        services.TryAddScoped<MissionConstraintService>();
        services.TryAddSingleton<ShellExecTool>();
        services.TryAddSingleton<ReadFileTool>();
        services.TryAddSingleton<WriteFileTool>();
        services.TryAddSingleton<ListFilesTool>();
        services.TryAddSingleton<HttpRequestTool>();
        // Connect-time address guard: the pre-flight host check cannot see DNS rebinding.
        services.AddHttpClient(HttpRequestTool.NoRedirectClientName)
            .ConfigurePrimaryHttpMessageHandler(() => SwarmCore.Abstractions.Networking.PublicNetworkAddress.CreateGuardedHandler(allowLoopback: true, allowAutoRedirect: false));
        return services;
    }
}
