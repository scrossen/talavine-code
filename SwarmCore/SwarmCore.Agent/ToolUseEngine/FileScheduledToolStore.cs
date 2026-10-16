using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace SwarmCore.Agent.ToolUseEngine;

/// <summary>
/// Profile-private, atomic file journal. Hosts supply their existing profile data directory.
/// Records are partitioned into one sub-directory per host turn, so reconciling a turn opens only
/// that turn's files; <see cref="PruneAsync"/> bounds how long finished turns are kept.
/// A cross-process exclusive handle protects each operation; replace publishes terminal receipts
/// before model continuation. Arguments stay in the private journal, never diagnostics.
/// A record this host cannot read (for example one protected on another machine) is preserved
/// and skipped by bulk reads; it never makes the rest of the journal unreadable.
/// </summary>
public sealed class FileScheduledToolStore(string directory, IToolJournalProtector protector,
    string? executionLockDirectory = null, ILogger? logger = null) : IScheduledToolStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    // Unreadable records are reported once per process, not on every read.
    private static readonly ConcurrentDictionary<string, byte> ReportedUnreadable = new(StringComparer.OrdinalIgnoreCase);
    private const int TurnDirectoryNameLength = 32;
    private readonly string _root = Path.GetFullPath(directory);
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string RecordName(string operation) => Hash(operation) + ".json";
    // Records written before turn partitioning live directly under the root until migrated.
    private string LegacyPathFor(string operation) => Path.Combine(_root, RecordName(operation));
    private string TurnDirectory(ToolScheduleIdentity identity)
        => Path.Combine(_root, Hash(JsonSerializer.Serialize(new[] { identity.Profile, identity.Conversation, identity.Turn }))[..TurnDirectoryNameLength]);
    private string PathFor(ToolScheduleIdentity identity, string operation) => Path.Combine(TurnDirectory(identity), RecordName(operation));

    public async Task<IAsyncDisposable> AcquireProfileLeaseAsync(string profile, CancellationToken ct)
        // One cross-process batch owns the host's tools. Its reviewed siblings may overlap;
        // unrelated batches wait, including different profiles that could name the same tab/repo.
        => await LockAsync("host-tool-batch", ct, executionLockDirectory);

    public async Task<ScheduledToolRecord> RegisterAsync(ScheduledToolRecord record, JsonObject arguments, CancellationToken ct)
    {
        var turn = TurnDirectory(record.Identity);
        await using var gate = await LockAsync(record.OperationId, ct, turn);
        var existing = await ReadRecordAsync(record.Identity, record.OperationId, ct);
        if (existing is not null)
        {
            if (existing.Identity != record.Identity || existing.CallId != record.CallId || existing.ToolId != record.ToolId
                || existing.ArgumentsReference != record.ArgumentsReference)
                throw new InvalidDataException("A tool operation identity was redelivered with different content.");
            return existing;
        }
        if (Hash(arguments.ToJsonString()) != record.ArgumentsReference)
            throw new InvalidDataException("Tool argument reference mismatch.");
        await WriteAtomicAsync(Path.Combine(turn, record.ArgumentsReference + ".arguments"), protector.Protect(arguments.ToJsonString()), ct);
        await WriteAtomicAsync(PathFor(record.Identity, record.OperationId), protector.Protect(JsonSerializer.Serialize(record, Json)), ct);
        return record;
    }

    public async Task<bool> UpdateAsync(ScheduledToolRecord record, long expectedVersion, CancellationToken ct)
    {
        await using var gate = await LockAsync(record.OperationId, ct, TurnDirectory(record.Identity));
        var prior = await ReadRecordAsync(record.Identity, record.OperationId, ct);
        if (prior is null || prior.Version != expectedVersion) return false;
        var allowed = prior.State switch
        {
            ScheduledToolState.Queued => record.State is ScheduledToolState.Running or ScheduledToolState.Failed or ScheduledToolState.Cancelled,
            ScheduledToolState.Running => record.State is ScheduledToolState.Completed or ScheduledToolState.Failed or ScheduledToolState.Cancelled or ScheduledToolState.Uncertain,
            ScheduledToolState.Uncertain => record.State is ScheduledToolState.Uncertain or ScheduledToolState.Completed or ScheduledToolState.Failed,
            _ => false
        };
        if (!allowed) throw new InvalidOperationException("Invalid scheduled tool lifecycle transition.");
        if (prior.Identity != record.Identity || prior.ArgumentsReference != record.ArgumentsReference
            || prior.ToolId != record.ToolId || prior.CallId != record.CallId)
            throw new InvalidDataException("A journal transition cannot change operation identity.");
        if (!ValidHash(record.ArgumentsReference)) throw new InvalidDataException("Invalid tool argument reference.");
        await WriteAtomicAsync(PathFor(record.Identity, record.OperationId), protector.Protect(JsonSerializer.Serialize(record with { Version = expectedVersion + 1 }, Json)), ct);
        return true;
    }

    public async Task<IReadOnlyList<ScheduledToolRecord>> ReadTurnAsync(ToolScheduleIdentity identity, CancellationToken ct)
    {
        var records = new List<ScheduledToolRecord>();
        await ReadReadableAsync(TurnDirectory(identity), records, ct);
        await ReadReadableAsync(_root, records, ct);
        return records.Where(record => record.Identity == identity).ToArray();
    }

    /// <summary>Every readable record; unreadable ones are preserved, reported once, and skipped.</summary>
    public async Task<IReadOnlyList<ScheduledToolRecord>> ReadAllAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_root)) return [];
        var records = new List<ScheduledToolRecord>();
        foreach (var turn in Directory.EnumerateDirectories(_root).Where(IsTurnDirectory))
            await ReadReadableAsync(turn, records, ct);
        await ReadReadableAsync(_root, records, ct);
        return records;
    }

    /// <summary>
    /// Retention sweep, run by the host (for example at start-up) under the host tool lease so no
    /// scheduler writes concurrently. Moves records written before turn partitioning into their
    /// turn directory, then deletes each turn directory whose records all finished
    /// (completed, failed or cancelled) before <paramref name="finishedBefore"/>. Pending, uncertain,
    /// recent and unreadable records are preserved. Returns the number of turns removed.
    /// </summary>
    public async Task<int> PruneAsync(DateTimeOffset finishedBefore, CancellationToken ct)
    {
        if (!Directory.Exists(_root)) return 0;
        await using var lease = await AcquireProfileLeaseAsync("", ct);
        await MigrateLegacyRecordsAsync(ct);
        var removed = 0;
        foreach (var turn in Directory.EnumerateDirectories(_root).Where(IsTurnDirectory).ToArray())
        {
            ct.ThrowIfCancellationRequested();
            // Every record write renames into the directory, so a recent directory time means a
            // recent record; only older directories are opened.
            if (Directory.GetLastWriteTimeUtc(turn) >= finishedBefore.UtcDateTime) continue;
            var finished = true;
            foreach (var path in Directory.EnumerateFiles(turn, "*.json"))
            {
                var record = await TryReadAsync(path, ct);
                if (record is null || record.UpdatedAtUtc >= finishedBefore || record.State is not
                        (ScheduledToolState.Completed or ScheduledToolState.Failed or ScheduledToolState.Cancelled))
                {
                    finished = false;
                    break;
                }
            }
            if (!finished) continue;
            try
            {
                Directory.Delete(turn, recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A reader still has a file open; the next sweep retries.
            }
        }
        return removed;
    }

    private async Task<ScheduledToolRecord?> ReadRecordAsync(ToolScheduleIdentity identity, string operationId, CancellationToken ct)
    {
        var path = PathFor(identity, operationId);
        if (!File.Exists(path))
        {
            var legacy = LegacyPathFor(operationId);
            if (!File.Exists(legacy)) return null;
            var legacyRecord = await ReadFileAsync(legacy, ct);
            if (legacyRecord.OperationId != operationId) throw new InvalidDataException("Invalid tool journal identity/version.");
            MoveLegacyRecord(legacy, legacyRecord);
            return legacyRecord;
        }
        var record = await ReadFileAsync(path, ct);
        if (record.OperationId != operationId) throw new InvalidDataException("Invalid tool journal identity/version.");
        return record;
    }

    private async Task<ScheduledToolRecord> ReadFileAsync(string path, CancellationToken ct)
    {
        var record = JsonSerializer.Deserialize<ScheduledToolRecord>(protector.Unprotect(await File.ReadAllTextAsync(path, ct)), Json)
            ?? throw new InvalidDataException("Empty tool journal record.");
        if (record.SchemaVersion != 1 || !ValidHash(record.ArgumentsReference))
            throw new InvalidDataException("Unsupported tool journal version/reference.");
        return record;
    }

    private async Task<ScheduledToolRecord?> TryReadAsync(string path, CancellationToken ct)
    {
        try { return await ReadFileAsync(path, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ReportedUnreadable.TryAdd(path, 0))
                logger?.LogWarning("Tool journal record {Record} could not be read ({ErrorType}); it was preserved and skipped.",
                    Path.GetFileName(path), ex.GetType().Name);
            return null;
        }
    }

    private async Task ReadReadableAsync(string directory, List<ScheduledToolRecord> records, CancellationToken ct)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            if (await TryReadAsync(path, ct) is { } record) records.Add(record);
        }
    }

    private async Task MigrateLegacyRecordsAsync(CancellationToken ct)
    {
        var unreadable = false;
        foreach (var legacy in Directory.EnumerateFiles(_root, "*.json").ToArray())
        {
            ct.ThrowIfCancellationRequested();
            if (await TryReadAsync(legacy, ct) is { } record) MoveLegacyRecord(legacy, record);
            else unreadable = true;
        }
        // Shared argument files may still belong to a record this host cannot read.
        if (unreadable) return;
        foreach (var arguments in Directory.EnumerateFiles(_root, "*.arguments").ToArray())
            File.Delete(arguments);
    }

    private void MoveLegacyRecord(string legacyPath, ScheduledToolRecord record)
    {
        var turn = TurnDirectory(record.Identity);
        Directory.CreateDirectory(turn);
        var arguments = record.ArgumentsReference + ".arguments";
        var legacyArguments = Path.Combine(_root, arguments);
        // Arguments are content-addressed, so overwriting repairs an interrupted earlier copy.
        if (File.Exists(legacyArguments)) File.Copy(legacyArguments, Path.Combine(turn, arguments), overwrite: true);
        var target = Path.Combine(turn, Path.GetFileName(legacyPath));
        if (File.Exists(target)) File.Delete(legacyPath);
        else File.Move(legacyPath, target);
        // The old per-operation lock beside the record is only taken under the host tool lease.
        var legacyLock = Path.ChangeExtension(legacyPath, ".lock");
        if (File.Exists(legacyLock)) File.Delete(legacyLock);
    }

    private static bool IsTurnDirectory(string path)
    {
        var name = Path.GetFileName(path);
        return name.Length == TurnDirectoryNameLength && name.All(char.IsAsciiHexDigit);
    }

    private static bool ValidHash(string hash) => hash.Length == 64 && hash.All(c => char.IsAsciiHexDigit(c));

    private async Task<FileStream> LockAsync(string operation, CancellationToken ct, string? lockDirectory = null)
    {
        var root = lockDirectory is null ? _root : Path.GetFullPath(lockDirectory);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, Hash(operation) + ".lock");
        var delay = 25;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                await Task.Delay(delay, ct);
                delay = Math.Min(delay * 2, 1000);
            }
        }
    }

    private static async Task WriteAtomicAsync(string path, string content, CancellationToken ct)
    {
        var scratch = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(scratch, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(content), ct);
                stream.Flush(flushToDisk: true);
            }
            File.Move(scratch, path, overwrite: true);
        }
        finally { if (File.Exists(scratch)) File.Delete(scratch); }
    }
}
