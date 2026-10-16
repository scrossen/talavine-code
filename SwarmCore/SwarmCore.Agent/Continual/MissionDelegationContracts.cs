namespace SwarmCore.Agent.Continual;

public sealed record MissionDelegationLimits
{
    public int MaximumChildren { get; init; } = 16;
    public int MaximumConcurrency { get; init; } = 3;
    public int MaximumDepth { get; init; } = 3;
}

/// <summary>Model-authored task data; every capability/resource is intersected with the authenticated parent.</summary>
public sealed record MissionDelegationSpec
{
    public required string DelegationId { get; init; }
    public required string Objective { get; init; }
    public string Role { get; init; } = "specialist";
    public required IReadOnlyList<string> SuccessCriteria { get; init; }
    public IReadOnlyList<MissionEvidenceRequirement> EvidenceRequirements { get; init; } = [];
    public required IReadOnlyList<string> AllowedToolIds { get; init; }
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = [];
    public IReadOnlyList<MissionResourceGrant> Resources { get; init; } = [];
    public IReadOnlyList<Guid> DependencyIds { get; init; } = [];
    public IReadOnlyList<string> ParentReceiptIds { get; init; } = [];
    public string? BudgetReference { get; init; }
    public int? BudgetToolCalls { get; init; }
    public DateTimeOffset? DeadlineUtc { get; init; }
}
public sealed record MissionDelegation(MissionDelegationSpec Spec, string SpecificationHash,
    int ParentAuthorityRevision, int ParentConstraintVersion, int Depth)
{
    public bool RetiredByParent { get; init; }
    public string? ContextFingerprint { get; init; }
    public IReadOnlyList<string> BoundReceiptIds { get; init; } = [];
    public IReadOnlyDictionary<string, string> WritableBaseHashes { get; init; } = new Dictionary<string, string>();
}
public sealed record MissionDelegationResult(string Status, ContinualObjectiveSnapshot? Child, string? Reason = null) : SwarmCore.Abstractions.Context.IContextPackEvidence
{
    SwarmCore.Abstractions.Context.ContextPack? SwarmCore.Abstractions.Context.IContextPackEvidence.WorkingContextSourcePack => null;
}
public sealed record MissionDelegationJoin(bool Complete, IReadOnlyList<ContinualObjectiveSnapshot> Children,
    IReadOnlyList<string> Blockers) : SwarmCore.Abstractions.Context.IContextPackEvidence
{
    SwarmCore.Abstractions.Context.ContextPack? SwarmCore.Abstractions.Context.IContextPackEvidence.WorkingContextSourcePack => null;
}

public sealed record MissionJoinWait(IReadOnlyList<Guid> ChildIds, string ProgressFingerprint, int ParentConstraintVersion, int ParentAuthorityRevision);

public sealed class MissionChildSnapshots(IEnumerable<ContinualObjectiveSnapshot> children) : List<ContinualObjectiveSnapshot>(children), SwarmCore.Abstractions.Context.IContextPackEvidence
{
    SwarmCore.Abstractions.Context.ContextPack? SwarmCore.Abstractions.Context.IContextPackEvidence.WorkingContextSourcePack => null;
}
