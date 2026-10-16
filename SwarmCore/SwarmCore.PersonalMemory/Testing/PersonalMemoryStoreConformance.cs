namespace SwarmCore.PersonalMemory.Testing;

/// <summary>
/// Framework-free conformance checks for <see cref="IPersonalMemoryStore"/> implementations. Host
/// test projects call <see cref="RunAsync"/> against a fresh store and assert the returned failure
/// list is empty. Covers: idempotent append, scope isolation, explicit-field patches (including
/// intentional null/false), version conflicts, all-or-nothing commits, competing writers, and
/// delete-then-save interleavings.
/// </summary>
public static class PersonalMemoryStoreConformance
{
    public static async Task<IReadOnlyList<string>> RunAsync(Func<IPersonalMemoryStore> createStore, CancellationToken ct = default)
    {
        var failures = new List<string>();
        await Check("idempotent append", IdempotentAppend);
        await Check("scope isolation", ScopeIsolation);
        await Check("explicit-field patch", ExplicitFieldPatch);
        await Check("version conflict is atomic", AtomicConflict);
        await Check("competing writers", CompetingWriters);
        await Check("delete then save", DeleteThenSave);
        await Check("cross-scope write rejected", CrossScopeWrite);
        return failures;

        async Task Check(string name, Func<IPersonalMemoryStore, CancellationToken, Task<string?>> test)
        {
            try
            {
                var error = await test(createStore(), ct).ConfigureAwait(false);
                if (error is not null) failures.Add($"{name}: {error}");
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: threw {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static readonly MemoryScope A = new("tenant-a", "subject-1", "conformance");
    private static readonly MemoryScope B = new("tenant-b", "subject-1", "conformance");

    private static MemoryClaim Claim(MemoryScope scope, string key) => new()
    {
        Scope = scope,
        IdempotencyKey = key,
        Category = MemoryCategory.Preference,
        Text = "User prefers tea over coffee.",
        Authority = MemorySourceAuthority.UserAuthored,
        Method = MemoryCaptureMethod.ExplicitRemember,
        Evidence = new MemoryEvidence { SourceKind = "test", SourceId = key, Authority = MemorySourceAuthority.UserAuthored, ObservedAt = DateTimeOffset.UtcNow },
        ObservedAt = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static MemoryFact Fact(MemoryScope scope) => new()
    {
        Scope = scope,
        Key = "drink",
        Category = MemoryCategory.Preference,
        Text = "User prefers tea over coffee.",
        Authority = MemorySourceAuthority.UserAuthored,
        StaleAfter = DateTimeOffset.UtcNow.AddDays(30),
        UserConfirmed = true,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task<string?> IdempotentAppend(IPersonalMemoryStore store, CancellationToken ct)
    {
        var first = await store.AppendClaimAsync(Claim(A, "k1"), ct);
        var second = await store.AppendClaimAsync(Claim(A, "k1"), ct);
        if (!first.Created || second.Created) return "second append with the same idempotency key must not create";
        if (second.Claim.Id != first.Claim.Id) return "repeat append must return the stored claim";
        var pending = await store.GetClaimsAsync(A, [MemoryClaimStatus.Pending], 10, ct);
        return pending.Count == 1 ? null : $"expected 1 pending claim, found {pending.Count}";
    }

    private static async Task<string?> ScopeIsolation(IPersonalMemoryStore store, CancellationToken ct)
    {
        await store.AppendClaimAsync(Claim(A, "k1"), ct);
        var fact = Fact(A);
        var commit = await store.CommitAsync(A, MemoryChangeSet.Of(new MemoryFactInsert(fact)), ct);
        if (!commit.Succeeded) return "insert failed: " + commit.Status;
        if ((await store.GetFactsAsync(B, MemoryFactQuery.All, ct)).Count != 0) return "scope B sees scope A facts";
        if (await store.GetFactAsync(B, fact.Id, ct) is not null) return "scope B can read scope A fact by id";
        if ((await store.GetClaimsAsync(B, [MemoryClaimStatus.Pending], 10, ct)).Count != 0) return "scope B sees scope A claims";
        return null;
    }

    private static async Task<string?> ExplicitFieldPatch(IPersonalMemoryStore store, CancellationToken ct)
    {
        var fact = Fact(A);
        await store.CommitAsync(A, MemoryChangeSet.Of(new MemoryFactInsert(fact)), ct);
        var stored = (await store.GetFactAsync(A, fact.Id, ct))!;
        var commit = await store.CommitAsync(A, MemoryChangeSet.Of(new MemoryFactUpdate(fact.Id, stored.Version, new MemoryFactPatch
        {
            StaleAfter = MemoryField<DateTimeOffset?>.Set(null),
            UserConfirmed = MemoryField<bool>.Set(false),
        })), ct);
        if (!commit.Succeeded) return "patch failed: " + commit.Status;
        var after = (await store.GetFactAsync(A, fact.Id, ct))!;
        if (after.StaleAfter is not null) return "explicit null was not persisted";
        if (after.UserConfirmed) return "explicit false was not persisted";
        if (after.Text != stored.Text || after.Category != stored.Category) return "unset fields changed";
        return after.Version == stored.Version + 1 ? null : "version must increment by exactly one";
    }

    private static async Task<string?> AtomicConflict(IPersonalMemoryStore store, CancellationToken ct)
    {
        var claim = (await store.AppendClaimAsync(Claim(A, "k1"), ct)).Claim;
        var fact = Fact(A);
        await store.CommitAsync(A, MemoryChangeSet.Of(new MemoryFactInsert(fact)), ct);
        var stored = (await store.GetFactAsync(A, fact.Id, ct))!;
        var newFact = Fact(A) with { Key = "drink-2", Text = "User prefers green tea." };
        var result = await store.CommitAsync(A, MemoryChangeSet.Of(
            new MemoryFactInsert(newFact),
            new MemoryClaimTransition(claim.Id, claim.Version, MemoryClaimStatus.Applied, newFact.Id, "x"),
            new MemoryFactUpdate(fact.Id, stored.Version + 7, new MemoryFactPatch { Suppressed = MemoryField<bool>.Set(true) })), ct);
        if (result.Status != MemoryCommitStatus.VersionConflict) return "stale version must be a VersionConflict, got " + result.Status;
        if (await store.GetFactAsync(A, newFact.Id, ct) is not null) return "insert from a failed change set was persisted";
        var claimAfter = await store.GetClaimAsync(A, claim.Id, ct);
        return claimAfter?.Status == MemoryClaimStatus.Pending ? null : "claim transition from a failed change set was persisted";
    }

    private static async Task<string?> CompetingWriters(IPersonalMemoryStore store, CancellationToken ct)
    {
        var fact = Fact(A);
        await store.CommitAsync(A, MemoryChangeSet.Of(new MemoryFactInsert(fact)), ct);
        var read = (await store.GetFactAsync(A, fact.Id, ct))!;
        var first = await store.CommitAsync(A, MemoryChangeSet.Of(new MemoryFactUpdate(fact.Id, read.Version,
            new MemoryFactPatch { Text = MemoryField<string>.Set("User prefers oolong tea.") })), ct);
        var second = await store.CommitAsync(A, MemoryChangeSet.Of(new MemoryFactUpdate(fact.Id, read.Version,
            new MemoryFactPatch { Text = MemoryField<string>.Set("User prefers black coffee.") })), ct);
        if (!first.Succeeded) return "first writer failed";
        if (second.Status != MemoryCommitStatus.VersionConflict) return "second writer with the same baseline must conflict";
        var after = (await store.GetFactAsync(A, fact.Id, ct))!;
        return after.Text == "User prefers oolong tea." ? null : "losing writer overwrote the row";
    }

    private static async Task<string?> DeleteThenSave(IPersonalMemoryStore store, CancellationToken ct)
    {
        var fact = Fact(A);
        await store.CommitAsync(A, MemoryChangeSet.Of(new MemoryFactInsert(fact)), ct);
        var read = (await store.GetFactAsync(A, fact.Id, ct))!;
        var delete = await store.CommitAsync(A, MemoryChangeSet.Of(new MemoryFactDelete(fact.Id, read.Version)), ct);
        if (!delete.Succeeded) return "delete failed";
        var save = await store.CommitAsync(A, MemoryChangeSet.Of(new MemoryFactUpdate(fact.Id, read.Version,
            new MemoryFactPatch { Suppressed = MemoryField<bool>.Set(true) })), ct);
        if (save.Succeeded) return "an update after delete must not resurrect the row";
        return await store.GetFactAsync(A, fact.Id, ct) is null ? null : "deleted fact reappeared";
    }

    private static async Task<string?> CrossScopeWrite(IPersonalMemoryStore store, CancellationToken ct)
    {
        var fact = Fact(A);
        await store.CommitAsync(A, MemoryChangeSet.Of(new MemoryFactInsert(fact)), ct);
        var read = (await store.GetFactAsync(A, fact.Id, ct))!;
        var update = await store.CommitAsync(B, MemoryChangeSet.Of(new MemoryFactUpdate(fact.Id, read.Version,
            new MemoryFactPatch { Text = MemoryField<string>.Set("Injected.") })), ct);
        if (update.Succeeded) return "a change set for scope B modified a scope A fact";
        var insert = await store.CommitAsync(B, MemoryChangeSet.Of(new MemoryFactInsert(Fact(A))), ct);
        if (insert.Succeeded) return "a change set for scope B inserted a scope A fact";
        return (await store.GetFactAsync(A, fact.Id, ct))!.Text == fact.Text ? null : "scope A fact changed";
    }
}
