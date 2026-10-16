namespace SwarmCore.Agent.Marketing;

public enum InquiryDeliveryState { Pending, Dispatching, Uncertain, Sent, Stopped }
public enum InquiryNextAction { Wait, Send, Reconcile, Stop, Done }

/// <summary>Host-neutral two-message inquiry policy. A transport ambiguity never permits resending.</summary>
public static class InquiryFollowUpPolicy
{
    public static InquiryNextAction Decide(InquiryDeliveryState state, bool suppressed, bool replied,
        bool taskClosed, bool authorized, bool mailboxObserved, bool previousSent, DateTime dueUtc, DateTime nowUtc)
    {
        if (state is InquiryDeliveryState.Sent or InquiryDeliveryState.Stopped) return InquiryNextAction.Done;
        if (state is InquiryDeliveryState.Dispatching or InquiryDeliveryState.Uncertain) return InquiryNextAction.Reconcile;
        if (suppressed || replied || taskClosed) return InquiryNextAction.Stop;
        if (!authorized || !mailboxObserved || !previousSent || dueUtc > nowUtc) return InquiryNextAction.Wait;
        return InquiryNextAction.Send;
    }

    public static void ValidateText(string subject, string body, string? followUp)
    {
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 200 || subject.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(body) || body.Length > 4000 || followUp?.Length > 4000)
            throw new ArgumentException("Provide a subject (200 characters) and approved plain text (4000 characters per message).");
    }

    /// <summary>Receipt-copy failure after transport acceptance must never change a send into a retryable failure.</summary>
    public static async Task SendOnceThenRecordCopyAsync(Func<Task> dispatchOnce, Func<Task> recordCopy)
    {
        await dispatchOnce().ConfigureAwait(false);
        try { await recordCopy().ConfigureAwait(false); }
        catch { /* The transport already accepted. Preserve that fact even if the Sent copy is unavailable. */ }
    }
}

public sealed record InquiryMailIdentity(Guid ConnectionId, string OwnerUserId, string Sender);
public sealed record InquiryMailObservation(bool Ready, bool HasReply, string? Detail = null);
public sealed record InquiryMailAttachment(SwarmCore.Agent.BusinessDispatch.BusinessMessageMedia Manifest, byte[] Bytes);
public sealed record InquiryMailEnvelope(string Recipient, string Subject, string Body, string MessageId, string? InReplyTo,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<InquiryMailAttachment>? Attachments = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    bool HtmlBody = false);

/// <summary>No arbitrary mailbox selection, guessed recipients, automatic retry, or shared sender fallback.</summary>
public interface IInquiryPersonalMailbox
{
    Task<bool> IsConfiguredAsync(InquiryMailIdentity identity, CancellationToken ct);
    Task<InquiryMailObservation> ObserveAsync(InquiryMailIdentity identity, string recipient, DateTime inquiryUtc,
        IReadOnlyList<string> sentMessageIds, CancellationToken ct);
    Task<bool> FindSentAsync(InquiryMailIdentity identity, InquiryMailEnvelope envelope, CancellationToken ct);
    Task SendOnceAsync(InquiryMailIdentity identity, InquiryMailEnvelope envelope, CancellationToken ct);
}
