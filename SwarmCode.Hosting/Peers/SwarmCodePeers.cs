using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding;
using SwarmCore.Coding.Agents;
using SwarmCore.Coding.Peers;
using SwarmCore.Missions;

namespace SwarmCode.Hosting.Peers;

public static class SwarmCodePeers
{
    public static IServiceCollection AddSwarmCodePeers(this IServiceCollection services, SwarmCodeDataPaths paths, IConfiguration configuration)
    {
        if (!configuration.GetValue("Peers:Enabled", true)) return services;
        services.AddHttpClient("swarmcode-peers").ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        { AllowAutoRedirect = false, UseProxy = false });
        services.TryAddSingleton(sp => new SwarmSpanPeerBridge(sp.GetRequiredService<IHttpClientFactory>().CreateClient("swarmcode-peers"),
            () => SwarmSpanPeerBridge.FindToken(configuration["Peers:SwarmSpanDataDirectory"])));
        services.TryAddSingleton<IPeerWorkBridge>(sp => sp.GetRequiredService<SwarmSpanPeerBridge>());
        services.TryAddSingleton<IPeerFileBridge>(sp => sp.GetRequiredService<SwarmSpanPeerBridge>());
        services.TryAddSingleton<PeerCoordinator>();
        services.TryAddSingleton(sp => new PeerToolScope(
            turn => turn is null ? null : "swarmcode-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(paths.RootDirectory + "\n" + turn.ConversationId))).ToLowerInvariant(),
            turn => turn?.CommunicationScopeUserInitiated == true && !turn.IsUnattended
                && !sp.GetRequiredService<ActiveCodingWorkspaces>().IsUnattendedTurn(turn.ConversationId)
                && !sp.GetRequiredService<AgentTeam>().IsAgent(turn.ConversationId)));
        services.AddHostedService<PeerToolRegistration>();
        services.AddHostedService<PeerFilesRegistration>();
        services.PostConfigure<CodingOptions>(coding => coding.AdditionalInstructions = string.IsNullOrWhiteSpace(coding.AdditionalInstructions)
            ? PeerTools.Guidance : coding.AdditionalInstructions.Trim() + Environment.NewLine + PeerTools.Guidance);
        return services;
    }
}

internal sealed class PeerFilesRegistration(ICapabilityCatalog catalog, IPeerFileBridge bridge, ISandboxPathResolver paths,
    PeerToolScope scope, ICurrentTurnContextAccessor turns) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var tool in PeerFileTools.Create(bridge, paths, () => scope.CanDelegate(turns.Current))) catalog.Register(tool);
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
