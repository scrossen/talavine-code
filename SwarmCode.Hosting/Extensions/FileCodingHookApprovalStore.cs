using SwarmCore.Coding.Extensions;

namespace SwarmCode.Hosting.Extensions;

/// <summary>
/// The person's approvals of repository hooks, kept in the SwarmCode data directory (<c>hook-approvals.json</c>), never
/// in the repository. One entry per repository holds the SHA-256 of the exact <c>.swarmcode/hooks.json</c> bytes the
/// person approved, so any change to that file needs a new approval; approving new content replaces the old hash.
/// The file handling (gate, sidecar lease, atomic replace) is <see cref="RepositoryApprovalFile"/>.
/// </summary>
public sealed class FileCodingHookApprovalStore(SwarmCodeDataPaths paths) : ICodingHookApprovalStore
{
    public const string FileName = "hook-approvals.json";
    private readonly RepositoryApprovalFile _file = new(System.IO.Path.Combine(paths.RootDirectory, FileName));

    public string Path => _file.Path;

    public Task<bool> IsApprovedAsync(string repositoryRoot, string sha256, CancellationToken ct = default) => _file.IsApprovedAsync(repositoryRoot, sha256, ct);

    /// <summary>The approval recorded for a repository, if any.</summary>
    public Task<RepositoryApprovalFile.Approval?> GetAsync(string repositoryRoot, CancellationToken ct = default) => _file.GetAsync(repositoryRoot, ct);

    public Task ApproveAsync(string repositoryRoot, string sha256, CancellationToken ct = default) => _file.ApproveAsync(repositoryRoot, sha256, ct);
    public Task RevokeAsync(string repositoryRoot, CancellationToken ct = default) => _file.RevokeAsync(repositoryRoot, ct);
}
