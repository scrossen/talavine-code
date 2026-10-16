using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Agent.ToolUseEngine;

namespace SwarmCore.Coding.Routing;

/// <summary>The native finishing step only narrows tools already admitted by the host profile.</summary>
public sealed class NativeCommitToolFilter : IAgentTurnToolFilter
{
    public const string MetadataKey = "coding.native-commit-finishing.v1";
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
        { "git.commit", "git.status", "git.diff", "git.log" };
    public ValueTask<IReadOnlyList<ToolDescriptor>> FilterAsync(AgentTurnRequest request, AgentProfileSnapshot profile,
        IReadOnlyList<ToolDescriptor> tools, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<ToolDescriptor>>(request.Metadata?.ContainsKey(MetadataKey) == true
            ? tools.Where(tool => Allowed.Contains(tool.ToolId)).ToList() : tools);
}
