namespace SwarmCore.Abstractions.CodingTasks;

public sealed record CodingRepairReadiness(string? RepositoryPath, bool CanActivate, string Detail);

/// <summary>Activation is separate from coding acceptance. Running still requires the original live probe.</summary>
public sealed record CodingRepairActivationResult
{
    public bool Success { get; init; }
    public string State { get; init; } = "Blocked";
    public Guid MissionId { get; init; }
    public Guid RunId { get; init; }
    public string? TreeDigest { get; init; }
    public string? RuntimeInstanceId { get; init; }
    public DateTimeOffset? ActivatedAtUtc { get; init; }
    public string Detail { get; init; } = "";
}

/// <summary>Host-owned build/relaunch adapter. No command or executable comes from model arguments.</summary>
public interface ICodingRepairActivationHost
{
    string RuntimeInstanceId { get; }
    CodingRepairReadiness GetReadiness();
    Task<CodingRepairActivationResult> GetStatusAsync(Guid missionId, Guid runId, string treeDigest, CancellationToken ct);
    Task<CodingRepairActivationResult> ActivateAsync(Guid missionId, CodingRunState run,
        Func<CancellationToken, Task> validateSource, CancellationToken ct);
}
