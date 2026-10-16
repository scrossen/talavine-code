using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using SwarmCore.PersonalMemory;

namespace SwarmCode.Hosting.Memory;

/// <summary>
/// SwarmCode's durable <see cref="IPersonalMemoryStore"/>: one JSON document under SWARMCODE_HOME
/// (<see cref="SwarmCodeDataPaths.PersonalMemoryPath"/>) holding claims, facts, forget rules and a bounded audit journal.
/// <list type="bullet">
/// <item>Every write is version-checked and all-or-nothing: the change set is staged on copies, the whole document is written
/// to a temporary file, flushed, and atomically renamed over the old one. Rows and journal land together or not at all.</item>
/// <item>Writers in this process serialize on a per-path gate; writers in other processes (the console, the desktop app, a
/// second window) serialize on an exclusive lock file and the cache reloads whenever the file changed underneath.</item>
/// <item>An unreadable document is never overwritten: reads and writes fail closed until the person repairs or removes it.</item>
/// </list>
/// Pure managed code; identical on Windows x64/ARM64 and macOS.
/// </summary>
public sealed class FilePersonalMemoryStore : IPersonalMemoryStore
{
    public const int SchemaVersion = 1;
    private const int MaxJournalEntries = 2_000;
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(15);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessGates = new(
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    internal static readonly JsonSerializerOptions Json = CreateJson();

    private readonly string _path;
    private readonly string _lockPath;
    private readonly SemaphoreSlim _gate;
    private readonly TimeProvider _time;
    private State? _state;
    private (long Ticks, long Length)? _stamp;

    public FilePersonalMemoryStore(string path, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _lockPath = _path + ".lock";
        _time = time ?? TimeProvider.System;
        _gate = ProcessGates.GetOrAdd(_path, _ => new SemaphoreSlim(1, 1));
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public FilePersonalMemoryStore(SwarmCodeDataPaths paths, TimeProvider? time = null) : this(paths.PersonalMemoryPath, time) { }

    public string FilePath => _path;

    // ---------------- reads ----------------

    public Task<MemoryClaim?> GetClaimAsync(MemoryScope scope, Guid claimId, CancellationToken ct = default)
        => ReadAsync(s => s.Claims.TryGetValue(claimId, out var c) && c.Scope == scope ? c : null, ct);

    public Task<IReadOnlyList<MemoryClaim>> GetClaimsAsync(MemoryScope scope, IReadOnlyCollection<MemoryClaimStatus> statuses, int maxCount, CancellationToken ct = default)
        => ReadAsync<IReadOnlyList<MemoryClaim>>(s => s.Claims.Values
            .Where(c => c.Scope == scope && statuses.Contains(c.Status))
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .Take(Math.Max(0, maxCount))
            .ToList(), ct);

    public Task<MemoryFact?> GetFactAsync(MemoryScope scope, Guid factId, CancellationToken ct = default)
        => ReadAsync(s => s.Facts.TryGetValue(factId, out var f) && f.Scope == scope ? f : null, ct);

    public Task<IReadOnlyList<MemoryFact>> GetFactsAsync(MemoryScope scope, MemoryFactQuery query, CancellationToken ct = default)
    {
        var asOf = query.AsOf ?? _time.GetUtcNow();
        return ReadAsync<IReadOnlyList<MemoryFact>>(s => s.Facts.Values
            .Where(f => f.Scope == scope)
            .Where(f => !query.CurrentOnly || (f.SupersededById is null && (f.ValidUntil is null || f.ValidUntil > asOf)))
            .Where(f => query.IncludeSuppressed || !f.Suppressed)
            .Where(f => query.Categories is null || query.Categories.Contains(f.Category))
            .Where(f => query.Key is null || f.Key == query.Key)
            .OrderBy(f => f.CreatedAt).ThenBy(f => f.Id)
            .Take(Math.Max(0, query.MaxCount))
            .ToList(), ct);
    }

    public Task<IReadOnlyList<MemorySuppression>> GetSuppressionsAsync(MemoryScope scope, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        return ReadAsync<IReadOnlyList<MemorySuppression>>(s => s.Suppressions
            .Where(x => x.Scope == scope && (x.ExpiresAt is null || x.ExpiresAt > now))
            .ToList(), ct);
    }

    public Task<IReadOnlyList<MemoryScope>> GetScopesWithPendingClaimsAsync(int maxCount, CancellationToken ct = default)
        => ReadAsync<IReadOnlyList<MemoryScope>>(s => s.Claims.Values
            .Where(c => c.Status == MemoryClaimStatus.Pending)
            .Select(c => c.Scope)
            .Distinct()
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Take(Math.Max(0, maxCount))
            .ToList(), ct);

    public Task<IReadOnlyList<MemoryScope>> GetScopesWithDueFactsAsync(MemoryDueQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return ReadAsync<IReadOnlyList<MemoryScope>>(s => s.Facts.Values
            .Where(query.IsDue)
            .Select(f => f.Scope)
            .Distinct()
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Take(Math.Max(0, query.MaxCount))
            .ToList(), ct);
    }

    /// <summary>Number of journal entries (tests and diagnostics).</summary>
    public Task<int> GetJournalCountAsync(CancellationToken ct = default) => ReadAsync(s => s.Journal.Count, ct);

    // ---------------- writes ----------------

    public async Task<MemoryClaimAppendResult> AppendClaimAsync(MemoryClaim claim, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (claim.Scope.IsEmpty) throw new ArgumentException("Claim scope is required.", nameof(claim));
        return await WriteAsync(state =>
        {
            var existing = state.Claims.Values.FirstOrDefault(c => c.Scope == claim.Scope && c.IdempotencyKey == claim.IdempotencyKey);
            if (existing is not null) return (new MemoryClaimAppendResult(existing, false), (State?)null);
            if (state.Claims.ContainsKey(claim.Id)) throw new InvalidOperationException("Claim id already exists.");
            var now = _time.GetUtcNow();
            var stored = claim with { Version = 1, CreatedAt = claim.CreatedAt == default ? now : claim.CreatedAt, UpdatedAt = now };
            var next = state.Copy();
            next.Claims[stored.Id] = stored;
            next.AddJournal(now, "claim.append", stored.Id, stored.Version);
            return (new MemoryClaimAppendResult(stored, true), next);
        }, ct).ConfigureAwait(false);
    }

    public async Task<MemoryCommitResult> CommitAsync(MemoryScope scope, MemoryChangeSet changes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (scope.IsEmpty) return new MemoryCommitResult(MemoryCommitStatus.ScopeMismatch, "empty scope");
        return await WriteAsync(state =>
        {
            var now = _time.GetUtcNow();
            var next = state.Copy();
            foreach (var change in changes.Changes)
            {
                var error = Apply(scope, change, next, now);
                if (error is not null) return (error, (State?)null);
            }
            return (MemoryCommitResult.Ok, next);
        }, ct).ConfigureAwait(false);
    }

    private static MemoryCommitResult? Apply(MemoryScope scope, MemoryChange change, State state, DateTimeOffset now)
    {
        switch (change)
        {
            case MemoryFactInsert insert:
                if (insert.Fact.Scope != scope) return new(MemoryCommitStatus.ScopeMismatch, "fact scope");
                if (insert.Fact.Version != 0 || state.Facts.ContainsKey(insert.Fact.Id)) return new(MemoryCommitStatus.Invalid, "fact exists");
                if (string.IsNullOrWhiteSpace(insert.Fact.Text) || string.IsNullOrWhiteSpace(insert.Fact.Key)) return new(MemoryCommitStatus.Invalid, "fact text/key");
                state.Facts[insert.Fact.Id] = insert.Fact with
                {
                    Version = 1,
                    CreatedAt = insert.Fact.CreatedAt == default ? now : insert.Fact.CreatedAt,
                    UpdatedAt = now,
                };
                state.AddJournal(now, "fact.insert", insert.Fact.Id, 1);
                return null;

            case MemoryFactUpdate update:
            {
                if (!state.Facts.TryGetValue(update.FactId, out var fact)) return new(MemoryCommitStatus.NotFound, "fact");
                if (fact.Scope != scope) return new(MemoryCommitStatus.ScopeMismatch, "fact scope");
                if (fact.Version != update.ExpectedVersion) return new(MemoryCommitStatus.VersionConflict, "fact version");
                var patched = InMemoryPersonalMemoryStore.ApplyPatch(fact, update.Patch, now);
                state.Facts[fact.Id] = patched;
                state.AddJournal(now, "fact.update", fact.Id, patched.Version);
                return null;
            }

            case MemoryFactDelete delete:
            {
                if (!state.Facts.TryGetValue(delete.FactId, out var fact)) return new(MemoryCommitStatus.NotFound, "fact");
                if (fact.Scope != scope) return new(MemoryCommitStatus.ScopeMismatch, "fact scope");
                if (fact.Version != delete.ExpectedVersion) return new(MemoryCommitStatus.VersionConflict, "fact version");
                state.Facts.Remove(fact.Id);
                state.AddJournal(now, "fact.delete", fact.Id, fact.Version);
                return null;
            }

            case MemoryClaimTransition transition:
            {
                if (!state.Claims.TryGetValue(transition.ClaimId, out var claim)) return new(MemoryCommitStatus.NotFound, "claim");
                if (claim.Scope != scope) return new(MemoryCommitStatus.ScopeMismatch, "claim scope");
                if (claim.Version != transition.ExpectedVersion) return new(MemoryCommitStatus.VersionConflict, "claim version");
                if (transition.ResultFactId is { } fid && (!state.Facts.TryGetValue(fid, out var result) || result.Scope != scope))
                    return new(MemoryCommitStatus.Invalid, "result fact missing");
                var moved = claim with
                {
                    Status = transition.Status,
                    ResultFactId = transition.ResultFactId,
                    StatusReason = transition.Reason,
                    UpdatedAt = now,
                    Version = claim.Version + 1,
                };
                state.Claims[claim.Id] = moved;
                state.AddJournal(now, "claim." + transition.Status.ToString().ToLowerInvariant(), claim.Id, moved.Version);
                return null;
            }

            case MemoryClaimDelete deleteClaim:
            {
                if (!state.Claims.TryGetValue(deleteClaim.ClaimId, out var claim)) return new(MemoryCommitStatus.NotFound, "claim");
                if (claim.Scope != scope) return new(MemoryCommitStatus.ScopeMismatch, "claim scope");
                if (claim.Version != deleteClaim.ExpectedVersion) return new(MemoryCommitStatus.VersionConflict, "claim version");
                state.Claims.Remove(claim.Id);
                state.AddJournal(now, "claim.delete", claim.Id, claim.Version);
                return null;
            }

            case MemorySuppressionInsert suppression:
                if (suppression.Suppression.Scope != scope) return new(MemoryCommitStatus.ScopeMismatch, "suppression scope");
                if (state.Suppressions.Any(s => s.Id == suppression.Suppression.Id)) return new(MemoryCommitStatus.Invalid, "suppression exists");
                state.Suppressions.Add(suppression.Suppression with { CreatedAt = suppression.Suppression.CreatedAt == default ? now : suppression.Suppression.CreatedAt });
                state.AddJournal(now, "suppression.insert", suppression.Suppression.Id, 1);
                return null;

            default:
                return new(MemoryCommitStatus.Invalid, "unknown change");
        }
    }

    // ---------------- persistence ----------------

    private async Task<T> ReadAsync<T>(Func<State, T> read, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await Task.Run(LoadCurrent, ct).ConfigureAwait(false);
            return read(state);
        }
        finally { _gate.Release(); }
    }

    private async Task<T> WriteAsync<T>(Func<State, (T Result, State? Next)> write, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                using var fileLock = await AcquireFileLockAsync(ct).ConfigureAwait(false);
                var state = LoadCurrent();
                var (result, next) = write(state);
                if (next is null) return result;
                next.Revision = state.Revision + 1;
                Save(next);
                return result;
            }, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private State LoadCurrent()
    {
        var info = new FileInfo(_path);
        if (!info.Exists)
        {
            _state = new State();
            _stamp = null;
            return _state;
        }
        var stamp = (info.LastWriteTimeUtc.Ticks, info.Length);
        if (_state is not null && _stamp == stamp) return _state;
        byte[] bytes;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                bytes = buffer.ToArray();
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 40) { Thread.Sleep(25); }
        }
        Document? doc;
        try { doc = bytes.Length == 0 ? new Document() : JsonSerializer.Deserialize<Document>(bytes, Json); }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The personal memory file '{_path}' is unreadable ({ex.Message}). It was left untouched; move it aside to start fresh.", ex);
        }
        if (doc is null) throw new InvalidDataException($"The personal memory file '{_path}' is empty or invalid. It was left untouched.");
        if (doc.Schema > SchemaVersion)
            throw new InvalidDataException($"The personal memory file '{_path}' was written by a newer Talavine Code (schema {doc.Schema}). It was left untouched.");
        _state = State.From(doc);
        _stamp = stamp;
        return _state;
    }

    private void Save(State next)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(next.ToDocument(), Json);
        var tmp = _path + "." + Guid.NewGuid().ToString("N")[..12] + ".tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // A scanner or indexer can hold the just-replaced file for a moment; the rename is retried, never skipped.
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(tmp, _path, overwrite: true); break; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 80) { Thread.Sleep(25); }
            }
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
        var info = new FileInfo(_path);
        _state = next;
        _stamp = (info.LastWriteTimeUtc.Ticks, info.Length);
    }

    private async Task<IDisposable> AcquireFileLockAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(40, ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new TimeoutException("Another Talavine Code process is holding the personal memory lock.", ex);
            }
        }
    }

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = false };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new MemoryScopeJsonConverter());
        return options;
    }

    internal sealed class Document
    {
        public int Schema { get; set; } = SchemaVersion;
        public long Revision { get; set; }
        public List<MemoryClaim> Claims { get; set; } = [];
        public List<MemoryFact> Facts { get; set; } = [];
        public List<MemorySuppression> Suppressions { get; set; } = [];
        public List<JournalEntry> Journal { get; set; } = [];
    }

    /// <summary>One audit row, written in the same atomic document as the change it records.</summary>
    internal sealed record JournalEntry(DateTimeOffset At, long Revision, string Change, Guid Id, long Version);

    private sealed class State
    {
        public long Revision { get; set; }
        public Dictionary<Guid, MemoryClaim> Claims { get; init; } = [];
        public Dictionary<Guid, MemoryFact> Facts { get; init; } = [];
        public List<MemorySuppression> Suppressions { get; init; } = [];
        public List<JournalEntry> Journal { get; init; } = [];

        public State Copy() => new()
        {
            Revision = Revision,
            Claims = new Dictionary<Guid, MemoryClaim>(Claims),
            Facts = new Dictionary<Guid, MemoryFact>(Facts),
            Suppressions = [.. Suppressions],
            Journal = [.. Journal],
        };

        public void AddJournal(DateTimeOffset at, string change, Guid id, long version)
        {
            Journal.Add(new JournalEntry(at, Revision + 1, change, id, version));
            if (Journal.Count > MaxJournalEntries) Journal.RemoveRange(0, Journal.Count - MaxJournalEntries);
        }

        public static State From(Document doc)
        {
            var state = new State { Revision = doc.Revision };
            foreach (var claim in doc.Claims.Where(c => !c.Scope.IsEmpty)) state.Claims[claim.Id] = claim;
            foreach (var fact in doc.Facts.Where(f => !f.Scope.IsEmpty)) state.Facts[fact.Id] = fact;
            state.Suppressions.AddRange(doc.Suppressions.Where(s => !s.Scope.IsEmpty));
            state.Journal.AddRange(doc.Journal);
            return state;
        }

        public Document ToDocument() => new()
        {
            Revision = Revision,
            Claims = Claims.Values.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToList(),
            Facts = Facts.Values.OrderBy(f => f.CreatedAt).ThenBy(f => f.Id).ToList(),
            Suppressions = [.. Suppressions],
            Journal = [.. Journal],
        };
    }
}

/// <summary>Round-trips <see cref="MemoryScope"/> through its validating constructor.</summary>
internal sealed class MemoryScopeJsonConverter : JsonConverter<MemoryScope>
{
    public override MemoryScope Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected a memory scope object.");
        string? tenant = null, subject = null, partition = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(subject)) return default;
                return new MemoryScope(tenant, subject, partition);
            }
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Invalid memory scope.");
            var name = reader.GetString();
            reader.Read();
            var value = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
            if (string.Equals(name, "tenantId", StringComparison.OrdinalIgnoreCase)) tenant = value;
            else if (string.Equals(name, "subjectId", StringComparison.OrdinalIgnoreCase)) subject = value;
            else if (string.Equals(name, "partition", StringComparison.OrdinalIgnoreCase)) partition = value;
        }
        throw new JsonException("Unterminated memory scope.");
    }

    public override void Write(Utf8JsonWriter writer, MemoryScope value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("tenantId", value.TenantId);
        writer.WriteString("subjectId", value.SubjectId);
        writer.WriteString("partition", value.Partition);
        writer.WriteEndObject();
    }
}
