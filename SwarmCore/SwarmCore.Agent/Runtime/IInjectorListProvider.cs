namespace SwarmCore.Agent.Runtime;

/// <summary>
/// Decides which <see cref="IContextInjector"/>s run for a given profile on
/// each turn. Hosts plug in their own implementation — typically reading
/// from profile metadata, or returning a static default set. Optional in
/// <see cref="IAgentTurnRunner"/>: when not registered, no injectors fire
/// and the system prompt comes through unchanged.
/// </summary>
public interface IInjectorListProvider
{
    IReadOnlyList<string> GetForProfile(Guid profileId);
}
