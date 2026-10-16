namespace SwarmCore.PersonalMemory;

/// <summary>
/// Reference implementation of <see cref="IPersonalMemoryStore"/>: thread-safe, scope-isolated,
/// version-checked, all-or-nothing commits. Used by tests, by hosts without durable storage, and as
/// the executable specification of the writer contract that database-backed stores must match
/// (see <see cref="Testing.PersonalMemoryStoreConformance"/>).
/// </summary>
public sealed class InMemoryPersonalMemoryStore : IPersonalMemoryStore
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, MemoryClaim> _claims = [];
    private readonly Dictionary<Guid, MemoryFact> _facts = [];
    private readonly List<MemorySuppression> _suppressions = [];
    private readonly TimeProvider _time;

    public InMemoryPersonalMemoryStore(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Number of successful commits (tests use this to prove idempotency).</summary>
    public int CommitCount { get; private set; }

    public Task<MemoryClaimAppendResult> AppendClaimAsync(MemoryClaim claim, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (claim.Scope.IsEmpty) throw new ArgumentException("Claim scope is required.", nameof(claim));
        lock (_sync)
        {
            var existing = _claims.Values.FirstOrDefault(c => c.Scope == claim.Scope && c.IdempotencyKey == claim.IdempotencyKey);
            if (existing is not null) return Task.FromResult(new MemoryClaimAppendResult(existing, false));
            if (_claims.ContainsKey(claim.Id)) throw new InvalidOperationException("Claim id already exists.");
            var now = _time.GetUtcNow();
            var stored = claim with
            {
                Version = 1,
                CreatedAt = claim.CreatedAt == default ? now : claim.CreatedAt,
                UpdatedAt = now,
            };
            _claims[stored.Id] = stored;
            return Task.FromResult(new MemoryClaimAppendResult(stored, true));
        }
    }

    public Task<IReadOnlyList<MemoryClaim>> GetClaimsAsync(MemoryScope scope, IReadOnlyCollection<MemoryClaimStatus> statuses, int maxCount, CancellationToken ct = default)
    {
        lock (_sync)
        {
            IReadOnlyList<MemoryClaim> result = _claims.Values
                .Where(c => c.Scope == scope && statuses.Contains(c.Status))
                .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
                .Take(Math.Max(0, maxCount))
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<MemoryClaim?> GetClaimAsync(MemoryScope scope, Guid claimId, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult(_claims.TryGetValue(claimId, out var c) && c.Scope == scope ? c : null);
    }

    public Task<IReadOnlyList<MemoryFact>> GetFactsAsync(MemoryScope scope, MemoryFactQuery query, CancellationToken ct = default)
    {
        var asOf = query.AsOf ?? _time.GetUtcNow();
        lock (_sync)
        {
            IReadOnlyList<MemoryFact> result = _facts.Values
                .Where(f => f.Scope == scope)
                .Where(f => !query.CurrentOnly || (f.SupersededById is null && (f.ValidUntil is null || f.ValidUntil > asOf)))
                .Where(f => query.IncludeSuppressed || !f.Suppressed)
                .Where(f => query.Categories is null || query.Categories.Contains(f.Category))
                .Where(f => query.Key is null || f.Key == query.Key)
                .OrderBy(f => f.CreatedAt).ThenBy(f => f.Id)
                .Take(Math.Max(0, query.MaxCount))
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<MemoryFact?> GetFactAsync(MemoryScope scope, Guid factId, CancellationToken ct = default)
    {
        lock (_sync)
            return Task.FromResult(_facts.TryGetValue(factId, out var f) && f.Scope == scope ? f : null);
    }

    public Task<IReadOnlyList<MemorySuppression>> GetSuppressionsAsync(MemoryScope scope, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        lock (_sync)
        {
            IReadOnlyList<MemorySuppression> result = _suppressions
                .Where(s => s.Scope == scope && (s.ExpiresAt is null || s.ExpiresAt > now))
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<MemoryScope>> GetScopesWithPendingClaimsAsync(int maxCount, CancellationToken ct = default)
    {
        lock (_sync)
        {
            IReadOnlyList<MemoryScope> result = _claims.Values
                .Where(c => c.Status == MemoryClaimStatus.Pending)
                .Select(c => c.Scope)
                .Distinct()
                .OrderBy(s => s.Key, StringComparer.Ordinal)
                .Take(Math.Max(0, maxCount))
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<MemoryScope>> GetScopesWithDueFactsAsync(MemoryDueQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (_sync)
        {
            IReadOnlyList<MemoryScope> result = _facts.Values
                .Where(query.IsDue)
                .Select(f => f.Scope)
                .Distinct()
                .OrderBy(s => s.Key, StringComparer.Ordinal)
                .Take(Math.Max(0, query.MaxCount))
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<MemoryCommitResult> CommitAsync(MemoryScope scope, MemoryChangeSet changes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ct.ThrowIfCancellationRequested();
        var now = _time.GetUtcNow();
        lock (_sync)
        {
            // Stage on copies; publish only if every change validates.
            var facts = new Dictionary<Guid, MemoryFact>(_facts);
            var claims = new Dictionary<Guid, MemoryClaim>(_claims);
            var suppressions = new List<MemorySuppression>();
            foreach (var change in changes.Changes)
            {
                var error = Apply(scope, change, facts, claims, suppressions, now);
                if (error is not null) return Task.FromResult(error);
            }
            _facts.Clear();
            foreach (var kv in facts) _facts[kv.Key] = kv.Value;
            _claims.Clear();
            foreach (var kv in claims) _claims[kv.Key] = kv.Value;
            _suppressions.AddRange(suppressions);
            CommitCount++;
            return Task.FromResult(MemoryCommitResult.Ok);
        }
    }

    private static MemoryCommitResult? Apply(
        MemoryScope scope,
        MemoryChange change,
        Dictionary<Guid, MemoryFact> facts,
        Dictionary<Guid, MemoryClaim> claims,
        List<MemorySuppression> suppressions,
        DateTimeOffset now)
    {
        switch (change)
        {
            case MemoryFactInsert insert:
                if (insert.Fact.Scope != scope) return new(MemoryCommitStatus.ScopeMismatch, "fact scope");
                if (insert.Fact.Version != 0 || facts.ContainsKey(insert.Fact.Id)) return new(MemoryCommitStatus.Invalid, "fact exists");
                if (string.IsNullOrWhiteSpace(insert.Fact.Text) || string.IsNullOrWhiteSpace(insert.Fact.Key)) return new(MemoryCommitStatus.Invalid, "fact text/key");
                facts[insert.Fact.Id] = insert.Fact with
                {
                    Version = 1,
                    CreatedAt = insert.Fact.CreatedAt == default ? now : insert.Fact.CreatedAt,
                    UpdatedAt = now,
                };
                return null;

            case MemoryFactUpdate update:
            {
                if (!facts.TryGetValue(update.FactId, out var fact)) return new(MemoryCommitStatus.NotFound, "fact");
                if (fact.Scope != scope) return new(MemoryCommitStatus.ScopeMismatch, "fact scope");
                if (fact.Version != update.ExpectedVersion) return new(MemoryCommitStatus.VersionConflict, "fact version");
                facts[fact.Id] = ApplyPatch(fact, update.Patch, now);
                return null;
            }

            case MemoryFactDelete delete:
            {
                if (!facts.TryGetValue(delete.FactId, out var fact)) return new(MemoryCommitStatus.NotFound, "fact");
                if (fact.Scope != scope) return new(MemoryCommitStatus.ScopeMismatch, "fact scope");
                if (fact.Version != delete.ExpectedVersion) return new(MemoryCommitStatus.VersionConflict, "fact version");
                facts.Remove(fact.Id);
                return null;
            }

            case MemoryClaimTransition transition:
            {
                if (!claims.TryGetValue(transition.ClaimId, out var claim)) return new(MemoryCommitStatus.NotFound, "claim");
                if (claim.Scope != scope) return new(MemoryCommitStatus.ScopeMismatch, "claim scope");
                if (claim.Version != transition.ExpectedVersion) return new(MemoryCommitStatus.VersionConflict, "claim version");
                if (transition.ResultFactId is { } fid && (!facts.TryGetValue(fid, out var result) || result.Scope != scope))
                    return new(MemoryCommitStatus.Invalid, "result fact missing");
                claims[claim.Id] = claim with
                {
                    Status = transition.Status,
                    ResultFactId = transition.ResultFactId,
                    StatusReason = transition.Reason,
                    UpdatedAt = now,
                    Version = claim.Version + 1,
                };
                return null;
            }

            case MemoryClaimDelete deleteClaim:
            {
                if (!claims.TryGetValue(deleteClaim.ClaimId, out var claim)) return new(MemoryCommitStatus.NotFound, "claim");
                if (claim.Scope != scope) return new(MemoryCommitStatus.ScopeMismatch, "claim scope");
                if (claim.Version != deleteClaim.ExpectedVersion) return new(MemoryCommitStatus.VersionConflict, "claim version");
                claims.Remove(claim.Id);
                return null;
            }

            case MemorySuppressionInsert suppression:
                if (suppression.Suppression.Scope != scope) return new(MemoryCommitStatus.ScopeMismatch, "suppression scope");
                suppressions.Add(suppression.Suppression with { CreatedAt = suppression.Suppression.CreatedAt == default ? now : suppression.Suppression.CreatedAt });
                return null;

            default:
                return new(MemoryCommitStatus.Invalid, "unknown change");
        }
    }

    /// <summary>Applies only the fields the patch owns; increments the version by one.</summary>
    public static MemoryFact ApplyPatch(MemoryFact fact, MemoryFactPatch patch, DateTimeOffset now)
    {
        var evidence = fact.Evidence.ToList();
        var known = evidence.Select(e => e.IdentityKey).ToHashSet(StringComparer.Ordinal);
        evidence.AddRange(patch.AddEvidence.Where(e => known.Add(e.IdentityKey)));
        return fact with
        {
            Text = patch.Text.IsSet ? patch.Text.Value : fact.Text,
            Category = patch.Category.IsSet ? patch.Category.Value : fact.Category,
            Authority = patch.Authority.IsSet ? patch.Authority.Value : fact.Authority,
            Confidence = patch.Confidence.IsSet ? patch.Confidence.Value : fact.Confidence,
            ValidUntil = patch.ValidUntil.IsSet ? patch.ValidUntil.Value : fact.ValidUntil,
            StaleAfter = patch.StaleAfter.IsSet ? patch.StaleAfter.Value : fact.StaleAfter,
            EventAt = patch.EventAt.IsSet ? patch.EventAt.Value : fact.EventAt,
            SupersededById = patch.SupersededById.IsSet ? patch.SupersededById.Value : fact.SupersededById,
            Suppressed = patch.Suppressed.IsSet ? patch.Suppressed.Value : fact.Suppressed,
            UserConfirmed = patch.UserConfirmed.IsSet ? patch.UserConfirmed.Value : fact.UserConfirmed,
            Sensitive = patch.Sensitive.IsSet ? patch.Sensitive.Value : fact.Sensitive,
            Evidence = evidence,
            UpdatedAt = now,
            Version = fact.Version + 1,
        };
    }
}
