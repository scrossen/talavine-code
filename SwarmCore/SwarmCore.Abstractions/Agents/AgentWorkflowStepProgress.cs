namespace SwarmCore.Abstractions.Agents;

/// <summary>An agent's explicit progress report, not a verification receipt or permission grant.</summary>
public sealed record AgentWorkflowStepProgress(Guid RunId, string Version, string StepId, string State, DateTime RecordedUtc);
