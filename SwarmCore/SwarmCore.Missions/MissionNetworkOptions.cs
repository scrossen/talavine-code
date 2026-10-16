using System.Text.Json;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.Security;
using SwarmCore.Agent.Continual;
using SwarmCore.Mcp.Abstractions;
using SwarmCore.Mcp.Models;

namespace SwarmCore.Missions;

/// <summary>Host configuration, never accepted as a tool parameter. Contracts describe
/// effects; they do not grant authority. The authenticated assignment supplies grants.</summary>
public sealed class MissionNetworkOptions
{
    public List<MissionConnectorContract> Connectors { get; set; } = [];
    public List<MissionBrowserRoute> BrowserRoutes { get; set; } = [];
}
public sealed class MissionConnectorContract
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public Dictionary<string, MissionConnectorToolContract> Tools { get; set; } = new(StringComparer.Ordinal);
}
public sealed class MissionConnectorToolContract
{
    public MissionEffect Effects { get; set; }
    public List<MissionArgumentResource> Resources { get; set; } = [];
}
public sealed class MissionArgumentResource
{
    public string Argument { get; set; } = "";
    public MissionResourceKind Kind { get; set; }
    public MissionEffect Effects { get; set; }
}
public sealed class MissionBrowserRoute
{
    /// <summary>Optional logical resource modified by this API route (for example a cart page).
    /// Without it, the concrete request URL is the target. This mapping comes only from the host.</summary>
    public string? ResourceUrl { get; set; }
    public string Origin { get; set; } = "";
    public string Path { get; set; } = "";
    public string Method { get; set; } = "";
    public MissionEffect Effects { get; set; }
}

public sealed class MissionMcpBinding(IOptions<MissionNetworkOptions> options) : IMcpMissionBinding
{
    public IReadOnlyList<ExecutionResourceAction>? Resolve(McpConnectorOptions connector, string? tool,
        IReadOnlyDictionary<string, object?> arguments)
    {
        // A remote service is a host-bound trust endpoint. A local arbitrary server needs
        // a contained process, not this remote-service contract.
        if (!McpTransportTypes.IsHttp(connector.TransportType)) return null;
        var contract = options.Value.Connectors.SingleOrDefault(c => c.Name == connector.Name && c.Url == connector.Url);
        if (contract is null) return null;
        var endpoint = new Uri(contract.Url).GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
        if (tool is null) return [new("mcp.connect", "Endpoint", endpoint, nameof(MissionEffect.Inspect))];
        if (!contract.Tools.TryGetValue(tool, out var binding) || binding.Effects == MissionEffect.None) return null;
        var result = new List<ExecutionResourceAction> { new("mcp.call", "Endpoint", endpoint, binding.Effects.ToString()) };
        foreach (var resource in binding.Resources)
        {
            if (!arguments.TryGetValue(resource.Argument, out var raw)) return null;
            var value = raw is JsonElement json && json.ValueKind == JsonValueKind.String ? json.GetString() : raw as string;
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (resource.Kind is MissionResourceKind.File or MissionResourceKind.Directory)
                value = SandboxPathResolver.ResolvePhysicalPath(value);
            else if (resource.Kind == MissionResourceKind.Endpoint)
                value = new Uri(value).GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
            result.Add(new("mcp.argument", resource.Kind.ToString(), value, resource.Effects.ToString()));
        }
        return result;
    }
}
