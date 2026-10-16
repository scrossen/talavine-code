using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwarmCore.Coding.Scheduling;

/// <summary>
/// Durable schedules. Writers own disjoint fields: definition edits carry the revision they read and are rejected when
/// it is stale; the scheduler's run bookkeeping changes only <see cref="CodingSchedule.Run"/> on the fresh stored row.
/// Neither can recreate a deleted schedule.
/// </summary>
public interface ICodingScheduleStore
{
    Task<IReadOnlyList<CodingSchedule>> ListAsync(CancellationToken ct = default);

    Task<CodingSchedule?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>Saves a new schedule at revision 1. Fails if the id exists.</summary>
    Task<CodingSchedule> CreateAsync(CodingSchedule schedule, CancellationToken ct = default);

    /// <summary>
    /// Applies a definition edit to the fresh stored row when its revision is still <paramref name="expectedRevision"/>.
    /// The result keeps the stored run state except <see cref="ScheduleRunState.NextRunUtc"/> (recomputed by the edit),
    /// its identity and creation fields, and gets the next revision.
    /// </summary>
    /// <exception cref="CodingScheduleConflictException">The schedule changed since it was read.</exception>
    /// <exception cref="CodingScheduleNotFoundException">The schedule was deleted.</exception>
    Task<CodingSchedule> UpdateDefinitionAsync(string id, long expectedRevision, Func<CodingSchedule, CodingSchedule> change, CancellationToken ct = default);

    /// <summary>
    /// Replaces only the run state, computed from the fresh stored row. <paramref name="change"/> returning null writes
    /// nothing. Returns the stored schedule, or null when it no longer exists (nothing is written).
    /// </summary>
    Task<CodingSchedule?> UpdateRunAsync(string id, Func<CodingSchedule, ScheduleRunState?> change, CancellationToken ct = default);

    /// <summary>Deletes the schedule; with <paramref name="expectedRevision"/> only if it is unchanged. False when absent.</summary>
    Task<bool> DeleteAsync(string id, long? expectedRevision = null, CancellationToken ct = default);
}

/// <summary>
/// One JSON file per schedule (<c>&lt;id&gt;.json</c>) under a host directory, replaced atomically. Every write reads
/// the current file under an in-process gate and an exclusive sidecar lock file, so writers in two processes over one
/// data directory are serialized too.
/// </summary>
public sealed class JsonFileCodingScheduleStore : ICodingScheduleStore
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonFileCodingScheduleStore(string directory) => Directory = Path.GetFullPath(directory);

    public string Directory { get; }

    /// <summary>Schedule ids are host-generated: 1-64 ASCII letters, digits, '-' or '_'.</summary>
    public static bool IsValidId(string? id)
        => !string.IsNullOrEmpty(id) && id.Length <= 64 && id.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_');

    public async Task<IReadOnlyList<CodingSchedule>> ListAsync(CancellationToken ct = default)
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var list = new List<CodingSchedule>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            var id = Path.GetFileNameWithoutExtension(file);
            if (!IsValidId(id)) continue;
            if (await ReadAsync(file, ct).ConfigureAwait(false) is { } schedule && schedule.Id == id) list.Add(schedule);
        }
        return list.OrderBy(s => s.CreatedUtc).ToList();
    }

    public async Task<CodingSchedule?> GetAsync(string id, CancellationToken ct = default)
        => IsValidId(id) ? await ReadAsync(PathFor(id), ct).ConfigureAwait(false) : null;

    public Task<CodingSchedule> CreateAsync(CodingSchedule schedule, CancellationToken ct = default)
    {
        if (!IsValidId(schedule.Id)) throw new ArgumentException("Invalid schedule id.");
        return WriteLockedAsync(async () =>
        {
            var path = PathFor(schedule.Id);
            if (File.Exists(path)) throw new InvalidOperationException("A schedule with that id already exists.");
            var stored = schedule with { Revision = 1 };
            await WriteAsync(path, stored, ct).ConfigureAwait(false);
            return stored;
        }, ct);
    }

    public Task<CodingSchedule> UpdateDefinitionAsync(string id, long expectedRevision, Func<CodingSchedule, CodingSchedule> change, CancellationToken ct = default)
    {
        if (!IsValidId(id)) throw new CodingScheduleNotFoundException("That schedule does not exist.");
        return WriteLockedAsync(async () =>
        {
            var path = PathFor(id);
            var current = await ReadAsync(path, ct).ConfigureAwait(false)
                          ?? throw new CodingScheduleNotFoundException("That schedule was deleted.");
            if (current.Revision != expectedRevision)
                throw new CodingScheduleConflictException("This schedule changed since it was read. Reload it and try again.");
            var edited = change(current);
            var stored = edited with
            {
                Id = current.Id,
                CreatedUtc = current.CreatedUtc,
                CreatedBySessionId = current.CreatedBySessionId,
                Revision = current.Revision + 1,
                Run = current.Run with { NextRunUtc = edited.Run.NextRunUtc },
            };
            await WriteAsync(path, stored, ct).ConfigureAwait(false);
            return stored;
        }, ct);
    }

    public Task<CodingSchedule?> UpdateRunAsync(string id, Func<CodingSchedule, ScheduleRunState?> change, CancellationToken ct = default)
    {
        if (!IsValidId(id)) return Task.FromResult<CodingSchedule?>(null);
        return WriteLockedAsync<CodingSchedule?>(async () =>
        {
            var path = PathFor(id);
            if (await ReadAsync(path, ct).ConfigureAwait(false) is not { } current) return null;
            if (change(current) is not { } run) return current;
            var stored = current with { Run = run };
            await WriteAsync(path, stored, ct).ConfigureAwait(false);
            return stored;
        }, ct);
    }

    public Task<bool> DeleteAsync(string id, long? expectedRevision = null, CancellationToken ct = default)
    {
        if (!IsValidId(id)) return Task.FromResult(false);
        return WriteLockedAsync(async () =>
        {
            var path = PathFor(id);
            if (await ReadAsync(path, ct).ConfigureAwait(false) is not { } current) return false;
            if (expectedRevision is { } expected && current.Revision != expected)
                throw new CodingScheduleConflictException("This schedule changed since it was read. Reload it and try again.");
            File.Delete(path);
            return true;
        }, ct);
    }

    private string PathFor(string id) => Path.Combine(Directory, id + ".json");

    private static async Task<CodingSchedule?> ReadAsync(string path, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return await JsonSerializer.DeserializeAsync<CodingSchedule>(stream, Json, ct).ConfigureAwait(false);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (JsonException) { return null; } // an unreadable record is skipped, never rewritten from a guess
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
            {
                await Task.Delay(25, ct).ConfigureAwait(false); // the file is being replaced or scanned
            }
        }
    }

    private static async Task WriteAsync(string path, CodingSchedule schedule, CancellationToken ct)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, schedule, Json, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            // A reader, indexer or scanner holding the target briefly makes the replace fail on Windows: retry shortly.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(tmp, path, overwrite: true);
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
                {
                    await Task.Delay(25, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    private async Task<T> WriteLockedAsync<T>(Func<Task<T>> write, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            await using var lease = await AcquireLeaseAsync(ct).ConfigureAwait(false);
            return await write().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<FileStream> AcquireLeaseAsync(CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(Directory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < until) { await Task.Delay(25, ct).ConfigureAwait(false); }
        }
    }
}
