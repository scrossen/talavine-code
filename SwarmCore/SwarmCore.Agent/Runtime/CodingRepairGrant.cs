using SwarmCore.Abstractions.CodingTasks;

namespace SwarmCore.Agent.Runtime;

/// <summary>
/// Host-only authority to prepare an isolated patch in one configured source repository.
/// It grants no communication, deployment, publication, or changes to a running application.
/// </summary>
public sealed class CodingRepairGrant
{
    private readonly HashSet<string> _tools;
    private CodingRepairGrant(string authorityId, string repositoryPath, IEnumerable<string> toolIds)
    {
        AuthorityId = authorityId;
        RepositoryPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        _tools = toolIds.ToHashSet(StringComparer.Ordinal);
    }

    public string AuthorityId { get; }
    public string RepositoryPath { get; }

    /// <summary>Call only after resolving the configured path physically and checking each tool against repair policy.</summary>
    public static CodingRepairGrant ForConfiguredRepository(AgentRepairAuthority authority,
        string physicalRepositoryPath, IEnumerable<string> admittedToolIds)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (!Path.IsPathFullyQualified(physicalRepositoryPath)
            || Path.TrimEndingDirectorySeparator(physicalRepositoryPath) == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(physicalRepositoryPath)!))
            throw new ArgumentException("Repair requires one configured repository, not a filesystem root.", nameof(physicalRepositoryPath));
        return new(authority.AuthorityId, physicalRepositoryPath, admittedToolIds);
    }

    public bool Allows(CodingTaskRequest request)
        => request.Mode == CodingTaskMode.Verified && !request.UseExistingCheckout
           && !string.IsNullOrWhiteSpace(request.RepoPath) && Path.IsPathFullyQualified(request.RepoPath)
           && string.Equals(RepositoryPath, Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.RepoPath)),
               OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
           && request.AllowedToolIds.Count > 0 && request.AllowedToolIds.All(_tools.Contains);
}
