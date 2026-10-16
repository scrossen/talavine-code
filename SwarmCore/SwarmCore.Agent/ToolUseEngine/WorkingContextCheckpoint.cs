using SwarmCore.Abstractions.Context;
using SwarmCore.Agent.Continual;

namespace SwarmCore.Agent.ToolUseEngine;

/// <summary>Host-bound identity. A conversation cannot reuse another profile or mission's state.</summary>
public sealed record WorkingContextIdentity(Guid ProfileId, string ConversationId, Guid? MissionId);

/// <summary>Host-observed authority, never supplied by a summarizing model.</summary>
public sealed record WorkingContextAuthority
{
    public required string Objective { get; init; }
    public required string Digest { get; init; }
    public int SteeringRevision { get; init; }
    public long? MissionStateVersion { get; init; }
    public IReadOnlyList<string> Constraints { get; init; } = [];
    public IReadOnlyList<string> AcceptanceCriteria { get; init; } = [];
    public IReadOnlyList<string> CompletedReceiptIds { get; init; } = [];
    public IReadOnlyList<string> UnresolvedIssues { get; init; } = [];
    public IReadOnlyList<string> PendingCallIds { get; init; } = [];
    public string? BudgetReference { get; init; }
    public IReadOnlySet<Guid>? AllowedSourceAccountIds { get; init; }
}

/// <summary>Opaque protocol data has its own lifetime and exact route binding.</summary>
public sealed record WorkingContextProviderState
{
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public required string Mode { get; init; }
    public string? Endpoint { get; init; }
    public string? CredentialReference { get; init; }
    public required string RequestLineage { get; init; }
    public string? ResponseId { get; init; }
    public string? CanonicalInputJson { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }
    public bool ServerStored { get; init; }
    public bool ConnectionLocal { get; init; }
}

/// <summary>Complete protected provider projection, separate from the unchanged durable transcript.</summary>
public sealed record WorkingContextCheckpoint
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required WorkingContextIdentity Identity { get; init; }
    public long Generation { get; init; }
    public required WorkingContextAuthority Authority { get; init; }
    public required WorkingContextSnapshot Snapshot { get; init; }
    public required string ProjectionJson { get; init; }
    public string? NeutralProjectionJson { get; init; }
    public int DurableMessageCount { get; init; }
    public required string DurablePrefixDigest { get; init; }
    public int CoveredMessageCount { get; init; }
    public string? CoveredMessagesDigest { get; init; }
    public WorkingContextProviderState? ProviderState { get; init; }
    public IReadOnlyList<ContextPack> SourcePacks { get; init; } = [];
    public IReadOnlyList<MissionObservedState> ObservedStates { get; init; } = [];
    public bool SourceProofComplete { get; init; }
    public required string RetentionPolicyVersion { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset ExpiresAtUtc { get; init; }
}

public enum WorkingContextLoadStatus { Missing, Available, ReconstructionRequired }
public sealed record WorkingContextLoadResult(WorkingContextLoadStatus Status,
    WorkingContextCheckpoint? Checkpoint = null, string? Reason = null, long Generation = 0);

/// <summary>Expected-generation CAS rejects stale compaction/result/steering writers. Delete retains a
/// generation tombstone while removing private payload, so a late writer cannot resurrect deleted state.</summary>
public interface IWorkingContextCheckpointStore
{
    Task<WorkingContextLoadResult> LoadAsync(WorkingContextIdentity identity, CancellationToken ct = default);
    Task<bool> TrySaveAsync(WorkingContextCheckpoint checkpoint, long expectedGeneration, CancellationToken ct = default);
    Task DeleteAsync(WorkingContextIdentity identity, CancellationToken ct = default);
}

public sealed record WorkingContextValidationResult(bool Valid, string? Reason = null);
public sealed record WorkingContextLegacyResult(IReadOnlyList<SwarmCore.Abstractions.LlmRouting.ChatMessage> Messages,
    IReadOnlyList<ContextPack> SourcePacks, bool SourceProofComplete, string? Reason = null);
public interface IWorkingContextSourceValidator
{
    Task<WorkingContextLegacyResult> RevalidateLegacyAsync(WorkingContextIdentity identity, WorkingContextAuthority authority,
        IReadOnlyList<SwarmCore.Abstractions.LlmRouting.ChatMessage> transcript, CancellationToken ct = default)
        => Task.FromResult(new WorkingContextLegacyResult([], [], false, "Legacy provenance validator is unavailable."));
    Task<WorkingContextValidationResult> ValidateAsync(WorkingContextCheckpoint checkpoint,
        WorkingContextAuthority currentAuthority, CancellationToken ct = default);
}

/// <summary>Local retention limits are host policy, not a claim about provider-side expiration.</summary>
public sealed record WorkingContextRetentionPolicy
{
    public string Version { get; init; } = "working-context-v1";
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromDays(7);
    public int MaximumPayloadCharacters { get; init; } = 4_000_000;
    public bool PersistProviderState { get; init; } = true;
}

public sealed record WorkingContextCompactionOptions
{
    public int OutputReserveTokens { get; init; } = 8192;
    public int ReasoningReserveTokens { get; init; } = 8192;
    public int ToolResultReserveTokens { get; init; } = 8192;
    public double TriggerFraction { get; init; } = 0.85;
    public bool PreferNativeCompaction { get; init; } = true;

    /// <summary>
    /// Largest share of a route's context window the reserves above may claim.
    /// Small runtime windows (a local runner started with 32K tokens) cannot
    /// honor the full reserves: 24K of reserve against a 32K window would leave
    /// almost no room for the prompt and put every request under pressure.
    /// </summary>
    public double MaximumReserveFraction { get; init; } = 0.25;
}
