using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwarmCore.Coding.SelfImprovement;

public sealed class SelfImprovementConflictException(string message) : InvalidOperationException(message);

/// <summary>
/// Durable backlog of defects and feature requests. Sightings fold into one item per fingerprint; the worker and the
/// person change items through <see cref="UpdateAsync"/>, optionally against the revision they read.
/// </summary>
public interface ISelfImprovementBacklog
{
    Task<IReadOnlyList<SelfImprovementItem>> ListAsync(CancellationToken ct = default);

    Task<SelfImprovementItem?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Folds a sighting into the item with the same fingerprint (applying <paramref name="update"/> to the stored item;
    /// null means no write) or stores <paramref name="create"/>'s item when there is none. Atomic under the store's lock.
    /// </summary>
    Task<(SelfImprovementItem Item, bool Created)> UpsertByFingerprintAsync(string fingerprint, Func<SelfImprovementItem> create,
        Func<SelfImprovementItem, SelfImprovementItem?> update, CancellationToken ct = default);

    /// <summary>
    /// Applies <paramref name="change"/> to the fresh stored item (null result writes nothing) and returns the stored
    /// item, or null when it no longer exists. With <paramref name="expectedRevision"/> a changed item is refused.
    /// </summary>
    /// <exception cref="SelfImprovementConflictException">The item changed since it was read.</exception>
    Task<SelfImprovementItem?> UpdateAsync(string id, Func<SelfImprovementItem, SelfImprovementItem?> change, long? expectedRevision = null, CancellationToken ct = default);

    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
}

/// <summary>Process-local backlog for hosts without a data directory and for tests.</summary>
public sealed class InMemorySelfImprovementBacklog : ISelfImprovementBacklog
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SelfImprovementItem> _items = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<SelfImprovementItem>> ListAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<SelfImprovementItem>>(_items.Values.OrderBy(i => i.FirstSeenUtc).ToList());
    }

    public Task<SelfImprovementItem?> GetAsync(string id, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_items.GetValueOrDefault(id));
    }

    public Task<(SelfImprovementItem Item, bool Created)> UpsertByFingerprintAsync(string fingerprint, Func<SelfImprovementItem> create,
        Func<SelfImprovementItem, SelfImprovementItem?> update, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var existing = _items.Values.FirstOrDefault(i => i.Fingerprint == fingerprint);
            if (existing is not null)
            {
                var changed = update(existing);
                if (changed is null) return Task.FromResult((existing, false));
                var stored = changed with { Id = existing.Id, Revision = existing.Revision + 1 };
                _items[stored.Id] = stored;
                return Task.FromResult((stored, false));
            }
            var created = create() with { Revision = 1 };
            _items[created.Id] = created;
            return Task.FromResult((created, true));
        }
    }

    public Task<SelfImprovementItem?> UpdateAsync(string id, Func<SelfImprovementItem, SelfImprovementItem?> change, long? expectedRevision = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(id, out var current)) return Task.FromResult<SelfImprovementItem?>(null);
            if (expectedRevision is { } expected && current.Revision != expected)
                throw new SelfImprovementConflictException("This item changed since it was read. Reload it and try again.");
            var changed = change(current);
            if (changed is null) return Task.FromResult<SelfImprovementItem?>(current);
            var stored = changed with { Id = current.Id, Revision = current.Revision + 1 };
            _items[id] = stored;
            return Task.FromResult<SelfImprovementItem?>(stored);
        }
    }

    public Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_items.Remove(id));
    }
}

/// <summary>
/// One JSON file per item (<c>&lt;id&gt;.json</c>) under a host directory, replaced atomically. Every write reads the
/// current files under an in-process gate and an exclusive sidecar lock file, so the console and the desktop app
/// writing one data directory are serialized too.
/// </summary>
public sealed class JsonFileSelfImprovementBacklog : ISelfImprovementBacklog
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonFileSelfImprovementBacklog(string directory) => Directory = Path.GetFullPath(directory);

    public string Directory { get; }

    /// <summary>Item ids are host-generated: 1-64 ASCII letters, digits, '-' or '_'.</summary>
    public static bool IsValidId(string? id)
        => !string.IsNullOrEmpty(id) && id.Length <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public async Task<IReadOnlyList<SelfImprovementItem>> ListAsync(CancellationToken ct = default)
    {
        var list = await ReadAllAsync(ct).ConfigureAwait(false);
        return list.OrderBy(i => i.FirstSeenUtc).ToList();
    }

    public async Task<SelfImprovementItem?> GetAsync(string id, CancellationToken ct = default)
        => IsValidId(id) ? await ReadAsync(PathFor(id), ct).ConfigureAwait(false) : null;

    public Task<(SelfImprovementItem Item, bool Created)> UpsertByFingerprintAsync(string fingerprint, Func<SelfImprovementItem> create,
        Func<SelfImprovementItem, SelfImprovementItem?> update, CancellationToken ct = default)
        => WriteLockedAsync(async () =>
        {
            var existing = (await ReadAllAsync(ct).ConfigureAwait(false)).FirstOrDefault(i => i.Fingerprint == fingerprint);
            if (existing is not null)
            {
                var changed = update(existing);
                if (changed is null) return (existing, false);
                var stored = changed with { Id = existing.Id, Revision = existing.Revision + 1 };
                await WriteAsync(PathFor(stored.Id), stored, ct).ConfigureAwait(false);
                return (stored, false);
            }
            var created = create() with { Revision = 1 };
            if (!IsValidId(created.Id)) throw new ArgumentException("Invalid item id.");
            if (File.Exists(PathFor(created.Id))) throw new InvalidOperationException("An item with that id already exists.");
            await WriteAsync(PathFor(created.Id), created, ct).ConfigureAwait(false);
            return (created, true);
        }, ct);

    public Task<SelfImprovementItem?> UpdateAsync(string id, Func<SelfImprovementItem, SelfImprovementItem?> change, long? expectedRevision = null, CancellationToken ct = default)
    {
        if (!IsValidId(id)) return Task.FromResult<SelfImprovementItem?>(null);
        return WriteLockedAsync<SelfImprovementItem?>(async () =>
        {
            var path = PathFor(id);
            if (await ReadAsync(path, ct).ConfigureAwait(false) is not { } current) return null;
            if (expectedRevision is { } expected && current.Revision != expected)
                throw new SelfImprovementConflictException("This item changed since it was read. Reload it and try again.");
            if (change(current) is not { } changed) return current;
            var stored = changed with { Id = current.Id, Revision = current.Revision + 1 };
            await WriteAsync(path, stored, ct).ConfigureAwait(false);
            return stored;
        }, ct);
    }

    public Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        if (!IsValidId(id)) return Task.FromResult(false);
        return WriteLockedAsync(async () =>
        {
            var path = PathFor(id);
            if (await ReadAsync(path, ct).ConfigureAwait(false) is null) return false;
            File.Delete(path);
            return true;
        }, ct);
    }

    private string PathFor(string id) => Path.Combine(Directory, id + ".json");

    private async Task<List<SelfImprovementItem>> ReadAllAsync(CancellationToken ct)
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var list = new List<SelfImprovementItem>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            var id = Path.GetFileNameWithoutExtension(file);
            if (!IsValidId(id)) continue;
            if (await ReadAsync(file, ct).ConfigureAwait(false) is { } item && item.Id == id) list.Add(item);
        }
        return list;
    }

    private static async Task<SelfImprovementItem?> ReadAsync(string path, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return await JsonSerializer.DeserializeAsync<SelfImprovementItem>(stream, Json, ct).ConfigureAwait(false);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (JsonException) { return null; } // an unreadable record is skipped, never rewritten from a guess
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
            {
                await Task.Delay(25, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task WriteAsync(string path, SelfImprovementItem item, CancellationToken ct)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, item, Json, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            for (var attempt = 1; ; attempt++)
            {
                try { File.Move(tmp, path, overwrite: true); break; }
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
