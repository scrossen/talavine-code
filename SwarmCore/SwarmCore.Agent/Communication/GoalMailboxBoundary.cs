namespace SwarmCore.Agent.Communication;

/// <summary>Boundary adapters share exact recipient validation; mailbox errors never cancel ordinary goal work.</summary>
public static class GoalMailboxBoundary
{
    public static async Task<GoalDelivery?> BeginAsync(IGoalMailbox? mailbox, Guid? goalId,
        string conversationId, string workerId, int maxCharacters = 16000, CancellationToken ct = default,
        string? expectedLeaseToken = null)
    {
        if (mailbox is null || goalId is null) return null;
        // A resumed worker must not lend its live lease to a previous worker with the same conversation id.
        // The private lease is host state, never delivery text or native process configuration.
        var delivery = expectedLeaseToken is not null
            ? await (mailbox is IGoalWorkerDeliverySink bound && !string.IsNullOrWhiteSpace(expectedLeaseToken)
                ? bound.BeginDeliveryForWorkerAsync(goalId.Value, conversationId, workerId, expectedLeaseToken, maxCharacters, ct)
                : throw new UnauthorizedAccessException("This mailbox cannot admit the exact goal worker lease.")).ConfigureAwait(false)
            : await mailbox.BeginDeliveryAsync(goalId.Value, conversationId, workerId, maxCharacters, ct).ConfigureAwait(false);
        if (delivery is null) return null;
        if (delivery.GoalId != goalId || delivery.ConversationId != conversationId || delivery.WorkerId != workerId
            || string.IsNullOrWhiteSpace(delivery.Id) || delivery.Messages.Count == 0
            || delivery.Messages.Select(message => message.Id).Distinct(StringComparer.Ordinal).Count() != delivery.Messages.Count
            || delivery.Messages.Any(message => message.GoalId != goalId || message.DeliveryId != delivery.Id
                || message.State != GoalDeliveryState.Presenting || string.IsNullOrWhiteSpace(message.Id)
                || string.IsNullOrWhiteSpace(message.Text) || message.Text.Length > GoalMessaging.MaximumMessageCharacters
                || !GoalMessaging.AreValidAttachments(message.Attachments)
                || message.AttachmentIssue is not null && (message.Attachments is not { Count: > 0 }
                    || string.IsNullOrWhiteSpace(message.AttachmentIssue) || message.AttachmentIssue.Length > 2000))
            || string.IsNullOrWhiteSpace(delivery.ContextText) || delivery.ContextText.Length > maxCharacters
            || !GoalMessaging.IsValidChronology(delivery.Messages, delivery.Chronology)
            || delivery.ContextText != GoalMessaging.FormatDelivery(delivery.Id, delivery.Messages, delivery.Chronology))
            throw new InvalidDataException("The goal inbox returned an unbound or incomplete delivery.");
        return delivery;
    }

    public static async Task TryRecordAsync(IGoalMailbox mailbox, GoalDelivery delivery,
        GoalDeliveryState state, string detail, CancellationToken ct = default)
    {
        try { await mailbox.RecordDeliveryAsync(delivery.Id, state, detail, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* The durable Presenting state remains uncertain. Never claim Read or replay the batch. */ }
    }
}
