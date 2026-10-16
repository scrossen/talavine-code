using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCore.Abstractions.Context;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.Agent.ToolUseEngine;

/// <summary>Per-turn mutable collection, owned by the single engine. Host authority is refreshed
/// before publication; CAS makes losing compaction attempts discardable.</summary>
public sealed class WorkingContextState(WorkingContextIdentity identity, WorkingContextAuthority authority)
{
    public WorkingContextIdentity Identity { get; } = identity;
    public WorkingContextAuthority Authority { get; set; } = authority;
    public List<ContextPack> SourcePacks { get; } = [];
    public IReadOnlyList<SwarmCore.Agent.Continual.MissionObservedState> ObservedStates { get; set; } = [];
    public Func<CancellationToken, Task>? PersistDurableBoundaryAsync { get; set; }
    public List<string> UnprovenSourceCallIds { get; } = [];
    public Func<IReadOnlyList<string>, CancellationToken, Task<bool>>? ValidateSourceCallProofsAsync { get; init; }
    public long Generation { get; set; }
    public WorkingContextProviderState? ProviderState { get; set; }
    public IReadOnlyList<ChatMessage>? NeutralProjection { get; set; }
    public int CommittedPrefixCount { get; set; }
    public int ResumeAfterMessageCount { get; set; }
    public bool SourceProofComplete { get; set; } = true;
    public Func<CancellationToken, Task<WorkingContextAuthority>>? RefreshAuthorityAsync { get; init; }
    public Func<CancellationToken, Task<IReadOnlyList<SwarmCore.Agent.Continual.MissionObservedState>>>? RefreshObservedStatesAsync { get; init; }
    public WorkingContextCheckpoint? Restored { get; set; }
    public string? ReconstructionReason { get; set; }
    public bool LastPublicationWasTurnOnly { get; set; }
}

public static class WorkingContextCodec
{
    public static string Serialize(IReadOnlyList<ChatMessage> messages)
        => new JsonArray(messages.Select(m => (JsonNode)new JsonObject
        { ["role"] = ChatTranscriptJson.RoleToString(m.Role), ["parts"] = JsonNode.Parse(ChatTranscriptJson.Serialize(m.Parts)) }).ToArray()).ToJsonString();

    public static List<ChatMessage> Deserialize(string json)
    {
        if (JsonNode.Parse(json) is not JsonArray array) throw new InvalidDataException("Invalid working-context projection.");
        return array.Select(n => n is JsonObject message && message["parts"] is JsonArray parts
            && (string?)message["role"] is "system" or "user" or "assistant" or "tool"
                ? new ChatMessage(ChatTranscriptJson.RoleFromString((string)message["role"]!), ChatTranscriptJson.Deserialize(parts.ToJsonString()))
                : throw new InvalidDataException("Invalid working-context message.")).ToList();
    }

    public static string Digest(IReadOnlyList<ChatMessage> messages)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(messages.Select(m => new ChatMessage(m.Role,
            m.Parts.Where(p => p is not ResponsesReasoningPart and not ResponsesCompactionPart and not ResponsesOutputPart and not ResponsesConfigurationUpdatePart).ToArray())).ToArray()))));

    /// <summary>Move a boundary earlier until every call/result crossing it is retained together.
    /// An unresolved call is always retained; no arbitrary truncation repairs invalid protocol.</summary>
    public static int SafeBoundary(IReadOnlyList<ChatMessage> messages, int proposed)
    {
        var boundary = Math.Clamp(proposed, 0, messages.Count);
        var calls = new Dictionary<string, int>(StringComparer.Ordinal);
        var results = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < messages.Count; i++)
            foreach (var part in messages[i].Parts)
            {
                if (part is ToolUsePart call && !calls.TryAdd(call.Id, i)) return 0;
                if (part is ToolResultPart result && !results.TryAdd(result.ToolUseId, i)) return 0;
            }
        foreach (var call in calls.OrderByDescending(pair => pair.Value))
            if (call.Value < boundary && (!results.TryGetValue(call.Key, out var result) || result >= boundary)) boundary = call.Value;
        foreach (var result in results)
            if (!calls.TryGetValue(result.Key, out var call) || call > result.Value) return 0;
        return boundary;
    }
}

public sealed class WorkingContextCheckpointManager(IWorkingContextCheckpointStore store,
    IWorkingContextSourceValidator validator, WorkingContextRetentionPolicy? retention = null)
{
    public WorkingContextRetentionPolicy Retention { get; } = retention ?? new();

    public async Task<WorkingContextLoadResult> RestoreAsync(WorkingContextState state, IReadOnlyList<ChatMessage> durable,
        CancellationToken ct)
    {
        var loaded = await store.LoadAsync(state.Identity, ct).ConfigureAwait(false);
        state.Generation = loaded.Generation;
        if (loaded.Status == WorkingContextLoadStatus.ReconstructionRequired)
        {
            await store.DeleteAsync(state.Identity, ct).ConfigureAwait(false);
            state.Generation = (await store.LoadAsync(state.Identity, ct).ConfigureAwait(false)).Generation;
            state.ReconstructionReason = loaded.Reason;
            return loaded with { Generation = state.Generation };
        }
        if (loaded.Status != WorkingContextLoadStatus.Available || loaded.Checkpoint is not { } checkpoint) return loaded;
        string? reason = checkpoint.SchemaVersion != WorkingContextCheckpoint.CurrentSchemaVersion ? "checkpoint-schema-changed"
            : checkpoint.Identity != state.Identity ? "checkpoint-identity-changed"
            : checkpoint.RetentionPolicyVersion != Retention.Version || checkpoint.ExpiresAtUtc <= DateTimeOffset.UtcNow ? "checkpoint-retention-expired"
            : checkpoint.Authority.Digest != state.Authority.Digest ? "checkpoint-authority-changed"
            : checkpoint.DurableMessageCount > durable.Count || checkpoint.DurableMessageCount < 0
                || checkpoint.DurablePrefixDigest != WorkingContextCodec.Digest(durable.Take(checkpoint.DurableMessageCount).ToArray()) ? "checkpoint-transcript-prefix-changed" : null;
        if (reason is null)
        {
            try
            {
                _ = WorkingContextCodec.Deserialize(checkpoint.ProjectionJson);
                if (checkpoint.NeutralProjectionJson is { } neutral) _ = WorkingContextCodec.Deserialize(neutral);
                var validation = await validator.ValidateAsync(checkpoint, state.Authority, ct).ConfigureAwait(false);
                if (!validation.Valid) reason = validation.Reason ?? "checkpoint-source-revalidation-failed";
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException)
            { reason = "checkpoint-corrupt"; }
        }
        if (reason is not null)
        {
            state.ReconstructionReason = reason;
            await store.DeleteAsync(state.Identity, ct).ConfigureAwait(false);
            state.Generation = (await store.LoadAsync(state.Identity, ct).ConfigureAwait(false)).Generation;
            return new(WorkingContextLoadStatus.ReconstructionRequired, Reason: reason, Generation: state.Generation);
        }
        state.ResumeAfterMessageCount = checkpoint.DurableMessageCount;
        if (checkpoint.CoveredMessageCount > checkpoint.DurableMessageCount)
        {
            if (durable.Count >= checkpoint.CoveredMessageCount && WorkingContextCodec.Digest(durable.Take(checkpoint.CoveredMessageCount).ToArray()) == checkpoint.CoveredMessagesDigest)
                state.ResumeAfterMessageCount = checkpoint.CoveredMessageCount;
            else if (durable.Count != checkpoint.DurableMessageCount)
            {
                reason = "checkpoint-covered-tail-changed";
            }
        }
        // A checkpoint certifies only its captured projection. Later durable messages can
        // contain generated source content whose provenance was never part of this checkpoint.
        if (durable.Count > state.ResumeAfterMessageCount) reason ??= "checkpoint-unvalidated-transcript-tail";
        if (reason is not null)
        {
            state.ReconstructionReason = reason;
            await store.DeleteAsync(state.Identity, ct).ConfigureAwait(false);
            state.Generation = (await store.LoadAsync(state.Identity, ct).ConfigureAwait(false)).Generation;
            return new(WorkingContextLoadStatus.ReconstructionRequired, Reason: reason, Generation: state.Generation);
        }
        if (!Retention.PersistProviderState && checkpoint.NeutralProjectionJson is { } retentionNeutral)
        {
            checkpoint = checkpoint with { ProjectionJson = retentionNeutral, ProviderState = null };
            loaded = loaded with { Checkpoint = checkpoint, Reason = "provider-state-retention-disabled" };
        }
        state.Restored = checkpoint;
        state.ProviderState = checkpoint.ProviderState;
        if (checkpoint.NeutralProjectionJson is { } neutralJson)
        {
            var neutral = WorkingContextCodec.Deserialize(neutralJson);
            var projection = WorkingContextCodec.Deserialize(checkpoint.ProjectionJson);
            var canonicalIndex = projection.FindIndex(m => m.Parts.Any(p => p is ResponsesCompactionPart));
            state.NeutralProjection = canonicalIndex < 0 ? neutral : neutral.Take(Math.Max(0, neutral.Count - (projection.Count - canonicalIndex - 1))).ToArray();
        }
        state.SourcePacks.AddRange(checkpoint.SourcePacks);
        return loaded;
    }

    public async Task<IReadOnlyList<ChatMessage>?> ReconstructProviderAsync(WorkingContextState state, IReadOnlyList<ChatMessage> current, CancellationToken ct)
    {
        if (state.Restored is not { } checkpoint) return null;
        var authority = state.RefreshAuthorityAsync is { } refresh ? await refresh(ct).ConfigureAwait(false) : state.Authority;
        if (authority.Digest != state.Authority.Digest) return null;
        var sourceProofComplete = state.SourceProofComplete && (state.UnprovenSourceCallIds.Count == 0
            || state.ValidateSourceCallProofsAsync is { } validateProofs && await validateProofs(state.UnprovenSourceCallIds, ct).ConfigureAwait(false));
        var validation = await validator.ValidateAsync(checkpoint with { SourcePacks = state.SourcePacks.ToArray(), ObservedStates = state.ObservedStates, SourceProofComplete = sourceProofComplete }, authority, ct).ConfigureAwait(false);
        if (!validation.Valid) return null;
        var canonical = current.ToList().FindIndex(m => m.Parts.Any(p => p is ResponsesCompactionPart));
        IReadOnlyList<ChatMessage> result = canonical >= 0 && state.NeutralProjection is { } neutral
            ? [..neutral, ..current.Skip(canonical + 1)] : current;
        state.ProviderState = null;
        return result.Select(m => new ChatMessage(m.Role, m.Parts.Where(p => p is not ResponsesCompactionPart and not ResponsesReasoningPart and not ResponsesOutputPart and not ResponsesConfigurationUpdatePart).ToArray()))
            .Where(m => m.Parts.Count > 0).ToArray();
    }

    public Task<WorkingContextLegacyResult> RevalidateLegacyAsync(WorkingContextState state, IReadOnlyList<ChatMessage> transcript, CancellationToken ct)
        => validator.RevalidateLegacyAsync(state.Identity, state.Authority, transcript, ct);

    public async Task<bool> PublishAsync(WorkingContextState state, WorkingContextCompactionResult result,
        IReadOnlyList<ChatMessage> durable, CancellationToken ct, bool isCompaction = true)
    {
        state.LastPublicationWasTurnOnly = false;
        if (!isCompaction && state.PersistDurableBoundaryAsync is { } persist)
            await persist(ct).ConfigureAwait(false);
        var authority = state.RefreshAuthorityAsync is null ? state.Authority
            : await state.RefreshAuthorityAsync(ct).ConfigureAwait(false);
        if (authority.Digest != state.Authority.Digest) return false;
        if (state.RefreshObservedStatesAsync is { } refreshEvidence)
            state.ObservedStates = await refreshEvidence(ct).ConfigureAwait(false);
        var projection = result.Messages;
        var canonicalIndex = projection.ToList().FindIndex(m => m.Parts.Any(p => p is ResponsesCompactionPart));
        IReadOnlyList<ChatMessage> neutralProjection = canonicalIndex >= 0 && state.NeutralProjection is { } previousNeutral
            ? [..previousNeutral, ..projection.Skip(canonicalIndex + 1)]
            : projection.Select(m => new ChatMessage(m.Role, m.Parts.Where(p => p is not ResponsesReasoningPart and not ResponsesCompactionPart and not ResponsesOutputPart and not ResponsesConfigurationUpdatePart).ToArray())).Where(m => m.Parts.Count > 0).ToArray();
        neutralProjection = neutralProjection.Select(m => new ChatMessage(m.Role,
            m.Parts.Where(p => p is not ResponsesReasoningPart and not ResponsesCompactionPart and not ResponsesOutputPart and not ResponsesConfigurationUpdatePart).ToArray()))
            .Where(m => m.Parts.Count > 0).ToArray();
        if (!Retention.PersistProviderState) projection = neutralProjection;
        var json = WorkingContextCodec.Serialize(projection);
        if (json.Length > Retention.MaximumPayloadCharacters || isCompaction && result.CompactedCharacters >= result.OriginalCharacters) return false;
        var sourceProofComplete = state.SourceProofComplete && (state.UnprovenSourceCallIds.Count == 0
            || state.ValidateSourceCallProofsAsync is { } validateProofs && await validateProofs(state.UnprovenSourceCallIds, ct).ConfigureAwait(false));
        var now = DateTimeOffset.UtcNow;
        var checkpoint = new WorkingContextCheckpoint
        {
            Identity = state.Identity, Generation = state.Generation + 1, Authority = authority, Snapshot = result.Snapshot,
            ProjectionJson = json, NeutralProjectionJson = WorkingContextCodec.Serialize(neutralProjection),
            ProviderState = Retention.PersistProviderState ? state.ProviderState : null, DurableMessageCount = state.CommittedPrefixCount,
            DurablePrefixDigest = WorkingContextCodec.Digest(durable.Take(state.CommittedPrefixCount).ToArray()),
            CoveredMessageCount = durable.Count, CoveredMessagesDigest = WorkingContextCodec.Digest(durable),
            SourcePacks = state.SourcePacks.ToArray(), ObservedStates = state.ObservedStates, SourceProofComplete = sourceProofComplete,
            RetentionPolicyVersion = Retention.Version, CreatedAtUtc = now, ExpiresAtUtc = now + Retention.Lifetime
        };
        var valid = await validator.ValidateAsync(checkpoint, authority, ct).ConfigureAwait(false);
        if (!valid.Valid)
        {
            // Missing restart provenance must not prevent an otherwise authorized active
            // turn from reducing its in-memory context. Still recheck every known source;
            // invalidation, authority drift and CAS losses never take this path.
            if (!sourceProofComplete)
            {
                var knownSources = await validator.ValidateAsync(checkpoint with { SourceProofComplete = true }, authority, ct).ConfigureAwait(false);
                if (knownSources.Valid)
                {
                    var currentAuthority = state.RefreshAuthorityAsync is { } refresh ? await refresh(ct).ConfigureAwait(false) : authority;
                    state.LastPublicationWasTurnOnly = currentAuthority.Digest == authority.Digest;
                }
            }
            return false;
        }
        if (!await store.TrySaveAsync(checkpoint, state.Generation, ct).ConfigureAwait(false)) return false;
        state.Generation = checkpoint.Generation; state.Restored = checkpoint;
        return true;
    }
}
