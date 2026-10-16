namespace SwarmCore.Abstractions.Simulation;

/// <summary>Read-only reconciliation of an exact local operation after uncertain acknowledgement.</summary>
public interface IAgentSimulationReceiptLookup
{
    Task<SimulationDispatchReceipt?> FindAsync(SimulationDispatchRequest request, CancellationToken cancellationToken);
}
