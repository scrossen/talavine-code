using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SwarmCore.Agent.Continual;
using SwarmCore.Coding.Goals;

namespace SwarmCode.Hosting.Goals;

/// <summary>
/// Profile-local atomic objective inventory. Every operation reloads under an OS file lock, so
/// independent workspaces/processes cannot replace a newer row or resurrect a deleted conversation.
/// Receipt texts longer than <see cref="InlineTextLimit"/> live in a per-goal append-only body log
/// (<c>goals/receipt-bodies/&lt;goal&gt;.log</c>) and the inventory keeps only hashed references, so the
/// file every write rewrites stays small while callers still receive the exact saved texts. A document
/// stamp lets a process reuse its last parsed inventory while the file bytes are unchanged. Each log record
/// is framed with its key and length, so references can be rebuilt if an older build ever rewrote the inventory.
/// </summary>
public sealed partial class FileCodingGoalStore(SwarmCodeDataPaths paths, TimeProvider? time = null, ILogger? logger = null) : ICodingGoalStore
{
    /// <summary>Receipt result/input texts above this length are retained in the body log, not the inventory.</summary>
    public const int InlineTextLimit = 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, CachedDocument> Cache = new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<ContinualToolReceipt, PersistedReceipt> Persisted = new();
    private static readonly ConcurrentDictionary<string, byte> Unavailable = new(StringComparer.Ordinal);
    /// <summary>Retained texts whose log bytes were missing or damaged at load; their receipts carry a null text until the log is restored.</summary>
    public static IReadOnlyCollection<string> UnavailableBodies => Unavailable.Keys.ToArray();
    private readonly string _path = Path.GetFullPath(Path.Combine(paths.RootDirectory, "goals", "objectives.json"));
    private readonly string _bodies = Path.GetFullPath(Path.Combine(paths.RootDirectory, "goals", "receipt-bodies"));
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    public bool SupportsRetainedVerificationCandidates => true;

    public Task<ContinualObjectiveSnapshot> CreateOrJoinAsync(ContinualObjectiveStart start, DateTimeOffset nowUtc, CancellationToken ct = default)
        => Access(async (document, store) =>
        {
            StorageIds.Validate(start.ConversationId);
            if (document.DeletedConversations.Contains(start.ConversationId, StringComparer.Ordinal))
                throw new InvalidOperationException("This conversation was deleted; its goal cannot be recreated.");
            var active = document.Rows.FirstOrDefault(row => row.ConversationId == start.ConversationId
                && !ContinualObjectiveStateMachine.IsTerminal(row.Status));
            if (active is not null && active.IdempotencyKey != start.IdempotencyKey)
                throw new InvalidOperationException("This conversation already has a goal. Pause, resume or cancel that goal first.");
            return await store.CreateOrJoinAsync(start, nowUtc, ct).ConfigureAwait(false);
        }, true, ct);

    public Task<ContinualObjectiveSnapshot?> GetAsync(Guid id, CancellationToken ct = default)
        => Access((_, store) => store.GetAsync(id, ct), false, ct);
    public Task<ContinualObjectiveSnapshot?> GetLatestForConversationAsync(string partitionKey, string conversationId, CancellationToken ct = default)
        => Access((_, store) => store.GetLatestForConversationAsync(partitionKey, conversationId, ct), false, ct);
    public Task<IReadOnlyList<ContinualObjectiveSnapshot>> GetDueAsync(DateTimeOffset nowUtc, int take, CancellationToken ct = default)
        => Access((_, store) => store.GetDueAsync(nowUtc, take, ct), false, ct);
    public Task<ContinualObjectiveLease?> TryClaimAsync(Guid id, long expectedVersion, string ownerId, DateTimeOffset nowUtc, DateTimeOffset leaseExpiresAtUtc, CancellationToken ct = default)
        => Access((_, store) => store.TryClaimAsync(id, expectedVersion, ownerId, nowUtc, leaseExpiresAtUtc, ct), true, ct);
    public Task<bool> HeartbeatAsync(Guid id, string leaseToken, DateTimeOffset nowUtc, DateTimeOffset leaseExpiresAtUtc, CancellationToken ct = default)
        => Access((_, store) => store.HeartbeatAsync(id, leaseToken, nowUtc, leaseExpiresAtUtc, ct), true, ct);
    public Task<bool> UpsertReceiptAsync(Guid id, string leaseToken, ContinualToolReceipt receipt, CancellationToken ct = default)
        => Access((document, store) =>
        {
            // Only ResolveReceiptAsync can establish host proof. Late ordinary events may not
            // erase a resolution or change the invocation to which it was bound.
            if (receipt.HostResolution is not null) return Task.FromResult(false);
            var row = document.Rows.FirstOrDefault(row => row.Id == id);
            var saved = row?.Receipts.FirstOrDefault(prior => prior.Attempt == receipt.Attempt && prior.ToolCallId == receipt.ToolCallId);
            if (saved?.HostResolution is not null)
                return Task.FromResult(row!.LeaseToken == leaseToken && row.LeaseExpiresAtUtc > _time.GetUtcNow()
                    && row.Status == ContinualObjectiveStatus.InProgress && ContinualReceiptResolutionPolicy.Fingerprint(saved)
                    == ContinualReceiptResolutionPolicy.Fingerprint(receipt));
            return store.UpsertReceiptAsync(id, leaseToken, receipt, ct);
        }, true, ct);
    public Task<bool> RecordProgressAsync(Guid id, string leaseToken, long sequence, string? toolName, string status, DateTimeOffset atUtc, CancellationToken ct = default)
        => Access((_, store) => store.RecordProgressAsync(id, leaseToken, sequence, toolName, status, atUtc, ct), true, ct);
    public Task<bool> ApplyTransitionAsync(Guid id, string leaseToken, ContinualObjectiveTransition transition, CancellationToken ct = default)
        => Access(async (document, store) =>
        {
            // Inbox writers use this same profile lock. A message accepted during verification must
            // be consumed and audited before completion can commit. Protected mission verification
            // retains its existing authority and evidence contract.
            var current = document.Rows.FirstOrDefault(row => row.Id == id);
            if (transition.Status == ContinualObjectiveStatus.Completed && current is { Mission: null }
                && current.PartitionKey == CodingGoalOrchestrator.Partition
                && !FileGoalMailbox.CanComplete(paths, current, transition.LastOutput))
            {
                await store.ApplyTransitionAsync(id, leaseToken, transition with
                {
                    Status = ContinualObjectiveStatus.RetryScheduled, NextAttemptAtUtc = _time.GetUtcNow(),
                    LastFailure = null, ContinuationFeedback = "User messages or their completion evidence changed before completion. Read the saved inbox and verify the current instructions at the next natural stopping point; continue this same goal without replaying actions.",
                    ReleaseLease = true,
                }, ct).ConfigureAwait(false);
                // The requested completion did not commit. The processor's existing reread observes
                // the durable continuation instead of a stale active lease or false success event.
                return false;
            }
            return await store.ApplyTransitionAsync(id, leaseToken, transition, ct).ConfigureAwait(false);
        }, true, ct);
    public Task<bool> SaveVerificationCandidateAsync(Guid id, string leaseToken, string output, DateTimeOffset atUtc, CancellationToken ct = default)
        => Access((_, store) => store.SaveVerificationCandidateAsync(id, leaseToken, output, atUtc, ct), true, ct);
    public Task<bool> TryStopAsync(Guid id, long expectedVersion, string reason, DateTimeOffset nowUtc, CancellationToken ct = default)
        => Access((_, store) => store.TryStopAsync(id, expectedVersion, reason, nowUtc, ct), true, ct);

    public Task<bool> ControlAsync(Guid id, long expectedVersion, CodingGoalControl control, DateTimeOffset now, CancellationToken ct = default)
        => Mutate(id, expectedVersion, row =>
        {
            if (ContinualObjectiveStateMachine.IsTerminal(row.Status)) return null;
            if (control == CodingGoalControl.Resume && row.LeaseExpiresAtUtc > now) return null;
            var status = control switch { CodingGoalControl.Resume => ContinualObjectiveStatus.RetryScheduled,
                CodingGoalControl.Cancel => ContinualObjectiveStatus.Cancelled, _ => ContinualObjectiveStatus.Paused };
            // Own one payload field under the same checked inventory mutation. Preserve references,
            // instruction chronology, and fields written by newer hosts.
            var payload = ReadPayloadObject(row);
            payload[nameof(CodingGoalPayload.LastUserControl)] = JsonSerializer.SerializeToNode(
                new CodingGoalUserControl(row.Id, control, now, row.Version + 1, row.Status, status));
            return row with
            {
                Status = status, ExecutorPayloadJson = payload.ToJsonString(),
                NextAttemptAtUtc = control == CodingGoalControl.Resume ? now : null,
                ContinuationFeedback = control == CodingGoalControl.Resume
                    ? row.ContinuationFeedback == "Paused by the user." ? null : row.ContinuationFeedback
                    : control == CodingGoalControl.Cancel ? "Cancelled by the user." : "Paused by the user.",
                UpdatedAtUtc = now, LeaseOwner = null, LeaseToken = null, LeaseExpiresAtUtc = null,
                CurrentToolName = null, Version = row.Version + 1,
            };
        }, ct);

    public Task<bool> TryResumeAfterRepairAsync(SwarmCore.Coding.SelfImprovement.GoalRepairLink baseline,
        string repairItemId, string installedFixEvidence, DateTimeOffset now, CancellationToken ct = default)
        => Mutate(baseline.GoalId, baseline.GoalVersion, row =>
        {
            if (!SwarmCore.Coding.SelfImprovement.JsonFileSelfImprovementBacklog.IsValidId(repairItemId)
                || string.IsNullOrWhiteSpace(installedFixEvidence) || !baseline.CanResume(row, now)) return null;
            var payload = ReadPayloadObject(row);
            payload[nameof(CodingGoalPayload.RepairContinuation)] = JsonSerializer.SerializeToNode(
                new CodingGoalRepairContinuation(repairItemId, row.Version + 1, baseline.IntentDigest));
            return row with
            {
                ExecutorPayloadJson = payload.ToJsonString(),
                Status = ContinualObjectiveStatus.RetryScheduled, NextAttemptAtUtc = now,
                ContinuationFeedback = (row.Receipts.Any(MissionTransitions.NeedsInspection)
                    ? "A verified harness repair is now installed. Inspect the exact uncertain outcomes through the host recovery gate before any ordinary execution. Never replay uncertain or completed actions. "
                    : "A verified harness repair is now installed. Continue this same goal from saved progress; do not repeat completed actions. ")
                    + installedFixEvidence,
                UpdatedAtUtc = now, CurrentToolName = null, Version = row.Version + 1,
            };
        }, ct);

    public Task<bool> ReconcileAsync(Guid id, string leaseToken, string evidence, DateTimeOffset now, CancellationToken ct = default)
        => Access((document, _) =>
        {
            var index = document.Rows.FindIndex(row => row.Id == id);
            if (index < 0) return Task.FromResult(false);
            var row = document.Rows[index];
            if (row.LeaseToken != leaseToken || row.LeaseExpiresAtUtc <= now || row.Status != ContinualObjectiveStatus.InProgress
                || !row.Receipts.Any(receipt => receipt.Attempt == row.AttemptCount
                    && receipt.State == ContinualToolReceiptState.Completed && !receipt.IsError
                    && receipt.SideEffect == ContinualSideEffectClass.ReadOnly)) return Task.FromResult(false);
            var sequence = row.LastEventSequence;
            var receipts = row.Receipts.Select(receipt => receipt.State == ContinualToolReceiptState.Started
                && receipt.Attempt < row.AttemptCount ? receipt with
                {
                    State = ContinualToolReceiptState.Completed, Sequence = ++sequence, CompletedAtUtc = now,
                    Result = "Prior outcome reconciled through read-only observations and independent acceptance. Do not replay this operation.\n" + evidence,
                    RecoveryJson = "{\"kind\":\"coding-goal-read-only-reconciliation\"}",
                } : receipt).ToArray();
            document.Rows[index] = row with { Receipts = receipts, LastEventSequence = sequence,
                UpdatedAtUtc = now, Version = row.Version + 1 };
            return Task.FromResult(true);
        }, true, ct, replaceRows: false);

    public Task<bool> ResolveReceiptAsync(Guid id, string leaseToken, ContinualReceiptResolution resolution,
        DateTimeOffset now, CancellationToken ct = default)
        => Access((document, _) =>
        {
            var index = document.Rows.FindIndex(row => row.Id == id);
            if (index < 0) return Task.FromResult(false);
            var row = document.Rows[index];
            var checkedAt = _time.GetUtcNow();
            if (row.Mission is not null || row.LeaseToken != leaseToken || row.LeaseExpiresAtUtc <= checkedAt
                || row.Status != ContinualObjectiveStatus.InProgress || resolution.VerifiedAtUtc > checkedAt || now > checkedAt)
                return Task.FromResult(false);
            var receipt = row.Receipts.SingleOrDefault(prior => prior.Attempt == resolution.Attempt
                && prior.ToolCallId == resolution.ToolCallId);
            if (receipt is null || receipt.ObjectiveId != id || !MissionTransitions.NeedsInspection(receipt)
                || !ContinualReceiptResolutionPolicy.Matches(receipt, resolution)
                || resolution.Observations.Any(binding => binding.Attempt != row.AttemptCount))
                return Task.FromResult(false);
            var resolved = receipt with { HostResolution = resolution };
            if (!ContinualReceiptResolutionPolicy.IsResolved(resolved, row.Receipts)) return Task.FromResult(false);
            if (receipt.HostResolution is not null)
                return Task.FromResult(JsonSerializer.Serialize(receipt.HostResolution, Json) == JsonSerializer.Serialize(resolution, Json));
            document.Rows[index] = row with
            {
                Receipts = row.Receipts.Select(prior => prior.Attempt == receipt.Attempt && prior.ToolCallId == receipt.ToolCallId
                    ? resolved : prior).ToArray(),
                Version = row.Version + 1, UpdatedAtUtc = now, LastProgressAtUtc = now, ConsecutiveNoProgress = 0,
            };
            return Task.FromResult(true);
        }, true, ct, replaceRows: false);

    public Task<bool> UpdateAsync(Guid id, long expectedVersion, string objective, DateTimeOffset now, CancellationToken ct = default,
        IReadOnlyList<SwarmCore.Coding.Files.CodingAttachment>? attachments = null)
    {
        if (string.IsNullOrWhiteSpace(objective)) throw new ArgumentException("The goal is required.", nameof(objective));
        return Mutate(id, expectedVersion, row =>
        {
            if (ContinualObjectiveStateMachine.IsTerminal(row.Status)) return null;
            var payload = ReadPayloadObject(row);
            if (attachments is not null)
                payload[nameof(CodingGoalPayload.Attachments)] = JsonSerializer.SerializeToNode(attachments);
            payload[nameof(CodingGoalPayload.AuthorityUpdatedAtUtc)] = JsonSerializer.SerializeToNode(now);
            payload[nameof(CodingGoalPayload.AuthorityAfterMessageCount)] = FileGoalMailbox.InstructionCount(paths, id);
            return row with
            {
                ObjectiveText = objective, ObjectiveSummary = objective.Length <= 300 ? objective : objective[..300],
                ExecutorPayloadJson = payload.ToJsonString(),
                VerificationCandidate = null, VerificationCandidatePending = false,
                ConsecutiveNoProgress = 0, LastOutputFingerprint = null, UpdatedAtUtc = now,
                Status = ContinualObjectiveStatus.Paused, NextAttemptAtUtc = null,
                LeaseOwner = null, LeaseToken = null, LeaseExpiresAtUtc = null, CurrentToolName = null,
                Version = row.Version + 1,
            };
        }, ct);
    }

    private static System.Text.Json.Nodes.JsonObject ReadPayloadObject(ContinualObjectiveSnapshot row)
        => row.ExecutorPayloadJson is null
            ? JsonSerializer.SerializeToNode(new CodingGoalPayload([], null))!.AsObject()
            : System.Text.Json.Nodes.JsonNode.Parse(row.ExecutorPayloadJson) as System.Text.Json.Nodes.JsonObject
                ?? throw new InvalidDataException("The saved goal references could not be read.");

    public Task<bool> HoldAsync(Guid id, long expectedVersion, string reason, DateTimeOffset now, CancellationToken ct = default)
        => Mutate(id, expectedVersion, row => ContinualObjectiveStateMachine.IsTerminal(row.Status) ? null : row with
        {
            Status = ContinualObjectiveStatus.Uncertain, LastFailure = reason, ContinuationFeedback = reason,
            NextAttemptAtUtc = null, UpdatedAtUtc = now, LeaseOwner = null, LeaseToken = null,
            LeaseExpiresAtUtc = null, CurrentToolName = null, Version = row.Version + 1,
        }, ct);

    public async Task DeleteConversationAsync(string conversationId, CancellationToken ct = default)
    {
        StorageIds.Validate(conversationId);
        await Access((document, _) =>
        {
            document.Rows.RemoveAll(row => row.ConversationId == conversationId);
            if (!document.DeletedConversations.Contains(conversationId, StringComparer.Ordinal)) document.DeletedConversations.Add(conversationId);
            return Task.FromResult(true);
        }, true, ct, replaceRows: false).ConfigureAwait(false);
    }

    private Task<bool> Mutate(Guid id, long expectedVersion, Func<ContinualObjectiveSnapshot, ContinualObjectiveSnapshot?> mutation, CancellationToken ct)
        => Access((document, _) =>
        {
            var index = document.Rows.FindIndex(row => row.Id == id);
            if (index < 0 || document.Rows[index].Version != expectedVersion) return Task.FromResult(false);
            var replacement = mutation(document.Rows[index]);
            if (replacement is null) return Task.FromResult(false);
            document.Rows[index] = replacement;
            return Task.FromResult(true);
        }, true, ct, replaceRows: false);

    /// <summary>Mailbox mutations share goal deletion/control's cross-process serialization boundary.</summary>
    internal Task<T> WithGoalsAsync<T>(Func<IReadOnlyList<ContinualObjectiveSnapshot>, Task<T>> action,
        CancellationToken ct = default) => Access((document, _) => action(document.Rows), false, ct);

    private async Task<T> Access<T>(Func<Document, InMemoryContinualObjectiveStore, Task<T>> operation, bool write, CancellationToken ct, bool replaceRows = true)
    {
        var gate = Gates.GetOrAdd(_path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            FileStream lease;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try { lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); break; }
                catch (IOException) { await Task.Delay(25, ct).ConfigureAwait(false); }
            }
            using (lease)
            {
                var document = await LoadAsync(ct).ConfigureAwait(false);
                document.SavedRows = null;
                var before = document.Rows.ToArray();
                var deletedBefore = document.DeletedConversations.Count;
                var store = InMemoryContinualObjectiveStore.Restore(document.Rows, _time);
                var result = await operation(document, store).ConfigureAwait(false);
                if (write)
                {
                    if (replaceRows) document.Rows = store.SnapshotAll().ToList();
                    // A refused claim or control changes nothing; rewriting would only restamp the file and
                    // invalidate every other process's parsed copy. An operation that saved midway (compaction, before
                    // it rewrites a log) is compared against what it saved.
                    if (Changed(document.SavedRows ?? before, document.SavedDeleted ?? deletedBefore, document)) await SaveAsync(document, ct).ConfigureAwait(false);
                }
                return result;
            }
        }
        finally { gate.Release(); }
    }

    private static bool Changed(ContinualObjectiveSnapshot[] before, int deletedBefore, Document document)
    {
        if (document.Dirty || document.DeletedConversations.Count != deletedBefore || document.Rows.Count != before.Length) return true;
        for (var index = 0; index < before.Length; index++)
            if (!ReferenceEquals(before[index], document.Rows[index])) return true;
        return false;
    }

    /// <summary>
    /// Reads the inventory under the lock. An unchanged stamp reuses this process's last parsed copy
    /// (rows are immutable records; the lists are copied), so pollers and heartbeats stop re-parsing
    /// the whole file. Any other writer changes the stamp, which forces a fresh read.
    /// </summary>
    private async Task<Document> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return new Document();
        var stamp = ReadStamp(_path);
        if (stamp is not null && Cache.TryGetValue(_path, out var cached) && cached.Stamp == stamp) return cached.Document.Clone();
        var document = JsonSerializer.Deserialize<Document>(await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false), Json)
            ?? throw new InvalidDataException("The saved goal inventory is unreadable.");
        Rehydrate(document);
        if (stamp is not null && document.Stamp == stamp) Cache[_path] = new CachedDocument(stamp, document.Clone());
        else Cache.TryRemove(_path, out _);
        return document;
    }

    private static string? ReadStamp(string path)
    {
        var head = new byte[96];
        int read;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) read = stream.Read(head, 0, head.Length);
        var match = StampPattern().Match(Encoding.UTF8.GetString(head, 0, read));
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex("^\\{\\s*\"stamp\"\\s*:\\s*\"([0-9a-f]{32})\"")]
    private static partial Regex StampPattern();

    /// <summary>
    /// Restores retained receipt texts so snapshots are byte-for-byte the receipts that were saved. A body is
    /// applied only where the inline text is null (the inventory form of a retained text); a damaged or missing
    /// body leaves the text null and keeps its reference, so nothing fabricated reaches a reader or the model.
    /// </summary>
    private void Rehydrate(Document document)
    {
        for (var index = 0; index < document.Rows.Count; index++)
        {
            var row = document.Rows[index];
            var log = LogPath(row.Id);
            if (!File.Exists(log)) continue;
            var prefix = row.Id.ToString("N") + "/";
            if (!document.Bodies.Keys.Any(key => key.StartsWith(prefix, StringComparison.Ordinal)))
            {
                // An inventory rewritten by a build that did not know the body log lost its references. The
                // framed log still identifies every body, so rebuild them once; the next save persists them.
                foreach (var pair in ScanLog(log))
                    if (pair.Key.StartsWith(prefix, StringComparison.Ordinal)) document.Bodies[pair.Key] = pair.Value;
            }
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var receipts = row.Receipts.ToArray();
            var changed = false;
            Dictionary<(string Key, string Sha256), BodyReference>? scanned = null;
            for (var position = 0; position < receipts.Length; position++)
            {
                var receipt = receipts[position];
                var result = receipt.Result;
                var input = receipt.InputJson;
                if (result is null && document.Bodies.TryGetValue(BodyKey(row.Id, receipt, ResultField), out var resultReference))
                    result = ReadBody(document, log, stream, resultReference, BodyKey(row.Id, receipt, ResultField), ref scanned);
                if (input is null && document.Bodies.TryGetValue(BodyKey(row.Id, receipt, InputField), out var inputReference))
                    input = ReadBody(document, log, stream, inputReference, BodyKey(row.Id, receipt, InputField), ref scanned);
                if (ReferenceEquals(result, receipt.Result) && ReferenceEquals(input, receipt.InputJson)) continue;
                receipts[position] = receipt with { Result = result, InputJson = input };
                changed = true;
            }
            if (changed) document.Rows[index] = row with { Receipts = receipts };
        }
    }

    /// <summary>
    /// Reads one retained text. A reference whose offset no longer verifies (an interrupted log rewrite, or an inventory an
    /// older build rewrote) is repaired from the framed log by key and hash; only a body absent from the log is unavailable.
    /// </summary>
    private string? ReadBody(Document document, string log, FileStream stream, BodyReference reference, string key,
        ref Dictionary<(string Key, string Sha256), BodyReference>? scanned)
    {
        var bytes = ReadVerified(stream, reference, key);
        if (bytes is null)
        {
            var repaired = Rescan(log, ref scanned, key, reference);
            bytes = ReadVerified(stream, repaired, key);
            if (bytes is null)
                return Diagnose(key, reference.Offset > stream.Length - reference.Length ? "the body log is shorter than the saved reference" : "the saved body hash does not match");
            document.Bodies[key] = repaired!;
            document.Dirty = true;
            logger?.LogInformation("Retained receipt text {Key} was found at another position in the body log; its reference is repaired.", key);
        }
        Unavailable.TryRemove(key, out _);
        return Encoding.UTF8.GetString(bytes);
    }

    private string? Diagnose(string key, string reason)
    {
        if (Unavailable.TryAdd(key, 0))
            logger?.LogWarning("Retained receipt text {Key} is unavailable: {Reason}. The receipt keeps a null text and its reference until the body log is restored.", key, reason);
        return null;
    }

    /// <summary>Writes the inventory atomically: bodies are appended (and synced) before the file that references them.</summary>
    private async Task SaveAsync(Document document, CancellationToken ct)
    {
        var stamp = Guid.NewGuid().ToString("N");
        document.Stamp = stamp;
        var persisted = Offload(document);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, persisted, Json, ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
            document.Dirty = false;
            document.SavedRows = document.Rows.ToArray();
            document.SavedDeleted = document.DeletedConversations.Count;
            Cache[_path] = new CachedDocument(stamp, document.Clone());
        }
        catch
        {
            Cache.TryRemove(_path, out _);
            throw;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        // Only once the inventory no longer references a deleted goal may its log go; a failed save above keeps both.
        SweepStaleLogs(document);
    }

    private void SweepStaleLogs(Document document)
    {
        if (!Directory.Exists(_bodies)) return;
        var live = document.Rows.Select(row => row.Id.ToString("N")).ToHashSet(StringComparer.Ordinal);
        try
        {
            foreach (var stale in Directory.EnumerateFiles(_bodies, "*.log").ToArray())
            {
                if (live.Contains(Path.GetFileNameWithoutExtension(stale))) continue;
                try { File.Delete(stale); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Returns the inventory form of <paramref name="document"/>: long receipt texts replaced by null with a
    /// hashed reference into the goal's body log. The caller's document keeps the full texts and gains the
    /// references; references of deleted goals are dropped here.
    /// </summary>
    private Document Offload(Document document)
    {
        var live = document.Rows.Select(row => row.Id.ToString("N")).ToHashSet(StringComparer.Ordinal);
        foreach (var key in document.Bodies.Keys.Where(key => key.Length < 33 || !live.Contains(key[..32])).ToArray()) document.Bodies.Remove(key);
        var persisted = new Document
        {
            Stamp = document.Stamp, Rows = [.. document.Rows], DeletedConversations = [.. document.DeletedConversations],
            Bodies = document.Bodies, CompactionRuns = document.CompactionRuns,
        };
        for (var index = 0; index < persisted.Rows.Count; index++)
        {
            var row = persisted.Rows[index];
            ContinualToolReceipt[]? receipts = null;
            FileStream? log = null;
            try
            {
                for (var position = 0; position < row.Receipts.Count; position++)
                {
                    var receipt = row.Receipts[position];
                    var form = Persist(row.Id, receipt, document.Bodies, ref log);
                    if (ReferenceEquals(form, receipt)) continue;
                    receipts ??= row.Receipts.ToArray();
                    receipts[position] = form;
                }
            }
            finally { log?.Dispose(); }
            if (receipts is not null) persisted.Rows[index] = row with { Receipts = receipts };
        }
        return persisted;
    }

    private ContinualToolReceipt Persist(Guid rowId, ContinualToolReceipt receipt, Dictionary<string, BodyReference> bodies, ref FileStream? log)
    {
        // The same immutable receipt instance was already placed and its references are still current.
        if (Persisted.TryGetValue(receipt, out var known)
            && known.References.All(pair => bodies.TryGetValue(pair.Key, out var current) && current == pair.Value))
            return known.Receipt;
        var references = new List<KeyValuePair<string, BodyReference>>(2);
        var result = Place(rowId, receipt, ResultField, receipt.Result, receipt.CompactedResult is not null, bodies, references, ref log);
        var input = Place(rowId, receipt, InputField, receipt.InputJson, receipt.CompactedInput is not null, bodies, references, ref log);
        var persisted = ReferenceEquals(result, receipt.Result) && ReferenceEquals(input, receipt.InputJson)
            ? receipt : receipt with { Result = result, InputJson = input };
        Persisted.AddOrUpdate(receipt, new PersistedReceipt(persisted, references));
        return persisted;
    }

    /// <summary>
    /// The inline form of one text: null once it is retained in the log, otherwise the text itself. A short, compacted or
    /// non-round-trippable text drops any older reference so a stale body can never shadow it; a null text keeps
    /// its reference (it is retained, or waiting for a restored log). A compacted text is the digest the inventory must
    /// hold itself, whatever its length.
    /// </summary>
    private string? Place(Guid rowId, ContinualToolReceipt receipt, string field, string? text, bool compacted, Dictionary<string, BodyReference> bodies,
        List<KeyValuePair<string, BodyReference>> references, ref FileStream? log)
    {
        var key = BodyKey(rowId, receipt, field);
        if (text is null)
        {
            if (bodies.TryGetValue(key, out var kept)) references.Add(new(key, kept));
            return null;
        }
        if (compacted || text.Length <= InlineTextLimit || !RoundTrips(text))
        {
            bodies.Remove(key);
            return text;
        }
        references.Add(Retain(rowId, key, text, bodies, ref log));
        return null;
    }

    /// <summary>Only texts that survive UTF-8 leave the inventory; a lone surrogate stays inline.</summary>
    private static bool RoundTrips(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                if (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1])) return false;
                index++;
            }
            else if (char.IsLowSurrogate(text[index])) return false;
        }
        return true;
    }

    private KeyValuePair<string, BodyReference> Retain(Guid rowId, string key, string text, Dictionary<string, BodyReference> bodies, ref FileStream? log)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        if (bodies.TryGetValue(key, out var existing) && existing.Length == bytes.Length
            && string.Equals(existing.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
            return new(key, existing);
        if (log is null)
        {
            Directory.CreateDirectory(_bodies);
            log = new FileStream(LogPath(rowId), FileMode.Append, FileAccess.Write, FileShare.Read);
        }
        // Frame: marker, key, byte length, then the bytes and a newline. The reference points at the bytes.
        if (key.IndexOfAny(['\n', '\r']) < 0) log.Write(Encoding.UTF8.GetBytes(Frame + "\n" + key + "\n" + bytes.Length + "\n"));
        var offset = log.Position;
        log.Write(bytes, 0, bytes.Length);
        log.Write("\n"u8);
        log.Flush(flushToDisk: true);
        var reference = new BodyReference(offset, bytes.Length, sha256);
        bodies[key] = reference;
        return new(key, reference);
    }

    /// <summary>Rebuilds references from a framed log; a record counts only when its trailer lands on a frame or the end.</summary>
    private static List<KeyValuePair<string, BodyReference>> ScanLog(string path)
    {
        var found = new List<KeyValuePair<string, BodyReference>>();
        var data = File.ReadAllBytes(path);
        var frame = Encoding.UTF8.GetBytes(Frame + "\n");
        var position = 0;
        while (position < data.Length)
        {
            var start = data.AsSpan(position).IndexOf(frame);
            if (start < 0) break;
            position += start + frame.Length;
            var keyEnd = data.AsSpan(position).IndexOf((byte)'\n');
            if (keyEnd < 0) break;
            var key = Encoding.UTF8.GetString(data, position, keyEnd);
            position += keyEnd + 1;
            var lengthEnd = data.AsSpan(position).IndexOf((byte)'\n');
            if (lengthEnd < 0 || !int.TryParse(Encoding.ASCII.GetString(data, position, lengthEnd), out var length) || length < 0) break;
            position += lengthEnd + 1;
            if (position + length + 1 > data.Length) break;
            var next = position + length + 1;
            if (data[position + length] != (byte)'\n' || (next < data.Length && !data.AsSpan(next).StartsWith(frame))) break;
            found.Add(new(key, new BodyReference(position, length, Convert.ToHexString(SHA256.HashData(data.AsSpan(position, length))))));
            position = next;
        }
        return found;
    }

    private const string Frame = "SWARMCODE-RECEIPT-BODY";
    private const string ResultField = "result";
    private const string InputField = "input";
    private static string BodyKey(Guid rowId, ContinualToolReceipt receipt, string field)
        => $"{rowId:N}/{receipt.Attempt}/{field}/{receipt.ToolCallId}";
    private string LogPath(Guid rowId) => Path.Combine(_bodies, rowId.ToString("N") + ".log");

    private sealed class Document
    {
        /// <summary>Changes on every write; first in the file so a reader can check it from the first bytes.</summary>
        [JsonPropertyOrder(-2)] public string? Stamp { get; set; }
        /// <summary>2 = retained texts live in the framed body log. A file without it is the earlier all-inline shape.</summary>
        [JsonPropertyOrder(-1)] public int Format { get; set; } = 2;
        public List<ContinualObjectiveSnapshot> Rows { get; set; } = [];
        public List<string> DeletedConversations { get; set; } = [];
        /// <summary>Retained receipt texts by <c>goal/attempt/field/toolCallId</c>.</summary>
        public Dictionary<string, BodyReference> Bodies { get; set; } = new(StringComparer.Ordinal);
        /// <summary>Compaction passes that changed a goal's storage, oldest first (bounded by <see cref="CompactionJournalLimit"/>).</summary>
        public List<GoalCompactionRun> CompactionRuns { get; set; } = [];
        /// <summary>Set when references moved (a log rewrite, or a repaired stale reference) so the next write persists them even if no row changed.</summary>
        [JsonIgnore] public bool Dirty { get; set; }
        /// <summary>The rows and tombstone count as of a save made inside the current operation, so its final change check compares against them.</summary>
        [JsonIgnore] public ContinualObjectiveSnapshot[]? SavedRows { get; set; }
        [JsonIgnore] public int? SavedDeleted { get; set; }
        public Document Clone() => new()
        {
            Stamp = Stamp, Format = Format, Rows = [.. Rows], DeletedConversations = [.. DeletedConversations],
            Bodies = new Dictionary<string, BodyReference>(Bodies, StringComparer.Ordinal), CompactionRuns = [.. CompactionRuns],
        };
    }

    private sealed record BodyReference(long Offset, int Length, string Sha256);
    private sealed record CachedDocument(string Stamp, Document Document);
    private sealed record PersistedReceipt(ContinualToolReceipt Receipt, IReadOnlyList<KeyValuePair<string, BodyReference>> References);
}
