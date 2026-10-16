using System.Collections.Concurrent;

namespace SwarmCore.Agent.Continual;

/// <summary>
/// Process-local reference implementation of the store contract. It is useful
/// for tests and ephemeral hosts, but is not restart durable and is therefore
/// never registered by <c>AddSwarmCoreContinualAgent</c>.
/// </summary>
public sealed class InMemoryContinualObjectiveStore : IContinualObjectiveStore
{
    public bool SupportsRetainedVerificationCandidates => true;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<Guid, ContinualObjectiveSnapshot> _rows = new();
    private readonly Dictionary<ObjectiveIdentity, Guid> _identity = new();
    private readonly TimeProvider _time;

    public InMemoryContinualObjectiveStore(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
    }

    public Task<ContinualObjectiveSnapshot> CreateOrJoinAsync(
        ContinualObjectiveStart start,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        ct.ThrowIfCancellationRequested();
        Validate(start);

        lock (_gate)
        {
            var key = ObjectiveIdentity.From(start);
            if (_identity.TryGetValue(key, out var existingId))
            {
                var existing = _rows[existingId];
                if (!string.Equals(existing.ConversationId, start.ConversationId, StringComparison.Ordinal)
                    || !string.Equals(existing.ExecutorKey, start.ExecutorKey, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Continual objective idempotency key '{start.IdempotencyKey}' was reused for another conversation or executor.");
                }
                return Task.FromResult(existing);
            }

            if (_rows.ContainsKey(start.Id))
                throw new InvalidOperationException($"Continual objective id '{start.Id}' already exists under another trigger key.");

            var created = new ContinualObjectiveSnapshot
            {
                Id = start.Id,
                Surface = start.Surface.Trim(),
                PartitionKey = start.PartitionKey.Trim(),
                IdempotencyKey = start.IdempotencyKey.Trim(),
                ConversationId = start.ConversationId.Trim(),
                ExecutorKey = start.ExecutorKey.Trim(),
                ExecutorPayloadJson = start.ExecutorPayloadJson,
                Mission = start.Mission,
                Checkpoint = start.Checkpoint,
                InitialTurnId = start.InitialTurnId,
                LatestTurnId = start.InitialTurnId,
                ObjectiveText = start.ObjectiveText,
                ObjectiveSummary = start.ObjectiveSummary,
                Authority = start.Authority,
                Status = ContinualObjectiveStatus.InProgress,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc,
                LastProgressAtUtc = nowUtc,
                AttemptCount = 0,
                Provider = start.Provider,
                Model = start.Model,
                Version = 0,
            };
            _rows[start.Id] = created;
            _identity[key] = start.Id;
            return Task.FromResult(created);
        }
    }

    public Task<ContinualObjectiveSnapshot?> GetAsync(
        Guid objectiveId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_rows.GetValueOrDefault(objectiveId));
    }

    public Task<ContinualObjectiveSnapshot?> GetLatestForConversationAsync(
        string partitionKey,
        string conversationId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var row = _rows.Values
                .Where(o => string.Equals(o.PartitionKey, partitionKey, StringComparison.Ordinal)
                            && string.Equals(o.ConversationId, conversationId, StringComparison.Ordinal))
                .OrderByDescending(o => o.CreatedAtUtc)
                .ThenByDescending(o => o.UpdatedAtUtc)
                .FirstOrDefault();
            return Task.FromResult(row);
        }
    }

    public Task<IReadOnlyList<ContinualObjectiveSnapshot>> GetDueAsync(
        DateTimeOffset nowUtc,
        int take,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (take <= 0) throw new ArgumentOutOfRangeException(nameof(take));
        lock (_gate)
        {
            IReadOnlyList<ContinualObjectiveSnapshot> due = _rows.Values
                .Where(row => IsDue(row, nowUtc))
                .OrderBy(row => row.NextAttemptAtUtc ?? row.LeaseExpiresAtUtc ?? row.UpdatedAtUtc)
                .Take(take)
                .ToArray();
            return Task.FromResult(due);
        }
    }

    public Task<ContinualObjectiveLease?> TryClaimAsync(
        Guid objectiveId,
        long expectedVersion,
        string ownerId,
        DateTimeOffset nowUtc,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException("A lease owner is required.", nameof(ownerId));
        if (leaseExpiresAtUtc <= nowUtc) throw new ArgumentOutOfRangeException(nameof(leaseExpiresAtUtc));

        lock (_gate)
        {
            if (!_rows.TryGetValue(objectiveId, out var row)
                || row.Version != expectedVersion
                || !IsDue(row, nowUtc)
                || row.LeaseExpiresAtUtc > nowUtc)
            {
                return Task.FromResult<ContinualObjectiveLease?>(null);
            }

            var token = Guid.NewGuid().ToString("N");
            var version = row.Version + 1;
            var claimed = row with
            {
                Status = ContinualObjectiveStatus.InProgress,
                AttemptCount = row.AttemptCount + 1,
                UpdatedAtUtc = nowUtc,
                NextAttemptAtUtc = null,
                CurrentToolName = null,
                LeaseOwner = ownerId.Trim(),
                LeaseToken = token,
                LeaseExpiresAtUtc = leaseExpiresAtUtc,
                Version = version,
            };
            _rows[objectiveId] = claimed;
            return Task.FromResult<ContinualObjectiveLease?>(new(
                objectiveId,
                claimed.LeaseOwner!,
                token,
                nowUtc,
                leaseExpiresAtUtc,
                version));
        }
    }

    public Task<bool> HeartbeatAsync(
        Guid objectiveId,
        string leaseToken,
        DateTimeOffset nowUtc,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (leaseExpiresAtUtc <= nowUtc) throw new ArgumentOutOfRangeException(nameof(leaseExpiresAtUtc));
        lock (_gate)
        {
            if (!TryGetLeased(objectiveId, leaseToken, nowUtc, out var row))
                return Task.FromResult(false);

            _rows[objectiveId] = row with { LeaseExpiresAtUtc = leaseExpiresAtUtc };
            return Task.FromResult(true);
        }
    }

    public Task<bool> UpsertReceiptAsync(
        Guid objectiveId,
        string leaseToken,
        ContinualToolReceipt receipt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var at = receipt.CompletedAtUtc ?? receipt.StartedAtUtc;
            if (receipt.ObjectiveId != objectiveId
                || !TryGetLeased(objectiveId, leaseToken, _time.GetUtcNow(), out var row)
                || receipt.Sequence < row.LastEventSequence)
            {
                return Task.FromResult(false);
            }

            var receipts = row.Receipts.ToList();
            var index = receipts.FindIndex(saved =>
                saved.Attempt == receipt.Attempt
                && string.Equals(saved.ToolCallId, receipt.ToolCallId, StringComparison.Ordinal));
            if (index >= 0)
            {
                if (receipt.Sequence < receipts[index].Sequence) return Task.FromResult(false);
                receipts[index] = receipt;
            }
            else
            {
                receipts.Add(receipt);
            }

            _rows[objectiveId] = row with
            {
                Receipts = receipts.ToArray(),
                // A receipt the engine marks as not physically dispatched (a guard or binding refusal) had no effect.
                HasPossibleSideEffects = row.HasPossibleSideEffects
                    || receipt.WasDispatched && receipt.SideEffect != ContinualSideEffectClass.ReadOnly,
                CurrentToolName = receipt.State == ContinualToolReceiptState.Started
                    ? receipt.ToolName
                    : null,
                LastFailure = receipt.IsError ? receipt.Result : row.LastFailure,
                UpdatedAtUtc = at,
                LastProgressAtUtc = at,
                LastEventSequence = receipt.Sequence,
            };
            return Task.FromResult(true);
        }
    }

    public Task<bool> RecordProgressAsync(
        Guid objectiveId,
        string leaseToken,
        long sequence,
        string? toolName,
        string status,
        DateTimeOffset atUtc,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!TryGetLeased(objectiveId, leaseToken, _time.GetUtcNow(), out var row)
                || sequence <= row.LastEventSequence)
            {
                return Task.FromResult(false);
            }

            _rows[objectiveId] = row with
            {
                CurrentToolName = toolName,
                ContinuationFeedback = status,
                UpdatedAtUtc = atUtc,
                LastProgressAtUtc = atUtc,
                LastEventSequence = sequence,
            };
            return Task.FromResult(true);
        }
    }

    public Task<bool> ApplyTransitionAsync(
        Guid objectiveId,
        string leaseToken,
        ContinualObjectiveTransition transition,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transition);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!TryGetLeased(objectiveId, leaseToken, _time.GetUtcNow(), out var row))
                return Task.FromResult(false);
            if (transition.RetainVerificationCandidate
                && (transition.Status != ContinualObjectiveStatus.Uncertain
                    || transition.NextAttemptAtUtc is not null
                    || transition.ExpectedVerificationCandidate is null
                    || !ContinualVerificationCandidatePolicy.Matches(row, row.LastOutput ?? string.Empty)
                    || !string.Equals(transition.LastOutput, row.LastOutput, StringComparison.Ordinal)))
                return Task.FromResult(false);
            if (transition.RetainedHoldKind is not null && !ContinualLocalResultHoldPolicy.IsTransition(row, transition))
                return Task.FromResult(false);
            if (transition.ExpectedVerificationCandidate is { } expected
                && !ContinualVerificationCandidatePolicy.MatchesExpectation(row, expected)) return Task.FromResult(false);
            if (transition.Status == ContinualObjectiveStatus.Completed
                && MissionVerification.ValidateCompletion(row, transition.Verification, transition.LastOutput) is not null)
                return Task.FromResult(false);

            _rows[objectiveId] = row with
            {
                Status = transition.Status,
                VerificationHistory = transition.Verification is { } verification ? [.. row.VerificationHistory, verification] : row.VerificationHistory,
                VerificationCandidatePending = !transition.ClearVerificationCandidate && (transition.RetainVerificationCandidate
                    || transition.Status == ContinualObjectiveStatus.VerificationPending),
                VerificationCandidate = transition.ClearVerificationCandidate ? null : row.VerificationCandidate,
                HasPossibleSideEffects = !transition.ClearVerificationCandidate && row.HasPossibleSideEffects,
                UpdatedAtUtc = transition.UpdatedAtUtc,
                LastProgressAtUtc = transition.LastProgressAtUtc,
                NextAttemptAtUtc = transition.NextAttemptAtUtc,
                LastOutput = transition.LastOutput,
                LastFailure = transition.LastFailure,
                LastOutputFingerprint = transition.LastOutputFingerprint,
                ContinuationFeedback = transition.ContinuationFeedback,
                ConsecutiveNoProgress = transition.ConsecutiveNoProgress,
                CurrentToolName = transition.ClearCurrentTool ? null : row.CurrentToolName,
                LeaseOwner = transition.ReleaseLease ? null : row.LeaseOwner,
                LeaseToken = transition.ReleaseLease ? null : row.LeaseToken,
                LeaseExpiresAtUtc = transition.ReleaseLease ? null : row.LeaseExpiresAtUtc,
                Version = row.Version + 1,
            };
            return Task.FromResult(true);
        }
    }

    public Task<bool> SaveVerificationCandidateAsync(Guid id, string leaseToken, string output, DateTimeOffset atUtc, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!TryGetLeased(id, leaseToken, _time.GetUtcNow(), out var row)) return Task.FromResult(false);
            _rows[id] = row with { LastOutput = output, VerificationCandidatePending = true,
                VerificationCandidate = ContinualVerificationCandidatePolicy.Create(row, output, atUtc),
                UpdatedAtUtc = atUtc, Version = row.Version + 1 };
            return Task.FromResult(true);
        }
    }

    public Task<bool> TryStopAsync(
        Guid objectiveId,
        long expectedVersion,
        string reason,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_rows.TryGetValue(objectiveId, out var row)
                || row.Version != expectedVersion
                || ContinualObjectiveStateMachine.IsTerminal(row.Status))
            {
                return Task.FromResult(false);
            }

            _rows[objectiveId] = row with
            {
                Status = ContinualObjectiveStatus.Idle,
                UpdatedAtUtc = nowUtc,
                NextAttemptAtUtc = null,
                CurrentToolName = null,
                ContinuationFeedback = reason,
                LeaseOwner = null,
                LeaseToken = null,
                LeaseExpiresAtUtc = null,
                Version = row.Version + 1,
            };
            return Task.FromResult(true);
        }
    }

    private bool TryGetLeased(
        Guid objectiveId,
        string leaseToken,
        DateTimeOffset atUtc,
        out ContinualObjectiveSnapshot row)
    {
        if (_rows.TryGetValue(objectiveId, out row!)
            && !string.IsNullOrEmpty(leaseToken)
            && string.Equals(row.LeaseToken, leaseToken, StringComparison.Ordinal)
            && row.LeaseExpiresAtUtc > atUtc
            && !ContinualObjectiveStateMachine.IsTerminal(row.Status))
        {
            return true;
        }
        row = null!;
        return false;
    }

    private static bool IsDue(ContinualObjectiveSnapshot row, DateTimeOffset nowUtc)
        => row.Status switch
        {
            ContinualObjectiveStatus.RetryScheduled
                or ContinualObjectiveStatus.VerificationPending
                or ContinualObjectiveStatus.AttentionRequired
                => (row.NextAttemptAtUtc is null || row.NextAttemptAtUtc <= nowUtc)
                   && (row.LeaseExpiresAtUtc is null || row.LeaseExpiresAtUtc <= nowUtc),
            ContinualObjectiveStatus.InProgress
                => row.LeaseExpiresAtUtc is null || row.LeaseExpiresAtUtc <= nowUtc,
            _ => false,
        };

    private static void Validate(ContinualObjectiveStart start)
    {
        if (start.Id == Guid.Empty) throw new ArgumentException("Objective id is required.", nameof(start));
        Require(start.Surface, nameof(start.Surface));
        Require(start.PartitionKey, nameof(start.PartitionKey));
        Require(start.IdempotencyKey, nameof(start.IdempotencyKey));
        Require(start.ConversationId, nameof(start.ConversationId));
        Require(start.ExecutorKey, nameof(start.ExecutorKey));
        Require(start.ObjectiveText, nameof(start.ObjectiveText));
        Require(start.ObjectiveSummary, nameof(start.ObjectiveSummary));
        ArgumentNullException.ThrowIfNull(start.Authority);
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required.", name);
    }

    private readonly record struct ObjectiveIdentity(
        string PartitionKey,
        string Surface,
        string IdempotencyKey)
    {
        public static ObjectiveIdentity From(ContinualObjectiveStart start)
            => new(start.PartitionKey.Trim(), start.Surface.Trim(), start.IdempotencyKey.Trim());
    }
}
