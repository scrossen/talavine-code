using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SwarmCore.Agent.AppControl;

namespace SwarmCode.Hosting.Tests;

/// <summary>The official MCP SDK client (what SwarmCode's relay and native agents use) talks to an app's loopback server.</summary>
public sealed class AppControlSdkCompatTests
{
    [Fact]
    public async Task The_official_client_initializes_lists_and_calls_tools()
    {
        var token = LoopbackMcpServer.NewToken();
        LoopbackMcpTool[] tools =
        [
            new("find_tools", "Find tools", new() { ["type"] = "object", ["properties"] = new System.Text.Json.Nodes.JsonObject() }, true,
                (args, _) => Task.FromResult(new LoopbackMcpToolResult("found " + (args["query"]?.GetValue<string>() ?? "all")))),
        ];
        await using var server = new LoopbackMcpServer("testapp", "1.0", token, () => tools, NullLogger.Instance);
        server.Start();
        var transport = new HttpClientTransport(new()
        {
            Endpoint = new Uri(server.Url), TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
        });
        await using var client = await McpClient.CreateAsync(transport);
        var listed = await client.ListToolsAsync();
        Assert.Equal("find_tools", Assert.Single(listed).Name);
        var result = await client.CallToolAsync("find_tools", new Dictionary<string, object?> { ["query"] = "contact" });
        Assert.Equal("found contact", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }
}
