using SwarmCore.Agent.Catalog;

namespace SwarmCore.Agent.Runtime;

/// <summary>Host-owned standing authority, revalidated for the actual invocation. Null retains ordinary consent.</summary>
public interface IAgentToolDelegationAuthorizer
{
    Task<ToolDelegationDecision?> AuthorizeAsync(ToolDescriptor tool, object input, CancellationToken ct);
}
