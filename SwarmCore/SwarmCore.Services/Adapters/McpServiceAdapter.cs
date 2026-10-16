using SwarmCore.Services.Abstractions;

namespace SwarmCore.Services.Adapters;

/// <summary>Where an MCP service points: a connector (stdio command or http url) and a tool on it.</summary>
public sealed record McpServiceTarget(string ConnectorName, string Transport, string? Command, string? Arguments, string? Url, string ToolName);

/// <summary>
/// Seam for talking to MCP servers. The host implements it over its MCP client
/// (SwarmCore.Mcp), so the Services library stays free of the MCP SDK and the
/// subprocess/connection lifecycle.
/// </summary>
public interface IMcpToolInvoker
{
    Task<ServiceCallResult> InvokeAsync(McpServiceTarget target, IReadOnlyDictionary<string, object?> args, CancellationToken ct = default);
}

/// <summary>
/// Invokes a tool exposed by an MCP server. Config keys: connectorName, transport
/// (stdio|http), command, arguments, url, tool. Delegates the actual protocol to
/// an <see cref="IMcpToolInvoker"/> supplied by the host.
/// </summary>
public sealed class McpServiceAdapter : IServiceKindAdapter
{
    private readonly IMcpToolInvoker? _invoker;

    public McpServiceAdapter(IMcpToolInvoker? invoker = null) => _invoker = invoker;

    public ServiceKind Kind => ServiceKind.Mcp;

    public async Task<ServiceCallResult> InvokeAsync(ServiceDefinition s, IReadOnlyDictionary<string, object?> args, CancellationToken ct = default)
    {
        await SwarmCore.Abstractions.Security.ExecutionResourceBoundary.CheckUnclassifiedAsync("service.mcp", s.Name, ct);
        if (_invoker is null)
            return ServiceCallResult.Fail("MCP isn't configured in this host.");

        var c = s.Config;
        var tool = c.GetValueOrDefault("tool");
        if (string.IsNullOrWhiteSpace(tool)) return ServiceCallResult.Fail("MCP service has no 'tool' configured.");

        var target = new McpServiceTarget(
            ConnectorName: c.GetValueOrDefault("connectorName", s.Name)!,
            Transport: c.GetValueOrDefault("transport", "stdio")!,
            Command: c.GetValueOrDefault("command"),
            Arguments: c.GetValueOrDefault("arguments"),
            Url: c.GetValueOrDefault("url"),
            ToolName: tool!);

        return await _invoker.InvokeAsync(target, args, ct);
    }
}
