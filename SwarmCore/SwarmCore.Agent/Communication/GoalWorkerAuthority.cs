namespace SwarmCore.Agent.Communication;

/// <summary>Host-derived narrowing from exact authenticated goal sources, never a model-authored summary.</summary>
public interface IGoalWorkerAuthority
{
    Task<bool> ToolsWithheldAsync(Guid goalId, string conversationId, string expectedLeaseToken, CancellationToken ct = default);
}
