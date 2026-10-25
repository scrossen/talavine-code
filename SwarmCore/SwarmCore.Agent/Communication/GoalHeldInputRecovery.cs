namespace SwarmCore.Agent.Communication;

public sealed record GoalHeldInputBatch(Guid GoalId, IReadOnlyList<GoalInboxMessage> Messages, string ContextText);

/// <summary>Host-only inspection of previously claimed input. This is never permission to replay physical work.</summary>
public interface IGoalWorkerHeldInputReader
{
    Task<GoalHeldInputBatch?> GetHeldInputAsync(Guid goalId, string conversationId, string expectedLeaseToken,
        int maxCharacters = 16000, CancellationToken ct = default);
}
