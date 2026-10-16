namespace SwarmCore.Agent.Continual;

/// <summary>The host binds immutable evidence storage to its profile and protection policy.</summary>
public interface IMissionEvidenceArchive
{
    Task<string> PreserveAsync(ContinualToolReceipt receipt, CancellationToken ct);
    Task RestoreArtifactAsync(Guid objectiveId, string digest, Stream destination, CancellationToken ct);
}

/// <summary>Host source repositories own current access and durable-state observation.</summary>
public interface IMissionCurrentStateEvidence
{
    Task<IReadOnlyList<MissionObservedState>> CaptureAsync(ContinualObjectiveSnapshot objective, ContinualToolReceipt receipt, CancellationToken ct);
    Task<string?> ValidateAsync(ContinualObjectiveSnapshot objective, CancellationToken ct);
}
