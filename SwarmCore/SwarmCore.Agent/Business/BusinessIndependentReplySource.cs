using System.Net.Mail;
using SwarmCore.Agent.BusinessDispatch;
using SwarmCore.Agent.Continual;

namespace SwarmCore.Agent.Business;

/// <summary>Host-owned thread/account scope. Never supplied as tool arguments by a model.</summary>
public sealed record BusinessLocalReplyScope(string PartitionKey, string AccountId, string OwnerMailbox,
    string TargetMailbox, IReadOnlyList<string> OutgoingMessageIds, DateTimeOffset CreatedAtUtc)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public BusinessHistoricalSimulatedArtifactReference? HistoricalSimulation { get; init; }
}

/// <summary>Actual source metadata from an owning store adapter. Snapshot/body hashes cover the current row.</summary>
public sealed record BusinessLocalReplyHeader(string PartitionKey, string AccountId, string SourceMessageId,
    string SourceReference, string SenderMailbox, IReadOnlyList<string> RecipientMailboxes,
    IReadOnlyList<string> ThreadMessageIds, BusinessEvidenceOrigin Origin, DateTimeOffset ObservedAtUtc,
    string SnapshotSha256, string BodySha256, bool IsDeleted = false, bool IsDraft = false,
    bool IsSimulatedOutbound = false, bool IsAutomatic = false, bool VerifiedDeliveryStatusReport = false,
    bool Archived = false, DateTimeOffset? ExplicitNotNowUntilUtc = null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public BusinessHistoricalSimulatedArtifactReference? HistoricalSimulation { get; init; }
}

public sealed record BusinessIndependentReplyObservation(string AccountId, string SourceMessageId, string SourceReference,
    string TargetMailbox, BusinessReplyIntent Intent, string Body, string SnapshotSha256,
    BusinessEvidenceOrigin Origin, bool Archived, DateTimeOffset ObservedAtUtc, DateTimeOffset? NotNowUntilUtc);
public sealed record BusinessIndependentReplyRead(bool Succeeded, BusinessIndependentReplyObservation? Observation, string? FailureCode = null);

/// <summary>
/// Read-only owning-store adapter. Implementations enforce the partition before reading,
/// derive origin from independently committed source receipts, and verify snapshot identity
/// when retrieving a body. A model answer or simulated Sent row is never an inbound source.
/// </summary>
public interface IBusinessLocalReplyStore
{
    Task<BusinessLocalReplyHeader?> ReadHeaderAsync(BusinessLocalReplyScope scope, string sourceMessageId, CancellationToken ct);
    Task<string?> ReadBodyAsync(BusinessLocalReplyScope scope, BusinessLocalReplyHeader expected, CancellationToken ct);
}

/// <summary>One exact independent source read, with current authority and row checks across every await. No send or domain writer.</summary>
public sealed class BusinessIndependentReplyReader(IBusinessLocalReplyStore store)
{
    public async Task<BusinessIndependentReplyRead> ReadAsync(BusinessLocalReplyScope scope, string sourceMessageId,
        Func<CancellationToken, Task<bool>> currentAuthority, DateTimeOffset cutoff, CancellationToken ct = default)
    {
        // Freeze every caller collection before the first external await.
        if (scope is null || scope.OutgoingMessageIds is null) return Failed("invalid-reply-scope");
        scope = scope with { OutgoingMessageIds = scope.OutgoingMessageIds.ToArray() };
        if (!ValidScope(scope) || !BusinessStageResultContract.Bounded(sourceMessageId, 200)) return Failed("invalid-reply-scope");
        if (!await currentAuthority(ct).ConfigureAwait(false)) return Failed("reply-authority-changed");
        var header = Freeze(await store.ReadHeaderAsync(scope, sourceMessageId, ct).ConfigureAwait(false));
        if (!await currentAuthority(ct).ConfigureAwait(false)) return Failed("reply-authority-changed");
        if (!ValidHeader(scope, header, sourceMessageId, cutoff)) return Failed("reply-source-unverified-or-out-of-scope");
        var body = await store.ReadBodyAsync(scope, header!, ct).ConfigureAwait(false);
        if (!await currentAuthority(ct).ConfigureAwait(false)) return Failed("reply-authority-changed");
        if (string.IsNullOrWhiteSpace(body) || body.Length > 10000
            || ContinualVerificationCandidatePolicy.Hash(body) != header!.BodySha256) return Failed("reply-body-drift");
        var current = Freeze(await store.ReadHeaderAsync(scope, sourceMessageId, ct).ConfigureAwait(false));
        if (!await currentAuthority(ct).ConfigureAwait(false)) return Failed("reply-authority-changed");
        if (!ValidHeader(scope, current, sourceMessageId, cutoff) || !SameHeader(header, current!)) return Failed("reply-source-drift");
        var firstLine = body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim().ToLowerInvariant();
        var intent = header.VerifiedDeliveryStatusReport ? BusinessReplyIntent.Bounce
            : firstLine is "unsubscribe" or "stop" or "remove me" ? BusinessReplyIntent.Unsubscribe
            : firstLine == "not now" && header.ExplicitNotNowUntilUtc is { } until && until > cutoff && until <= cutoff.AddYears(1)
                ? BusinessReplyIntent.NotNow : BusinessReplyIntent.Ambiguous;
        return new(true, new(header.AccountId, header.SourceMessageId, header.SourceReference, scope.TargetMailbox,
            intent, body, header.SnapshotSha256, header.Origin, header.Archived, header.ObservedAtUtc,
            intent == BusinessReplyIntent.NotNow ? header.ExplicitNotNowUntilUtc : null));
    }

    private static BusinessIndependentReplyRead Failed(string code) => new(false, null, code);
    private static BusinessLocalReplyHeader? Freeze(BusinessLocalReplyHeader? header)
        => header is null || header.RecipientMailboxes is null || header.ThreadMessageIds is null ? null
            : header with { RecipientMailboxes = header.RecipientMailboxes.ToArray(), ThreadMessageIds = header.ThreadMessageIds.ToArray() };
    private static bool ValidScope(BusinessLocalReplyScope scope) => BusinessStageResultContract.Bounded(scope.PartitionKey, 200)
        && BusinessStageResultContract.Bounded(scope.AccountId, 200) && Mailbox(scope.OwnerMailbox) && Mailbox(scope.TargetMailbox)
        && (scope.HistoricalSimulation is null
            ? scope.OutgoingMessageIds is { Count: > 0 and <= 6 } && scope.OutgoingMessageIds.All(MessageId)
                && scope.OutgoingMessageIds.Distinct(StringComparer.Ordinal).Count() == scope.OutgoingMessageIds.Count
            : scope.OutgoingMessageIds is { Count: 0 } && BusinessHistoricalSimulatedArtifactPolicy.Valid(scope.HistoricalSimulation, scope.CreatedAtUtc));
    private static bool ValidHeader(BusinessLocalReplyScope scope, BusinessLocalReplyHeader? header, string id, DateTimeOffset cutoff)
        => header is not null && header.PartitionKey == scope.PartitionKey && header.AccountId == scope.AccountId
        && header.SourceMessageId == id && BusinessStageResultContract.Bounded(header.SourceReference, 2000)
        && !header.IsDeleted && !header.IsDraft && !header.IsSimulatedOutbound
        && header.Origin is BusinessEvidenceOrigin.SeededWorldEvent or BusinessEvidenceOrigin.InjectedWorldEvent or BusinessEvidenceOrigin.ObservedInbound
        && header.ObservedAtUtc >= scope.CreatedAtUtc && header.ObservedAtUtc <= cutoff
        && ContinualSavedReportCorrectionPolicy.IsSha256(header.SnapshotSha256)
        && ContinualSavedReportCorrectionPolicy.IsSha256(header.BodySha256)
        && (header.VerifiedDeliveryStatusReport || Mailbox(header.SenderMailbox) && string.Equals(header.SenderMailbox, scope.TargetMailbox, StringComparison.OrdinalIgnoreCase))
        && (!header.IsAutomatic || header.VerifiedDeliveryStatusReport)
        && header.RecipientMailboxes is { Count: > 0 and <= 50 } && header.RecipientMailboxes.All(Mailbox)
        && header.RecipientMailboxes.Contains(scope.OwnerMailbox, StringComparer.OrdinalIgnoreCase)
        && (scope.HistoricalSimulation is null
            ? header.HistoricalSimulation is null && header.ThreadMessageIds is { Count: > 0 and <= 50 } && header.ThreadMessageIds.All(MessageId)
                && header.ThreadMessageIds.Any(reference => scope.OutgoingMessageIds.Contains(reference, StringComparer.Ordinal))
            : header.ThreadMessageIds is { Count: 0 } && header.HistoricalSimulation == scope.HistoricalSimulation
                && BusinessHistoricalSimulatedArtifactPolicy.Valid(header.HistoricalSimulation, header.ObservedAtUtc));
    private static bool SameHeader(BusinessLocalReplyHeader before, BusinessLocalReplyHeader after)
        => before with { RecipientMailboxes = Array.Empty<string>(), ThreadMessageIds = Array.Empty<string>() }
            == after with { RecipientMailboxes = Array.Empty<string>(), ThreadMessageIds = Array.Empty<string>() }
            && before.RecipientMailboxes.SequenceEqual(after.RecipientMailboxes, StringComparer.Ordinal)
            && before.ThreadMessageIds.SequenceEqual(after.ThreadMessageIds, StringComparer.Ordinal);
    private static bool MessageId(string value) => BusinessStageResultContract.Bounded(value, 320)
        && !value.Any(char.IsWhiteSpace) && !value.Contains('<') && !value.Contains('>');
    private static bool Mailbox(string value) => BusinessStageResultContract.Bounded(value, 255)
        && MailAddress.TryCreate(value, out var parsed) && parsed.Address == value;
}
