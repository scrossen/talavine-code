using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Missions;

namespace SwarmCore.Coding.Experiments;

/// <summary>
/// Host-owned experiment write policy, composed with ordinary permission/recovery guards. Only declared
/// artifact paths may be written through declared file tools. The sandbox resolver owns physical containment.
/// This policy never grants authority or admits shell/network tools into an experiment.
/// </summary>
public sealed class HarnessArtifactWriteGuard(ISandboxPathResolver paths,
    IReadOnlyCollection<string> artifacts, IReadOnlyDictionary<string, string> toolPathArguments) : IToolInvocationGuard
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string[] _artifacts = artifacts.Select(paths.Resolve).ToArray();
    private readonly IReadOnlyDictionary<string, string> _toolPathArguments = new Dictionary<string, string>(toolPathArguments);

    public Task<ToolGuardDecision> InspectAsync(ToolInvocationRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (request.EffectiveSideEffect == ToolSideEffect.ReadOnly) return Task.FromResult(ToolGuardDecision.Allow());
        if (request.EffectiveSideEffect != ToolSideEffect.WritesLocal || !_toolPathArguments.TryGetValue(request.ToolId, out var argument))
            return Block();
        try
        {
            var node = JsonSerializer.SerializeToNode(request.Input, Json) as JsonObject;
            var name = node?.FirstOrDefault(pair => string.Equals(pair.Key, argument, StringComparison.OrdinalIgnoreCase)).Value?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) return Block();
            var target = paths.Resolve(name);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (_artifacts.Any(artifact => string.Equals(target, artifact, comparison)))
                return Task.FromResult(ToolGuardDecision.Allow());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException
            or UnauthorizedAccessException or JsonException or NotSupportedException) { }
        return Block();
    }

    private static Task<ToolGuardDecision> Block() => Task.FromResult(ToolGuardDecision.Block(
        "Experiment inputs and evaluators are read-only. Write only the host-declared output artifact using an admitted file tool."));
}
