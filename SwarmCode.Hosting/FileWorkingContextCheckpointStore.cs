using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmCore.Agent.ToolUseEngine;

namespace SwarmCode.Hosting;

/// <summary>
/// Working-context checkpoints as one JSON file per conversation and profile under
/// &lt;data&gt;/sessions/context/. Registering a checkpoint store (with the manager) is what
/// makes the engine persist tool receipts at every durable boundary instead of only at the
/// end of a turn, so a cancelled or crashed turn keeps the work it already did. The file holds
/// a generation envelope for compare-and-swap; a deleted checkpoint leaves a tombstone so a late
/// writer cannot resurrect it. The transcript beside it is plain JSON already, so the
/// projection is stored the same way.
/// </summary>
public sealed class FileWorkingContextCheckpointStore : IWorkingContextCheckpointStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new GuidSetConverter() } };
    private readonly string _directory;
    private readonly WorkingContextRetentionPolicy _policy;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    private sealed record Envelope(long Generation, string? Payload);

    public FileWorkingContextCheckpointStore(SwarmCodeDataPaths paths, WorkingContextRetentionPolicy? policy = null, TimeProvider? time = null, ILogger<FileWorkingContextCheckpointStore>? logger = null)
    {
        _directory = Path.Combine(paths.SessionsDirectory, "context");
        _policy = policy ?? new WorkingContextRetentionPolicy();
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<FileWorkingContextCheckpointStore>.Instance;
        Directory.CreateDirectory(_directory);
    }

    public Task<WorkingContextLoadResult> LoadAsync(WorkingContextIdentity identity, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var envelope = Read(identity, out var corrupt);
            if (corrupt) return Task.FromResult(new WorkingContextLoadResult(WorkingContextLoadStatus.ReconstructionRequired, Reason: "checkpoint-envelope-corrupt"));
            if (envelope is null) return Task.FromResult(new WorkingContextLoadResult(WorkingContextLoadStatus.Missing));
            if (envelope.Payload is null) return Task.FromResult(new WorkingContextLoadResult(WorkingContextLoadStatus.Missing, Reason: "checkpoint-deleted", Generation: envelope.Generation));
            try
            {
                var value = JsonSerializer.Deserialize<WorkingContextCheckpoint>(envelope.Payload, Json);
                if (value is null || value.SchemaVersion != WorkingContextCheckpoint.CurrentSchemaVersion || value.Identity != identity || value.Generation != envelope.Generation)
                    return Task.FromResult(new WorkingContextLoadResult(WorkingContextLoadStatus.ReconstructionRequired, Reason: "checkpoint-identity-or-version-mismatch", Generation: envelope.Generation));
                if (!RetentionValid(value))
                    return Task.FromResult(new WorkingContextLoadResult(WorkingContextLoadStatus.ReconstructionRequired, Reason: "checkpoint-retention-changed-or-expired", Generation: envelope.Generation));
                return Task.FromResult(new WorkingContextLoadResult(WorkingContextLoadStatus.Available, value, Generation: envelope.Generation));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                _logger.LogWarning(ex, "Working-context checkpoint for {Conversation} is unreadable; it will be rebuilt.", identity.ConversationId);
                return Task.FromResult(new WorkingContextLoadResult(WorkingContextLoadStatus.ReconstructionRequired, Reason: "checkpoint-unreadable", Generation: envelope.Generation));
            }
        }
    }

    public Task<bool> TrySaveAsync(WorkingContextCheckpoint checkpoint, long expectedGeneration, CancellationToken ct = default)
    {
        if (expectedGeneration == long.MaxValue) return Task.FromResult(false); // exhausted tombstone
        if (expectedGeneration < 0 || checkpoint.Generation != expectedGeneration + 1 || checkpoint.Identity.ProfileId == Guid.Empty
            || string.IsNullOrWhiteSpace(checkpoint.Identity.ConversationId) || checkpoint.SchemaVersion != WorkingContextCheckpoint.CurrentSchemaVersion)
            throw new ArgumentException("Invalid working-context generation or identity.");
        if (!RetentionValid(checkpoint))
        {
            _logger.LogDebug("Checkpoint for {Conversation} rejected: retention policy or timestamps invalid.", checkpoint.Identity.ConversationId);
            return Task.FromResult(false);
        }
        var payload = JsonSerializer.Serialize(checkpoint, Json);
        if (payload.Length > _policy.MaximumPayloadCharacters)
        {
            _logger.LogDebug("Checkpoint for {Conversation} rejected: {Length} characters exceeds the retention maximum.", checkpoint.Identity.ConversationId, payload.Length);
            return Task.FromResult(false);
        }

        lock (_gate)
        {
            var current = Read(checkpoint.Identity, out var corrupt);
            if (corrupt) return Task.FromResult(false);
            if ((current?.Generation ?? 0) != expectedGeneration)
            {
                _logger.LogDebug("Checkpoint for {Conversation} lost the compare-and-swap: expected generation {Expected}, found {Found}.", checkpoint.Identity.ConversationId, expectedGeneration, current?.Generation ?? 0);
                return Task.FromResult(false);
            }
            Write(checkpoint.Identity, new Envelope(checkpoint.Generation, payload));
            return Task.FromResult(true);
        }
    }

    public Task DeleteAsync(WorkingContextIdentity identity, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var current = Read(identity, out _);
            var generation = current?.Generation ?? 0;
            Write(identity, new Envelope(generation == long.MaxValue ? long.MaxValue : Math.Max(0, generation) + 1, null));
        }
        return Task.CompletedTask;
    }

    private bool RetentionValid(WorkingContextCheckpoint checkpoint)
    {
        var now = _time.GetUtcNow();
        return checkpoint.RetentionPolicyVersion == _policy.Version
            && checkpoint.CreatedAtUtc <= now && checkpoint.ExpiresAtUtc > now
            && checkpoint.ExpiresAtUtc > checkpoint.CreatedAtUtc
            && checkpoint.ExpiresAtUtc - checkpoint.CreatedAtUtc <= _policy.Lifetime;
    }

    internal string PathFor(WorkingContextIdentity identity)
    {
        var key = identity.ConversationId + "|" + identity.ProfileId.ToString("N") + "|" + (identity.MissionId?.ToString("N") ?? "chat");
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant();
        return Path.Combine(_directory, name + ".json");
    }

    private Envelope? Read(WorkingContextIdentity identity, out bool corrupt)
    {
        corrupt = false;
        var path = PathFor(identity);
        if (!File.Exists(path)) return null;
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path), Json);
            if (envelope is null || envelope.Generation < 1) { corrupt = true; return null; }
            return envelope;
        }
        catch (JsonException) { corrupt = true; return null; }
    }

    private void Write(WorkingContextIdentity identity, Envelope envelope)
    {
        var path = PathFor(identity);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(envelope, Json));
        File.Move(tmp, path, overwrite: true);
    }

    private sealed class GuidSetConverter : JsonConverter<IReadOnlySet<Guid>>
    {
        public override IReadOnlySet<Guid> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
            => (JsonSerializer.Deserialize<Guid[]>(ref reader, options) ?? []).ToHashSet();
        public override void Write(Utf8JsonWriter writer, IReadOnlySet<Guid> value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, value.Order().ToArray(), options);
    }
}

/// <summary>
/// Source validation for a coding workspace. SwarmCode injects no account-scoped context
/// packs, so a checkpoint is valid when its authority (the request digest and steering
/// revision) still matches; the manager has already checked identity, schema, retention, and
/// the durable transcript digest before asking.
/// </summary>
public sealed class CodingWorkingContextSourceValidator(ILogger<CodingWorkingContextSourceValidator>? logger = null) : IWorkingContextSourceValidator
{
    private readonly ILogger _logger = logger ?? NullLogger<CodingWorkingContextSourceValidator>.Instance;

    /// <summary>
    /// First turn without a checkpoint: the durable transcript IS the provenance in a coding workspace
    /// (the user's own repository, shell, and messages), so it is handed back whole. Returning the default
    /// empty result would make the engine start the provider transcript from the current message alone.
    /// </summary>
    public Task<WorkingContextLegacyResult> RevalidateLegacyAsync(WorkingContextIdentity identity, WorkingContextAuthority authority,
        IReadOnlyList<SwarmCore.Abstractions.LlmRouting.ChatMessage> transcript, CancellationToken ct = default)
        => Task.FromResult(new WorkingContextLegacyResult(transcript, [], SourceProofComplete: true));

    public Task<WorkingContextValidationResult> ValidateAsync(WorkingContextCheckpoint checkpoint, WorkingContextAuthority currentAuthority, CancellationToken ct = default)
    {
        string? reason = null;
        if (!checkpoint.SourceProofComplete) reason = "checkpoint-source-proof-incomplete";
        else if (checkpoint.Authority.Digest != currentAuthority.Digest || checkpoint.Authority.SteeringRevision != currentAuthority.SteeringRevision) reason = "checkpoint-authority-changed";
        else if (checkpoint.SourcePacks.Count > 0 || checkpoint.ObservedStates.Count > 0) reason = "checkpoint-source-revalidation-unavailable";
        if (reason is not null)
        {
            _logger.LogDebug("Working-context checkpoint for {Conversation} not accepted: {Reason}.", checkpoint.Identity.ConversationId, reason);
            return Task.FromResult(new WorkingContextValidationResult(false, reason));
        }
        return Task.FromResult(new WorkingContextValidationResult(true));
    }
}
