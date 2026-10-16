namespace SwarmCore.Missions;

/// <summary>The persisted executor identity belongs to the embedding host. Keep it
/// stable across upgrades so pending objectives remain dispatchable after restart.</summary>
public sealed class MissionRuntimeOptions
{
    public SwarmCore.Agent.Continual.MissionDelegationLimits DelegationLimits { get; set; } = new();
    public string ExecutorKey { get; set; } = "swarmcore.mission.turn.v1";
}
