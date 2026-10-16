using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SwarmCore.Coding.Native;

namespace SwarmCode.Hosting.Mcp;

/// <summary>
/// The MCP server a native coding agent starts (<c>swarmcode --mcp-server --repo &lt;root&gt;</c>), talking JSON-RPC over
/// stdin/stdout, so nothing may be written to the console here: logs go to the data directory. It is a thin adapter: it
/// lists the tools the SwarmCode host offers the conversation it was launched for and forwards each call over the host
/// tool bridge (<see cref="HostToolBridgeBroker"/>), where every tool runs under that conversation's turn with the same
/// guard and hooks an API lane gets. It holds no vault, browser or memory of its own; without a host connection it offers
/// no tools.
/// </summary>
public static class SwarmCodeMcpServer
{
    public const string ServerName = "swarmcode";

    public static async Task<int> RunAsync(SwarmCodeDataPaths paths, string repositoryRoot, CancellationToken ct = default)
    {
        using var loggers = LoggerFactory.Create(b =>
        {
            b.AddProvider(new FileLoggerProvider(Path.Combine(paths.LogsDirectory, "swarmcode-mcp.log")));
            b.SetMinimumLevel(LogLevel.Information);
        });
        var logger = loggers.CreateLogger("SwarmCode.Bridge");
        var proxy = HostToolBridgeProxy.FromEnvironment();
        if (proxy is null) logger.LogWarning("No Talavine Code host connection in the environment; the bridge offers no tools.");

        await using var server = McpServer.Create(new StdioServerTransport(ServerName, loggerFactory: null),
            ServerOptions(proxy, logger), loggerFactory: null, serviceProvider: null);
        try
        {
            await server.RunAsync(ct).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) { return 0; }
    }

    /// <summary>The MCP handlers over a host connection (also the test seam).</summary>
    public static McpServerOptions ServerOptions(HostToolBridgeProxy? proxy, ILogger? logger = null) => new()
    {
        ServerInfo = new() { Name = ServerName, Version = typeof(SwarmCodeMcpServer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0" },
        Capabilities = new() { Tools = new() },
        Handlers = new()
        {
            ListToolsHandler = async (_, ct) => new ListToolsResult { Tools = await ListAsync(proxy, logger, ct).ConfigureAwait(false) },
            CallToolHandler = async (request, ct) => await CallAsync(proxy, request.Params, ct).ConfigureAwait(false),
        },
    };

    private static async Task<IList<Tool>> ListAsync(HostToolBridgeProxy? proxy, ILogger? logger, CancellationToken ct)
    {
        if (proxy is null) return [];
        try
        {
            return (await proxy.ListAsync(ct).ConfigureAwait(false)).Select(tool => new Tool
            {
                Name = tool.Name,
                Description = tool.Description,
                InputSchema = JsonSerializer.SerializeToElement(tool.InputSchema),
                Annotations = new ToolAnnotations { ReadOnlyHint = tool.ReadOnly },
            }).ToList();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or JsonException)
        {
            logger?.LogWarning("Could not list Talavine Code tools ({Error}).", ex.GetType().Name);
            return [];
        }
    }

    private static async Task<CallToolResult> CallAsync(HostToolBridgeProxy? proxy, CallToolRequestParams? request, CancellationToken ct)
    {
        if (proxy is null || request is null) return Text("Talavine Code tools are available only inside a Talavine Code session.", isError: true);
        HostToolResult result;
        try
        {
            var arguments = request.Arguments is { } args ? JsonSerializer.SerializeToElement(args) : (JsonElement?)null;
            result = await proxy.CallAsync(request.Name, arguments, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or JsonException)
        {
            return Text(ex.Message, isError: true);
        }
        return ToMcp(result);
    }

    /// <summary>A host result as MCP content: the text, then each image as an image block (pixels never inside the text).</summary>
    internal static CallToolResult ToMcp(HostToolResult result)
    {
        var content = new List<ContentBlock> { new TextContentBlock { Text = result.Text } };
        foreach (var image in result.Images)
            content.Add(ImageContentBlock.FromBytes(Convert.FromBase64String(image.Base64), image.MediaType));
        return new CallToolResult { Content = content, IsError = result.IsError };
    }

    private static CallToolResult Text(string text, bool isError) => new() { Content = [new TextContentBlock { Text = text }], IsError = isError };
}
