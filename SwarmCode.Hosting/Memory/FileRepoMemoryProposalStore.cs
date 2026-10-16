using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using SwarmCore.Coding.Memory;

namespace SwarmCode.Hosting.Memory;

/// <summary>
/// Durable repository-memory proposals under SWARMCODE_HOME (<see cref="SwarmCodeDataPaths.RepoMemoryProposalsDirectory"/>),
/// one JSON file per proposal written atomically. A status change is version-checked under a per-process gate and a
/// cross-process lock file, so the desktop app, the console and the native-lane bridge can share the directory.
/// </summary>
public sealed class FileRepoMemoryProposalStore : IRepoMemoryProposalStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxListed = 500;

    private readonly string _directory;
    private readonly SemaphoreSlim _gate;
    private readonly TimeProvider _time;

    public FileRepoMemoryProposalStore(string directory, TimeProvider? time = null)
    {
        _directory = Path.GetFullPath(directory);
        _gate = Gates.GetOrAdd(_directory, _ => new SemaphoreSlim(1, 1));
        _time = time ?? TimeProvider.System;
    }

    public FileRepoMemoryProposalStore(SwarmCodeDataPaths paths, TimeProvider? time = null) : this(paths.RepoMemoryProposalsDirectory, time) { }

    public async Task<RepoMemoryProposal> AddAsync(RepoMemoryProposal proposal, CancellationToken ct = default)
    {
        if (proposal.Version != 0) throw new ArgumentException("A new proposal must have version 0.", nameof(proposal));
        var now = _time.GetUtcNow();
        var stored = proposal with { Version = 1, CreatedAt = proposal.CreatedAt == default ? now : proposal.CreatedAt, UpdatedAt = now };
        await WithLockAsync(() =>
        {
            var path = PathFor(stored.Id);
            if (File.Exists(path)) throw new InvalidOperationException("Proposal id already exists.");
            Write(path, stored);
            return Task.FromResult(0);
        }, ct).ConfigureAwait(false);
        return stored;
    }

    public Task<RepoMemoryProposal?> GetAsync(Guid id, CancellationToken ct = default)
        => Task.Run(() => Read(PathFor(id)), ct);

    public Task<IReadOnlyList<RepoMemoryProposal>> ListAsync(string? repositoryRoot, bool includeClosed, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<RepoMemoryProposal>>(() =>
        {
            if (!Directory.Exists(_directory)) return [];
            return Directory.EnumerateFiles(_directory, "*.json")
                .Select(Read)
                .OfType<RepoMemoryProposal>()
                .Where(p => repositoryRoot is null || RepoMemoryProposals.SameRoot(p.RepositoryRoot, repositoryRoot))
                .Where(p => includeClosed || p.Status == RepoMemoryProposalStatus.Pending)
                .OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id)
                .Take(MaxListed)
                .ToList();
        }, ct);

    public Task<RepoMemoryProposal?> SetStatusAsync(Guid id, long expectedVersion, RepoMemoryProposalStatus status, string? reason, CancellationToken ct = default)
        => WithLockAsync(() =>
        {
            var path = PathFor(id);
            var current = Read(path);
            if (current is null || current.Version != expectedVersion) return Task.FromResult<RepoMemoryProposal?>(null);
            var next = current with { Status = status, StatusReason = reason, UpdatedAt = _time.GetUtcNow(), Version = current.Version + 1 };
            Write(path, next);
            return Task.FromResult<RepoMemoryProposal?>(next);
        }, ct);

    private string PathFor(Guid id) => Path.Combine(_directory, id.ToString("N") + ".json");

    private static RepoMemoryProposal? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<RepoMemoryProposal>(File.ReadAllBytes(path), Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static void Write(string path, RepoMemoryProposal proposal)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(proposal, Json));
        for (var attempt = 0; ; attempt++)
        {
            try { File.Move(tmp, path, overwrite: true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 80) { Thread.Sleep(25); }
        }
    }

    private async Task<T> WithLockAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            FileStream? fileLock = null;
            while (fileLock is null)
            {
                try { fileLock = new FileStream(Path.Combine(_directory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(40, ct).ConfigureAwait(false); }
            }
            using (fileLock) return await action().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
