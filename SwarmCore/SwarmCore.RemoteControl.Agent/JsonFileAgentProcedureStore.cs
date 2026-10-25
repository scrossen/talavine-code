using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwarmCore.Agent.Procedures;

namespace SwarmCore.RemoteControl.Agent;

/// <summary>
/// File-backed <see cref="IAgentProcedureStore"/> for standalone hosts without a database: one JSON file per key in
/// <see cref="Directory"/>. Compare-and-swap is by version under an in-process gate plus an exclusive lock file, so a
/// second process cannot interleave; the new head is written to a temp file and atomically moved over the old one.
/// Revisions are never removed: superseded and rejected revisions stay in the file for history and rollback.
/// </summary>
public sealed class JsonFileAgentProcedureStore : IAgentProcedureStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonFileAgentProcedureStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = Path.GetFullPath(directory);
    }

    public string Directory { get; }

    private sealed record FileRecord(AgentProcedureKey Key, long Version, AgentProcedureHistory History);

    public string PathFor(AgentProcedureKey key)
    {
        Validate(key);
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{key.ProfileId}\n{key.AgentId}\n{key.TaskId}")));
        return Path.Combine(Directory, id[..32] + ".json");
    }

    public async Task<AgentProcedureHistory?> GetAsync(AgentProcedureKey key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return (await ReadAsync(path, ct).ConfigureAwait(false)) is { } r && r.Key == key ? r.History : null; }
        finally { _gate.Release(); }
    }

    public async Task<bool> CompareExchangeAsync(AgentProcedureKey key, long expectedVersion,
        AgentProcedureHistory replacement, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (expectedVersion < 0 || replacement.Key != key || replacement.Version != checked(expectedVersion + 1))
            throw new ArgumentException("A procedure transition must retain its scope and increment the expected version once.");
        // Freeze the caller's collections before the first await.
        var json = JsonSerializer.Serialize(new FileRecord(key, replacement.Version, replacement), Json);
        var path = PathFor(key);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            await using var fileLock = await AcquireLockAsync(path + ".lock", ct).ConfigureAwait(false);
            var current = await ReadAsync(path, ct).ConfigureAwait(false);
            if (current is not null && current.Key != key) throw new InvalidDataException("A procedure file belongs to another key.");
            if ((current?.Version ?? 0) != expectedVersion) return false;
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temp, json, ct).ConfigureAwait(false);
                File.Move(temp, path, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<AgentProcedureHistory>> ListAsync(string profileId, string agentId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return [];
            var list = new List<AgentProcedureHistory>();
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
                if (await ReadAsync(file, ct).ConfigureAwait(false) is { } r && r.Key.ProfileId == profileId && r.Key.AgentId == agentId)
                    list.Add(r.History);
            return list.OrderBy(h => h.Key.TaskId, StringComparer.Ordinal).ToArray();
        }
        finally { _gate.Release(); }
    }

    private static async Task<FileRecord?> ReadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var record = JsonSerializer.Deserialize<FileRecord>(text, Json) ?? throw new InvalidDataException($"Unreadable procedure file {path}.");
        if (record.History.Key != record.Key || record.History.Version != record.Version)
            throw new InvalidDataException($"Procedure file {path} is inconsistent.");
        return record;
    }

    private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                // The lock file is deleted when the handle closes, so no stale lock outlives a crash of this process.
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (attempt < 200)
            {
                await Task.Delay(25, ct).ConfigureAwait(false);
            }
        }
    }

    private static void Validate(AgentProcedureKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.ProfileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.AgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key.TaskId);
    }
}
