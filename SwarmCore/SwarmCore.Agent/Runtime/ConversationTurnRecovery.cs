namespace SwarmCore.Agent.Runtime;

public static class ConversationTurnRecovery
{
    /// <summary>
    /// Guidance for an existing durable objective, not a replacement user request
    /// or an exactly-once guarantee. The host must still retain its objective lease.
    /// </summary>
    public const string ReceiptReconciliationInstruction =
        "Inspect the saved tool receipts and existing transcript before taking further action. "
        + "Do not repeat actions whose successful receipts already establish completion. "
        + "A started, interrupted, timed-out, or failed tool call may still have changed external state. "
        + "Reconcile that outcome with a read-only inspection before considering another attempt; "
        + "if the outcome cannot be established, preserve the partial results and explain the exact verification needed instead of repeating the action. "
        + "Continue only the remaining work. If the work is complete and only the answer is missing, finish the answer from the saved receipts.";

    /// <summary>
    /// A visible completed continuation can replace an earlier attempt's transient
    /// error. Never infer recovery from another user request or an unfinished row.
    /// This describes presentation recovery, not completion of the whole objective.
    /// </summary>
    public static bool HasVisibleContinuation(IReadOnlyList<ChatTurnInfo> turns, Guid failedTurnId)
    {
        var failed = turns.FirstOrDefault(turn => turn.Id == failedTurnId);
        if (failed is null) return false;
        foreach (var turn in turns.Where(turn => turn.ConversationId == failed.ConversationId
                                                 && turn.Sequence > failed.Sequence)
                                  .OrderBy(turn => turn.Sequence))
        {
            if (!turn.IsContinuation) return false;
            if (turn.CompletedUtc is not null && !string.IsNullOrWhiteSpace(turn.AssistantText)) return true;
        }
        return false;
    }
}
