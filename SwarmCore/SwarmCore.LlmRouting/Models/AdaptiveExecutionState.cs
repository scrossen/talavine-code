namespace SwarmCore.LlmRouting.Models;

/// <summary>Host-bound work identity. Never populated from model-authored tool arguments.</summary>
public sealed record AdaptiveWorkIdentity(Guid ProfileId, string ConversationId, Guid? MissionId)
{
    public string WorkId => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new { ProfileId, ConversationId, MissionId }))));
}

public sealed record AdaptiveWorkAuthority(string Digest, long? MissionVersion, string FunctionType);

public sealed record AdaptiveRouteState(string Provider, string Model, string? Effort)
{
    public string? SelectionAuditReference { get; init; }
    public decimal? CapabilityScore { get; init; }
    public decimal? MinimumCapabilityScore { get; init; }
    public bool IsUserPinned { get; init; }
    public string? OriginalRequestEffort { get; init; }
}

/// <summary>References existing measured router usage; this is not a reservation or spend ledger.</summary>
public sealed record AdaptiveUsageReference(string Reference, string Provider, string Model,
    long? InputTokens, long? OutputTokens, decimal? Cost, double? LatencyMs, bool Succeeded);

public enum AdaptiveDecisionOutcome { Proposed, Applied, Refused, Superseded }

public sealed record AdaptiveDecisionRecord
{
    public required Guid Id { get; init; }
    public required AdaptiveReasoningDecision Decision { get; init; }
    public required AdaptiveReasoningObservation Observation { get; init; }
    public required string AuthorityDigest { get; init; }
    public required DateTimeOffset AtUtc { get; init; }
    public AdaptiveRouteState? Before { get; init; }
    public AdaptiveRouteState? After { get; init; }
    public AdaptiveDecisionOutcome Outcome { get; init; }
    public string? OutcomeReason { get; init; }
    public string? EffectiveBoundary { get; init; }
    public decimal? EstimatedCost { get; init; }
    public IReadOnlyList<string> UsageReferences { get; init; } = [];
}

/// <summary>Protected device-local state plus bounded append-only decision history.</summary>
public sealed record AdaptiveExecutionSnapshot
{
    public int SchemaVersion { get; init; } = 1;
    public required AdaptiveWorkIdentity Identity { get; init; }
    public required AdaptiveWorkAuthority Authority { get; init; }
    public long Generation { get; init; }
    public required AdaptiveReasoningState PolicyState { get; init; }
    public AdaptiveRouteState? CurrentRoute { get; init; }
    public IReadOnlyList<AdaptiveDecisionRecord> Decisions { get; init; } = [];
    public IReadOnlyList<AdaptiveUsageReference> Usage { get; init; } = [];
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record AdaptiveExecutionLoad(AdaptiveExecutionSnapshot? Snapshot, long Generation,
    bool ReconstructionRequired = false);

public interface IAdaptiveExecutionStore
{
    Task<AdaptiveExecutionLoad> LoadAsync(AdaptiveWorkIdentity identity, CancellationToken ct = default);
    /// <summary>Compare generation and current mission version/profile/conversation in one writer transaction.
    /// Proposed snapshot.Generation must equal expectedGeneration + 1. Preserve prior decision records;
    /// bounded pruning removes only the oldest terminal records, never an unresolved proposal.</summary>
    Task<bool> TrySaveAsync(AdaptiveExecutionSnapshot snapshot, long expectedGeneration, CancellationToken ct = default);
    /// <summary>Delete protected contents and retain a generation tombstone against delayed publication.</summary>
    Task DeleteAsync(AdaptiveWorkIdentity identity, CancellationToken ct = default);
}
