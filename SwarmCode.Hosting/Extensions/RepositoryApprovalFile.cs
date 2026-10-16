using System.Collections.Concurrent;
using System.Text.Json;
using SwarmCore.Coding.Extensions;

namespace SwarmCode.Hosting.Extensions;

/// <summary>
/// One JSON file of per-repository content approvals in the SwarmCode data directory: each entry holds the SHA-256 of
/// the exact content the person approved for one repository, so any change needs a new approval and approving new
/// content replaces the old hash. Writes reread the file under an in-process gate plus an exclusive sidecar lease (the
/// console and the desktop app can run together), then replace it atomically, so a concurrent approval for another
/// repository is never lost. Shared by <see cref="FileCodingHookApprovalStore"/> and
/// <see cref="FileCodingRepositorySettingsApprovalStore"/>.
/// </summary>
public sealed class RepositoryApprovalFile(string path)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Path { get; } = path;

    public sealed record Approval(string Repository, string Sha256, DateTimeOffset ApprovedUtc);

    private sealed class Document
    {
        public List<Approval> Approvals { get; set; } = [];
    }

    public async Task<bool> IsApprovedAsync(string repositoryRoot, string sha256, CancellationToken ct = default)
    {
        var root = CodingHookSource.NormalizeRoot(repositoryRoot);
        var document = await ReadAsync(ct).ConfigureAwait(false);
        return document.Approvals.Any(a => CodingHookSource.RootComparer.Equals(a.Repository, root)
            && string.Equals(a.Sha256, sha256, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The approval recorded for a repository, if any.</summary>
    public async Task<Approval?> GetAsync(string repositoryRoot, CancellationToken ct = default)
    {
        var root = CodingHookSource.NormalizeRoot(repositoryRoot);
        return (await ReadAsync(ct).ConfigureAwait(false)).Approvals.FirstOrDefault(a => CodingHookSource.RootComparer.Equals(a.Repository, root));
    }

    public Task ApproveAsync(string repositoryRoot, string sha256, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sha256) || sha256.Length != 64) throw new ArgumentException("A SHA-256 hex digest is required.", nameof(sha256));
        var root = CodingHookSource.NormalizeRoot(repositoryRoot);
        return UpdateAsync(d =>
        {
            d.Approvals.RemoveAll(a => CodingHookSource.RootComparer.Equals(a.Repository, root));
            d.Approvals.Add(new Approval(root, sha256.ToLowerInvariant(), DateTimeOffset.UtcNow));
        }, ct);
    }

    public Task RevokeAsync(string repositoryRoot, CancellationToken ct = default)
    {
        var root = CodingHookSource.NormalizeRoot(repositoryRoot);
        return UpdateAsync(d => d.Approvals.RemoveAll(a => CodingHookSource.RootComparer.Equals(a.Repository, root)), ct);
    }

    private async Task<Document> ReadAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(Path)) return new Document();
            await using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return await JsonSerializer.DeserializeAsync<Document>(stream, Json, ct).ConfigureAwait(false) ?? new Document();
        }
        catch (JsonException) { return new Document(); } // unreadable approvals approve nothing (fail closed)
        catch (FileNotFoundException) { return new Document(); }
    }

    private async Task UpdateAsync(Action<Document> change, CancellationToken ct)
    {
        var gate = Gates.GetOrAdd(Path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            await using var lease = await AcquireLeaseAsync(ct).ConfigureAwait(false);
            var document = await ReadAsync(ct).ConfigureAwait(false);
            change(document);
            var temp = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, document, Json, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, Path, overwrite: true);
        }
        finally { gate.Release(); }
    }

    private async Task<FileStream> AcquireLeaseAsync(CancellationToken ct)
    {
        var lockPath = Path + ".lock";
        for (var attempt = 0; ; attempt++)
        {
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 100) { await Task.Delay(50, ct).ConfigureAwait(false); }
        }
    }
}

/// <summary>
/// The person's approvals of repository permission settings (<c>.swarmcode/settings.json</c>, <c>.claude/settings.json</c>
/// allow rules), kept in the SwarmCode data directory (<c>repo-settings-approvals.json</c>), never in the repository.
/// One entry per repository holds the digest <see cref="SwarmCore.Coding.Permissions.RepositoryPermissionRules"/> computes
/// over the files read, so any change to either file needs a new approval. Deny rules never need approval.
/// </summary>
public sealed class FileCodingRepositorySettingsApprovalStore(SwarmCodeDataPaths paths) : SwarmCore.Coding.Permissions.ICodingRepositorySettingsApprovalStore
{
    public const string FileName = "repo-settings-approvals.json";
    private readonly RepositoryApprovalFile _file = new(System.IO.Path.Combine(paths.RootDirectory, FileName));

    public string Path => _file.Path;

    public Task<bool> IsApprovedAsync(string repositoryRoot, string sha256, CancellationToken ct = default) => _file.IsApprovedAsync(repositoryRoot, sha256, ct);
    public Task<RepositoryApprovalFile.Approval?> GetAsync(string repositoryRoot, CancellationToken ct = default) => _file.GetAsync(repositoryRoot, ct);
    public Task ApproveAsync(string repositoryRoot, string sha256, CancellationToken ct = default) => _file.ApproveAsync(repositoryRoot, sha256, ct);
    public Task RevokeAsync(string repositoryRoot, CancellationToken ct = default) => _file.RevokeAsync(repositoryRoot, ct);
}
