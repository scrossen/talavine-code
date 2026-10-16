using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwarmCore.Coding.Routing;

namespace SwarmCore.Coding.Tests;

/// <summary>Builds an orchestrator over a test service collection with options-backed lanes and a fixed workspace.</summary>
internal static class TestOrchestrator
{
    public static string Workspace => Path.GetTempPath();

    public static CodingTurnOrchestrator Create(IServiceCollection services, CodingLaneOptions lanes, CodingOptions coding, LaneHealthTracker? health = null)
        => Create(services, lanes, coding, out _, health);

    public static CodingTurnOrchestrator Create(IServiceCollection services, CodingLaneOptions lanes, CodingOptions coding,
        out ServiceProvider provider, LaneHealthTracker? health = null)
    {
        services.AddSingleton<ICodingLaneSource>(new OptionsCodingLaneSource(Options.Create(lanes)));
        services.AddSingleton<ICodingWorkspaceResolver>(new FixedWorkspace(new CodingWorkspace { RepositoryRoot = Workspace }));
        provider = services.BuildServiceProvider();
        return new CodingTurnOrchestrator(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(coding),
            NullLogger<CodingTurnOrchestrator>.Instance, health);
    }

    internal sealed class FixedWorkspace(CodingWorkspace? workspace) : ICodingWorkspaceResolver
    {
        public Task<CodingWorkspace?> ResolveAsync(string conversationId, CancellationToken ct = default) => Task.FromResult(workspace);
    }
}
