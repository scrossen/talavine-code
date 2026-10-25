using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCore.Agent.Abstractions;

namespace SwarmCore.Coding.Native;

/// <summary>
/// Recognizes a saved goal receipt as a call to one of this host's own bridged tools and knows how
/// each transport stored the bridge's result text. Identity comes from the receipt's tool name and
/// input (the bridge server name and the tool's canonical MCP name), never from result content.
/// </summary>
internal static class OwnHostToolReceipts
{
    internal enum ResultEncoding
    {
        /// <summary>The stored result is the MCP result object; the bridge text is <c>content[0].text</c>.</summary>
        McpResult,
        /// <summary>The stored result is the bridge text itself.</summary>
        Text,
    }

    internal sealed record Transport(ResultEncoding Encoding, string Description);

    private static readonly Transport CodexMcp = new(ResultEncoding.McpResult, "the selected native lane's own-host MCP relay");
    private static readonly Transport ClaudeBridge = new(ResultEncoding.Text, "the native lane's own-host MCP bridge");
    private static readonly Transport InProcess = new(ResultEncoding.Text, "the in-process tool engine");

    /// <summary>
    /// True when the receipt is a call to <paramref name="toolId"/> through this host's bridge or engine.
    /// A foreign MCP server, another tool, a bridge name the current catalog does not serve or serves
    /// ambiguously, or an input that is not the bridge's call shape all return false.
    /// </summary>
    public static bool TryIdentify(string toolName, string? inputJson, string toolId, ICapabilityCatalog? catalog, out Transport transport)
    {
        transport = InProcess;
        if (string.IsNullOrEmpty(toolName) || inputJson is null) return false;
        if (string.Equals(toolName, toolId, StringComparison.Ordinal)) return true;
        var mcpName = HostToolBridgeCatalog.McpName(toolId);
        if (toolName == CodexNativeLaneRunner.McpToolName)
        {
            if (ParseObject(inputJson) is not { } input
                || input["server"] is not JsonValue server || !server.TryGetValue<string>(out var serverName)
                || !string.Equals(serverName, McpServerLaunch.DefaultServerName, StringComparison.Ordinal)
                || input["tool"] is not JsonValue tool || !tool.TryGetValue<string>(out var bridgeTool)
                || !string.Equals(bridgeTool, mcpName, StringComparison.Ordinal)
                || input["arguments"] is not JsonObject) return false;
            transport = CodexMcp;
            return ServedUniquely(mcpName, toolId, catalog);
        }
        if (toolName.StartsWith(NativeToolSideEffects.ClaudeBridgePrefix, StringComparison.Ordinal))
        {
            if (!string.Equals(toolName[NativeToolSideEffects.ClaudeBridgePrefix.Length..], mcpName, StringComparison.Ordinal)
                || ParseObject(inputJson) is null) return false;
            transport = ClaudeBridge;
            return ServedUniquely(mcpName, toolId, catalog);
        }
        return false;
    }

    private static JsonObject? ParseObject(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }

    /// <summary>Extracts the bridge's own text from the stored result in the transport's encoding.</summary>
    public static bool TryReadResultText(string result, Transport transport, out string text)
    {
        text = result;
        if (transport.Encoding == ResultEncoding.Text) return true;
        JsonObject? wrapper;
        try { wrapper = JsonNode.Parse(result) as JsonObject; }
        catch (JsonException) { return false; }
        // The shell tool returns one text part and no images. The error flag is not part of the proof, but a
        // projection that carries one must carry it as a boolean (or an explicit null); anything else is not the bridge's result.
        if (wrapper?["content"] is not JsonArray { Count: 1 } content || content[0] is not JsonObject part
            || part["type"] is not JsonValue type || !type.TryGetValue<string>(out var kind) || kind != "text"
            || part["text"] is not JsonValue value || !value.TryGetValue(out text!)) return false;
        return !wrapper.TryGetPropertyValue("isError", out var flag) || flag is null || flag is JsonValue error && error.TryGetValue<bool>(out _);
    }

    /// <summary>
    /// The bridge can only have served a name its catalog mapped to exactly one implementation. With no catalog
    /// to consult, identity rests on the transport-reserved names alone; with one, the tool must be present and
    /// no other registered tool may normalize to the same MCP name (normalization is lossy).
    /// </summary>
    private static bool ServedUniquely(string mcpName, string toolId, ICapabilityCatalog? catalog)
    {
        if (catalog is null) return true;
        var present = false;
        foreach (var tool in catalog.All())
        {
            if (string.Equals(tool.ToolId, toolId, StringComparison.Ordinal)) { present = true; continue; }
            if (HostToolBridgeCatalog.NativeEquivalentToolIds.Contains(tool.ToolId)
                || tool.ToolId.StartsWith("mcp.", StringComparison.OrdinalIgnoreCase)) continue;
            if (HostToolBridgeCatalog.McpName(tool.ToolId) == mcpName) return false;
        }
        return present;
    }
}
