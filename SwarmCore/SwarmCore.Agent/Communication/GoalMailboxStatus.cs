using SwarmCore.Agent.Continual;

namespace SwarmCore.Agent.Communication;

/// <summary>Truthful, deterministic receipts for a saved message while execution is held.
/// These describe host state; they do not read or incorporate the user's instruction.</summary>
public static class GoalMailboxStatus
{
    public static string? Detail(ContinualObjectiveStatus status, DateTimeOffset? nextAttemptAtUtc) => status switch
    {
        ContinualObjectiveStatus.Paused => "Work is paused. Sending a message saves it without resuming work.",
        ContinualObjectiveStatus.WaitingForInput => "Work is waiting for input before it can continue.",
        ContinualObjectiveStatus.Uncertain => "An earlier action needs outcome inspection before work can continue. It must not be repeated just to obtain proof.",
        ContinualObjectiveStatus.DeliveryBlocked => "Delivery is blocked. The existing result and your message are retained.",
        ContinualObjectiveStatus.AttentionRequired when nextAttemptAtUtc is null => "Work needs attention before it can continue.",
        _ => null,
    };

    public static string Acknowledgment(string detail, string? blocker)
        => "Message saved. At this update, the agent had not read it. Recorded status: " + detail
            + (string.IsNullOrWhiteSpace(blocker) ? "" : " Recorded blocker: " + blocker)
            + " This is a host status receipt, not an agent reply.";

    public static bool IsAgentReply(GoalReply reply) => reply.Origin == GoalReplyOrigin.Agent;
}
