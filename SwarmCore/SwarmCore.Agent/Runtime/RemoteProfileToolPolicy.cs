using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCore.Agent.Catalog;

namespace SwarmCore.Agent.Runtime;

/// <summary>How far an explicitly connected client reaches into the host's catalog.</summary>
public enum RemoteToolReach
{
    /// <summary>A profile client connected through a relay (ChatGPT, Dot): only tools the host reviewed for it.</summary>
    ConnectedProfile,

    /// <summary>
    /// An agent the person connected on this same computer (SwarmCode): every catalog tool the host does not
    /// exclude, except code/command execution, through the same guards, scheduler and receipts as the assistant.
    /// </summary>
    LocalAppControl,
}

/// <summary>Common discovery and typed-input rules for an explicitly connected profile client.</summary>
public static class RemoteProfileToolPolicy
{
    public static bool IsAvailable(ToolDescriptor tool) => tool.RemoteProfileAvailable && tool.SideEffect != ToolSideEffect.Exec;

    public static bool IsAvailable(ToolDescriptor tool, RemoteToolReach reach) => reach switch
    {
        RemoteToolReach.LocalAppControl => tool.SideEffect != ToolSideEffect.Exec,
        _ => IsAvailable(tool),
    };

    public static object Describe(ToolDescriptor tool) => new
    {
        id = tool.ToolId, tool.Description, inputSchema = tool.InputSchema,
        readOnly = tool.SideEffect == ToolSideEffect.ReadOnly, sideEffect = tool.SideEffect.ToString(),
        dynamicSideEffect = tool.ResolveSideEffect is not null
    };

    public static object ReadInput(ToolDescriptor tool, JsonObject input) => ReadInput(tool, input, RemoteToolReach.ConnectedProfile);

    public static object ReadInput(ToolDescriptor tool, JsonObject input, RemoteToolReach reach)
    {
        if (!IsAvailable(tool, reach)) throw new InvalidOperationException("This operation is not available to the connected profile client.");
        return input.Deserialize(tool.InputType, InputJson) ?? throw new InvalidOperationException("Operation input is required.");
    }

    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}
