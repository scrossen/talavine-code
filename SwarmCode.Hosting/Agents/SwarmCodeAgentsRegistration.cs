using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmCore.Coding;
using SwarmCore.Coding.Agents;

namespace SwarmCode.Hosting.Agents;

public static class SwarmCodeAgentsRegistration
{
    /// <summary>
    /// Agents for a SwarmCode workspace: the durable team (one record per agent under
    /// <see cref="SwarmCodeDataPaths.AgentsDirectory"/>; a host may pre-register one team shared across workspaces),
    /// the session-backed <see cref="SwarmCodeAgentHost"/> bound while the workspace runs, a runner pool unless the host
    /// supplies its own runners, the native-lane bridge endpoint, and the guidance in the coding instructions.
    /// </summary>
    public static IServiceCollection AddSwarmCodeAgents(this IServiceCollection services, SwarmCodeDataPaths paths, IConfiguration configuration)
    {
        services.TryAddSingleton<IAgentTeamStore>(_ => new JsonFileAgentTeamStore(paths.AgentsDirectory));
        services.AddSwarmCoreAgentTeam(o => configuration.GetSection(AgentTeamOptions.SectionName).Bind(o));
        services.TryAddSingleton<IConversationRunnerSource>(sp => new ConversationRunnerPool(sp));
        services.TryAddSingleton<SwarmCodeAgentHost>();
        services.AddHostedService<AgentHostActivation>();
        services.PostConfigure<CodingOptions>(coding =>
        {
            var options = new AgentTeamOptions();
            configuration.GetSection(AgentTeamOptions.SectionName).Bind(options);
            if (!options.Enabled) return;
            coding.AdditionalInstructions = string.IsNullOrWhiteSpace(coding.AdditionalInstructions)
                ? AgentTeamPrompts.Guidance
                : coding.AdditionalInstructions.Trim() + Environment.NewLine + AgentTeamPrompts.Guidance;
            // Agent roles (agents/*.md) are listed for API lanes and, bounded, for native lanes' host context.
            if (!coding.AdditionalInjectorNames.Contains(AgentDefinitionsInjector.InjectorName, StringComparer.OrdinalIgnoreCase))
                coding.AdditionalInjectorNames.Add(AgentDefinitionsInjector.InjectorName);
            if (!coding.NativeLaneInjectorNames.Contains(AgentDefinitionsInjector.InjectorName, StringComparer.OrdinalIgnoreCase))
                coding.NativeLaneInjectorNames.Add(AgentDefinitionsInjector.InjectorName);
        });
        return services;
    }
}

/// <summary>
/// Binds this workspace's host to the team while the workspace runs, so agents start in the open repository, and runs
/// this workspace's SubagentStop hooks when an agent settles (the team is host-neutral and raises
/// <see cref="AgentTeam.AgentSettled"/> once per settlement).
/// </summary>
internal sealed class AgentHostActivation(AgentTeam team, SwarmCodeAgentHost host, IServiceProvider services, ILogger<AgentHostActivation>? logger = null) : IHostedService
{
    private volatile bool _stopping;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await team.InitializeAsync(cancellationToken).ConfigureAwait(false);
        team.AgentSettled += OnAgentSettledAsync;
        team.AttachHost(host);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        team.DetachHost(host, interruptRunning: true);
        team.AgentSettled -= OnAgentSettledAsync;
        return Task.CompletedTask;
    }

    // A shared team raises every settlement to every open workspace; only the bound workspace runs its hooks (plus the
    // interruptions its own detach caused, which arrive after the host is unbound).
    private async Task OnAgentSettledAsync(AgentRecord record)
    {
        if (!team.IsAttached(host) && !(_stopping && record.State == AgentState.Interrupted)) return;
        foreach (var warning in await Extensions.SwarmCodeTurnHooks.SubagentStopAsync(services, record).ConfigureAwait(false))
            logger?.LogWarning("SubagentStop hook for agent {Agent}: {Warning}", record.Id, warning);
    }
}
