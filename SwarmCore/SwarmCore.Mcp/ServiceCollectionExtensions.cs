using SwarmCore.Mcp.Abstractions;
using SwarmCore.Mcp.Core;
using Microsoft.Extensions.DependencyInjection;

namespace SwarmCore.Mcp;

/// <summary>
/// Extension methods for registering SwarmCore.Mcp services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add SwarmCore MCP client services — connection management and skill bridging.
    /// Call this after AddSwarmCoreSkills() to enable MCP connector support.
    /// </summary>
    public static IServiceCollection AddSwarmCoreMcp(this IServiceCollection services)
    {
        services.AddSingleton<IMcpConnectorManager, McpConnectorManager>();
        services.AddSingleton<IMcpToolBridge, McpToolBridgeService>();
        return services;
    }
}
