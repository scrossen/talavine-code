namespace SwarmCore.Agent.Runtime;

/// <summary>Host-verified standing authority for one concrete invocation; never supplied by a model.</summary>
public sealed record ToolDelegationDecision(bool Allowed, string Authority, string Reason);
