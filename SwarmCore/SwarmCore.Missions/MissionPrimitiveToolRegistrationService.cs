using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SwarmCore.Agent.Abstractions;

namespace SwarmCore.Missions;

/// <summary>
/// Publishes the shared primitive tools (shell, file read/write/list, HTTP) to
/// the host <see cref="ICapabilityCatalog"/>. Publication is discovery only:
/// whether a turn may use a tool is still decided by the profile's enabled
/// tool ids, the communication scope, the permission broker, and the mission
/// resource fence each primitive enforces.
/// </summary>
public static class MissionPrimitiveTools
{
    public static IReadOnlyList<string> ToolIds { get; } =
    [
        ShellExecTool.ToolId, ReadFileTool.ToolId, WriteFileTool.ToolId, ListFilesTool.ToolId, HttpRequestTool.ToolId,
    ];

    /// <summary>Registers every primitive descriptor; safe to call more than once (registration replaces by tool id).</summary>
    public static void RegisterAll(IServiceProvider services)
    {
        var catalog = services.GetRequiredService<ICapabilityCatalog>();
        catalog.Register(services.GetRequiredService<ShellExecTool>().Descriptor);
        catalog.Register(services.GetRequiredService<ReadFileTool>().Descriptor);
        catalog.Register(services.GetRequiredService<WriteFileTool>().Descriptor);
        catalog.Register(services.GetRequiredService<ListFilesTool>().Descriptor);
        catalog.Register(services.GetRequiredService<HttpRequestTool>().Descriptor);
    }
}

/// <summary>Hosted registrar so the primitives are in the catalog before the first turn.</summary>
public sealed class MissionPrimitiveToolRegistrationService(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        MissionPrimitiveTools.RegisterAll(services);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
