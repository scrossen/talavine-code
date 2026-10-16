namespace SwarmCore.Agent.Continual;

[Flags]
public enum MissionEffect
{
    None = 0, Inspect = 1, Modify = 2, Remove = 4, Execute = 8, Install = 16,
    Send = 32, Deploy = 64, Financial = 128, DestroyProductionData = 256,
}

public enum MissionResourceKind { File, Directory, Endpoint, Environment, Profile, WebOrigin }
public enum MissionAuthorityDecision { ExecuteWithinMission, SkipOutsideMission }

/// <summary>Canonical host resource identity; never an executable model instruction.</summary>
public sealed record MissionResource(MissionResourceKind Kind, string Identity);
public sealed record MissionResourceGrant(MissionResource Resource, MissionEffect Effects,
    string Provenance, bool Explicit = false);
public sealed record MissionAction(MissionResource Resource, MissionEffect Effects, string PartitionKey);
public sealed record MissionAuthorityResult(MissionAuthorityDecision Decision, string ReasonCode)
{
    public bool Allowed => Decision == MissionAuthorityDecision.ExecuteWithinMission;
}

/// <summary>Durable host-authored resource authority. Tool names and model rationale grant nothing.</summary>
public sealed record MissionAuthority
{
    public int SchemaVersion { get; init; } = 1;
    public int Revision { get; init; } = 1;
    public required string PrincipalId { get; init; }
    public required string PartitionKey { get; init; }
    public required string AssignmentReference { get; init; }
    public IReadOnlyList<MissionResourceGrant> Grants { get; init; } = [];
    public IReadOnlyList<MissionResourceGrant> Exclusions { get; init; } = [];
    public IReadOnlyList<Guid> DelegationLineage { get; init; } = [];
    public IReadOnlyDictionary<Guid, int> DelegationAuthorityRevisions { get; init; } = new Dictionary<Guid, int>();
    public bool AssignmentInterpreted { get; init; }
    public SwarmCore.Abstractions.Context.PersonalRecurrenceIntent? PersonalRecurrence { get; init; }
    public string? InitialAuthorityHash { get; init; }
    public string? DeploymentRequestEvidence { get; init; }
    public IReadOnlyDictionary<string, string> EnvironmentBindings { get; init; } = new Dictionary<string, string>();

    public MissionAuthorityResult Evaluate(MissionAction action)
    {
        MissionAuthorityResult Skip(string reason) => new(MissionAuthorityDecision.SkipOutsideMission, reason);
        if (SchemaVersion != 1 || Revision < 1 || string.IsNullOrWhiteSpace(PrincipalId)
            || string.IsNullOrWhiteSpace(AssignmentReference)) return Skip("invalid-authority");
        if (PartitionKey != action.PartitionKey) return Skip("partition-mismatch");
        if (action.Effects == MissionEffect.None) return Skip("unclassified-effect");
        if (Exclusions.Any(g => Covers(g.Resource, action.Resource) && (g.Effects & action.Effects) != 0))
            return Skip("explicit-exclusion");
        const MissionEffect highImpact = MissionEffect.Send | MissionEffect.Deploy | MissionEffect.Financial | MissionEffect.DestroyProductionData;
        var granted = MissionEffect.None;
        foreach (var grant in Grants.Where(g => Covers(g.Resource, action.Resource) && !string.IsNullOrWhiteSpace(g.Provenance)))
            granted |= grant.Explicit ? grant.Effects : grant.Effects & ~highImpact;
        return (granted & action.Effects) == action.Effects
            ? new(MissionAuthorityDecision.ExecuteWithinMission, "mission-resource-grant")
            : Skip("outside-mission-resources-or-effects");
    }

    public MissionAuthority Delegate(Guid parentId, string principalId)
        => this with { PrincipalId = principalId, DelegationLineage = [.. DelegationLineage, parentId], AssignmentInterpreted = true,
            DelegationAuthorityRevisions = new Dictionary<Guid, int>(DelegationAuthorityRevisions) { [parentId] = Revision } };

    private static bool Covers(MissionResource grant, MissionResource action)
    {
        if (grant.Kind == MissionResourceKind.WebOrigin && action.Kind == MissionResourceKind.Endpoint
            && Uri.TryCreate(grant.Identity, UriKind.Absolute, out var origin)
            && Uri.TryCreate(action.Identity, UriKind.Absolute, out var endpoint)
            && origin.Scheme is "http" or "https" && endpoint.Scheme == origin.Scheme
            && origin.Authority == endpoint.Authority && origin.AbsolutePath == "/" && origin.Query.Length == 0)
            return true;
        var file = action.Kind is MissionResourceKind.File or MissionResourceKind.Directory;
        var comparison = file && OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (grant.Kind == action.Kind && string.Equals(grant.Identity, action.Identity, comparison)) return true;
        return file && grant.Kind == MissionResourceKind.Directory
            && action.Identity.StartsWith(grant.Identity.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison);
    }
}
