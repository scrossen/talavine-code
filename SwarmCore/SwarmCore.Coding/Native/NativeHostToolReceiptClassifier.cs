using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;

namespace SwarmCore.Coding.Native;

/// <summary>Classifies only this host's native MCP calls from its owning tool metadata.</summary>
public sealed class NativeHostToolReceiptClassifier(ICapabilityCatalog? catalog)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ToolSideEffect? Resolve(string name, JsonObject? input)
    {
        if (catalog is null || input is null) return null;
        string bridgeName;
        JsonObject arguments;
        if (name == CodexNativeLaneRunner.McpToolName)
        {
            if (input["server"] is not JsonValue server || !server.TryGetValue<string>(out var serverName)
                || serverName != McpServerLaunch.DefaultServerName
                || input["tool"] is not JsonValue tool || !tool.TryGetValue<string>(out var toolName)
                || input["arguments"] is not JsonObject supplied) return null;
            bridgeName = toolName;
            arguments = supplied;
        }
        else if (name.StartsWith(NativeToolSideEffects.ClaudeBridgePrefix, StringComparison.Ordinal))
        {
            bridgeName = name[NativeToolSideEffects.ClaudeBridgePrefix.Length..];
            arguments = input;
        }
        else return null;

        // MCP normalization is lossy. A collision cannot establish which implementation ran.
        var matches = catalog.All().Where(tool => !HostToolBridgeCatalog.NativeEquivalentToolIds.Contains(tool.ToolId)
            && !tool.ToolId.StartsWith("mcp.", StringComparison.OrdinalIgnoreCase)
            && HostToolBridgeCatalog.McpName(tool.ToolId) == bridgeName).Take(2).ToArray();
        if (matches.Length != 1) return null;
        try
        {
            var typed = arguments.Deserialize(matches[0].InputType, Json);
            return typed is null ? null : matches[0].EffectiveSideEffect(typed);
        }
        catch (Exception)
        {
            // Malformed arguments or a failed metadata resolver never prove a read-only call.
            return null;
        }
    }

    public TurnEvent Enrich(TurnEvent evt, IDictionary<string, ToolSideEffect?> effects)
    {
        if (evt is ToolCallStarted start)
        {
            var ownBridge = start.ToolName.StartsWith(NativeToolSideEffects.ClaudeBridgePrefix, StringComparison.Ordinal)
                || start.ToolName == CodexNativeLaneRunner.McpToolName
                    && start.Input["server"] is JsonValue server && server.TryGetValue<string>(out var serverName)
                    && serverName == McpServerLaunch.DefaultServerName;
            // Legacy parser tables cannot prove a bridge read: require current owning
            // metadata. An already identified physical effect is never relaxed.
            var effect = ownBridge && (start.SideEffect is null or ToolSideEffect.ReadOnly)
                ? Resolve(start.ToolName, start.Input) : start.SideEffect;
            effects[start.ToolCallId] = effect;
            return start with { SideEffect = effect };
        }
        if (evt is ToolCallCompleted end && (end.SideEffect is null or ToolSideEffect.ReadOnly)
            && effects.TryGetValue(end.ToolCallId, out var saved))
            return end with { SideEffect = saved };
        return evt;
    }
}
