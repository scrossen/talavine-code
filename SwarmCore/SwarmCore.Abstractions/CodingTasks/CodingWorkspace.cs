namespace SwarmCore.Abstractions.CodingTasks;

/// <summary>Host-authorized setup, invoked only after durable run ownership is claimed.</summary>
public sealed record CodingWorkspaceRequest(Guid RunId, string RepositoryPath, string WorkspaceRoot)
{
    public string? BaselineRef { get; init; }
    public bool UseExistingCheckout { get; init; }
    public IReadOnlyList<string> AdmittedPaths { get; init; } = [];
    public CodingWorkspaceIdentity? ExistingIdentity { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<CancellationToken, Task>? ValidatePreparation { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<string, bool, CancellationToken, Task>? AuthorizePreparationWrite { get; init; }
}
public sealed record CodingWorkspaceIdentity(Guid RunId, string RepositoryPath, string WorkspacePath,
    string Branch, string BaseCommit, string InitialStatus, string BaselineTreeDigest)
{
    public bool UseExistingCheckout { get; init; }
    public IReadOnlyList<string> AdmittedPaths { get; init; } = [];
    public IReadOnlyDictionary<string, string> BaselineFiles { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> GeneratedOutputDirectories { get; init; } = [];
}
public sealed record CodingWorkspaceCapture(string TreeDigest, string Diff, IReadOnlyList<string> ChangedFiles)
{
    public IReadOnlyList<string> BoundaryViolations { get; init; } = [];
    public IReadOnlyDictionary<string, string> FileDigests { get; init; } = new Dictionary<string, string>();
}
public interface ICodingWorkspaceService
{
    Task<CodingWorkspaceIdentity> PrepareAsync(CodingWorkspaceRequest request, CancellationToken ct = default);
    Task<CodingWorkspaceCapture> CaptureAsync(CodingWorkspaceIdentity identity, CancellationToken ct = default);
}
