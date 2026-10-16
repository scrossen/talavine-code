using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmCore.Coding;

namespace SwarmCode.Hosting.Mcp;

/// <summary>
/// Adds the servers listed in <see cref="SwarmCodeDataPaths.McpServersPath"/> to
/// <see cref="NativeAgentOptions.ExternalMcpServers"/>, after any configured in
/// <c>Coding:NativeAgents:ExternalMcpServers</c>. A name already configured wins, so settings can
/// override the shared file. The file is read once per workspace host.
/// </summary>
internal sealed class ExternalMcpNativeOptions(ExternalMcpServersFile file, ILogger<ExternalMcpNativeOptions> logger) : IPostConfigureOptions<CodingOptions>
{
    public void PostConfigure(string? name, CodingOptions options)
    {
        ExternalMcpServersSnapshot snapshot;
        try { snapshot = file.Read(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read the external MCP server list at {Path}", file.Path);
            return;
        }
        foreach (var problem in snapshot.Problems)
            logger.LogWarning("External MCP server list {Path}: {Problem}", file.Path, problem);

        var configured = new HashSet<string>(options.NativeAgents.ExternalMcpServers.Select(s => s.ServerName), StringComparer.OrdinalIgnoreCase);
        foreach (var server in snapshot.Servers)
        {
            if (configured.Add(server.Name)) options.NativeAgents.ExternalMcpServers.Add(server.ToLaunch());
        }
    }
}
