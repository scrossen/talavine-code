namespace SwarmCore.Agent.Continual;

/// <summary>Thrown only before an adapter starts its physical action. Safe to record as not dispatched.</summary>
public sealed class MissionActionSkippedException(string reasonCode)
    : SwarmCore.Agent.Catalog.ToolCallNotDispatchedException("SkipOutsideMission: " + reasonCode);
