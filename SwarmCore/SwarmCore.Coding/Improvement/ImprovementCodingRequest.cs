using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Routing;

namespace SwarmCore.Coding.Improvement;

/// <summary>Bounded unattended source repair. The host independently verifies the resulting candidate.</summary>
public static class ImprovementCodingRequest
{
    public static CodingTurnRequest Create(string conversationId, string prompt, string repository, string? lane, int timeoutMinutes)
    {
        if (timeoutMinutes is < 1 or > 90) throw new ArgumentOutOfRangeException(nameof(timeoutMinutes));
        return new()
        {
            ConversationId = conversationId, UserMessage = prompt, LaneName = lane,
            Workspace = new() { RepositoryRoot = Path.GetFullPath(repository), AcceptanceChecks = [] },
            CommunicationScope = AgentCommunicationScope.None,
            UnattendedAgentTurn = true, AllowNativeLanes = false,
            ExecutionLimits = new() { Timeout = TimeSpan.FromMinutes(timeoutMinutes), MaxCumulativeTokens = 500000 }
        };
    }
}
