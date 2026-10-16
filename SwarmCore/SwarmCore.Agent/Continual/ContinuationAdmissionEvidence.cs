namespace SwarmCore.Agent.Continual;

/// <summary>Runtime-only authority evidence for one next scheduled continuation; never a grant.</summary>
public sealed record ContinuationAdmissionScope(Guid ObjectiveId, string ConversationId, Guid ProfileId, string WorkspaceRoot)
{
    public DateTime ObjectiveUpdatedAtUtc { get; init; }
    public int AttemptCount { get; init; }
}
public sealed record AdmissionLane(string ReferenceId, string Provider, string Model);
public enum CapabilityAdmissionState { Unknown, Denied, Admitted, Unavailable }
public sealed record CapabilityLaneAdmission(string LaneReference, CapabilityAdmissionState State);
public sealed record CapabilityAdmissionEntry(string ReferenceId, string ToolId, IReadOnlyList<CapabilityLaneAdmission> Lanes)
{ public string PolicyProjection { get; init; } = ""; }
public sealed record ContinuationAdmissionEvidence(ContinuationAdmissionScope Scope, Guid InstanceId,
    DateTime CapturedAtUtc, bool Complete, bool NativeScopeExcluded,
    IReadOnlyList<AdmissionLane> CoveredLanes, IReadOnlyList<CapabilityAdmissionEntry> Capabilities)
{
    public const string Purpose = "NextScheduledCodingContinuation";
    public const int MaxLanes = 64;
    public const int MaxCapabilities = 512;

    public ContinuationAdmissionEvidence Freeze()
    {
        if (Scope is null || Scope.ConversationId is null || Scope.ConversationId.Length > 200
            || Scope.WorkspaceRoot is null || Scope.WorkspaceRoot.Length > 4096
            || CoveredLanes is null || Capabilities is null || CoveredLanes.Count > MaxLanes || Capabilities.Count > MaxCapabilities
            || CoveredLanes.Any(x => x is null || x.ReferenceId is null || x.ReferenceId.Length > 128
                || x.Provider is null || x.Provider.Length > 128 || x.Model is null || x.Model.Length > 512)
            || Capabilities.Any(x => x is null || x.ReferenceId is null || x.ReferenceId.Length > 128
                || x.ToolId is null || x.ToolId.Length > 256 || x.PolicyProjection is null || x.PolicyProjection.Length > 256
                || x.Lanes is null || x.Lanes.Count > MaxLanes
                || x.Lanes.Any(y => y is null || y.LaneReference is null || y.LaneReference.Length > 128)))
            return new(new(Guid.Empty, "", Guid.Empty, ""), Guid.Empty, DateTime.UtcNow, false, false, [], []);
        return this with
        {
            CoveredLanes = Array.AsReadOnly(CoveredLanes.Select(x => x with { }).ToArray()),
            Capabilities = Array.AsReadOnly(Capabilities.Select(x => x with
            { Lanes = Array.AsReadOnly(x.Lanes.Take(MaxLanes + 1).Select(y => y with { }).ToArray()) }).ToArray()),
        };
    }
}

public enum RequiredCapabilityResolution { None, Unavailable, StillPossible, ProvedDenied }

/// <summary>Validates auditor-selected necessity references separately from action/observation receipts.</summary>
public static class ContinuationAdmissionValidation
{
    public static RequiredCapabilityResolution Resolve(IReadOnlyList<string>? required,
        IReadOnlyList<string>? actionReferences, IReadOnlyList<string>? observedReferences,
        ContinuationAdmissionScope? expected, ContinuationAdmissionEvidence? evidence, DateTime cutoffUtc)
    {
        if (required is null || required.Count == 0) return RequiredCapabilityResolution.None;
        evidence = evidence?.Freeze();
        if (required.Count > ContinuationAdmissionEvidence.MaxCapabilities || expected is null || evidence is null
            || expected.ObjectiveId == Guid.Empty || expected.ProfileId == Guid.Empty
            || string.IsNullOrWhiteSpace(expected.ConversationId) || expected.ConversationId.Length > 200
            || string.IsNullOrWhiteSpace(expected.WorkspaceRoot) || expected.WorkspaceRoot.Length > 4096
            || evidence.InstanceId == Guid.Empty || evidence.Scope != expected
            || evidence.CapturedAtUtc > cutoffUtc || evidence.CapturedAtUtc < cutoffUtc.AddMinutes(-1)
            || required.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 128)
            || required.Distinct(StringComparer.Ordinal).Count() != required.Count
            || required.Intersect(actionReferences ?? [], StringComparer.Ordinal).Any()
            || required.Intersect(observedReferences ?? [], StringComparer.Ordinal).Any())
            return RequiredCapabilityResolution.Unavailable;
        var lanes = evidence.CoveredLanes;
        if (lanes.Count == 0 || lanes.Count > ContinuationAdmissionEvidence.MaxLanes
            || evidence.Capabilities.Count > ContinuationAdmissionEvidence.MaxCapabilities
            || lanes.Any(x => string.IsNullOrWhiteSpace(x.ReferenceId) || string.IsNullOrWhiteSpace(x.Provider) || string.IsNullOrWhiteSpace(x.Model) || x.ReferenceId.Length > 128 || x.Provider.Length > 128 || x.Model.Length > 512)
            || lanes.Select(x => x.ReferenceId).Distinct(StringComparer.Ordinal).Count() != lanes.Count
            || evidence.Capabilities.Select(x => x.ReferenceId).Distinct(StringComparer.Ordinal).Count() != evidence.Capabilities.Count
            || evidence.Capabilities.Select(x => x.ToolId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != evidence.Capabilities.Count)
            return RequiredCapabilityResolution.Unavailable;
        if (!evidence.Complete) return RequiredCapabilityResolution.Unavailable;
        var allDenied = evidence.NativeScopeExcluded;
        foreach (var reference in required)
        {
            if (!reference.StartsWith($"capability-admission:{evidence.InstanceId:N}:", StringComparison.Ordinal))
                return RequiredCapabilityResolution.Unavailable;
            var entry = evidence.Capabilities.SingleOrDefault(x => x.ReferenceId == reference);
            if (entry is null || string.IsNullOrWhiteSpace(entry.ToolId) || entry.ToolId.Length > 256 || entry.Lanes.Count != lanes.Count
                || entry.Lanes.Select(x => x.LaneReference).Distinct(StringComparer.Ordinal).Count() != lanes.Count
                || entry.Lanes.Any(x => !lanes.Any(y => y.ReferenceId == x.LaneReference) || !Enum.IsDefined(x.State)))
                return RequiredCapabilityResolution.Unavailable;
            if (entry.Lanes.Any(x => x.State is CapabilityAdmissionState.Unknown or CapabilityAdmissionState.Unavailable))
                return RequiredCapabilityResolution.Unavailable;
            allDenied &= entry.Lanes.All(x => x.State == CapabilityAdmissionState.Denied);
        }
        return allDenied ? RequiredCapabilityResolution.ProvedDenied : RequiredCapabilityResolution.StillPossible;
    }
}
