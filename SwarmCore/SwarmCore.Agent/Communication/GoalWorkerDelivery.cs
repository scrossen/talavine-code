namespace SwarmCore.Agent.Communication;

/// <summary>Host-only delivery admission bound to the exact running worker lease. Never expose this lease to a model.</summary>
public interface IGoalWorkerDeliverySink
{
    Task<GoalDelivery?> BeginDeliveryForWorkerAsync(Guid goalId, string conversationId, string workerId,
        string expectedLeaseToken, int maxCharacters = 16000, CancellationToken ct = default);
}
