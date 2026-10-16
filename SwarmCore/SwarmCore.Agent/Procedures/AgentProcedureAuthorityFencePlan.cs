namespace SwarmCore.Agent.Procedures;

public enum AgentProcedureAuthorityBoundary { Manual, ToolPolicy, Account, Source, ProfileSelection, Route, RemoteAuthority }
public enum AgentProcedureAuthorityResourceKind { SqliteDatabase, File }
public sealed record AgentProcedureAuthorityFenceResource(string HostPath, AgentProcedureAuthorityResourceKind Kind,
    IReadOnlyList<AgentProcedureAuthorityBoundary> Boundaries);

/// <summary>
/// Finite host-only writer-fence inventory. Never a serialized candidate's authority or model/tool input.
/// Coverage labels do not prove their meaning: the owning host must resolve actual writer paths,
/// re-read all scoped baselines while the locks are held and verify the remote authority separately.
/// Only the owning terminal/head database may be written under this evidence fence.
/// </summary>
public sealed class AgentProcedureAuthorityFencePlan
{
    public AgentProcedureAuthorityFencePlan(string profileId, IEnumerable<AgentProcedureAuthorityFenceResource> resources,
        bool requiresRemoteAuthority)
    {
        var values = resources.Take(33).Select(r => r with { Boundaries = Array.AsReadOnly(r.Boundaries.ToArray()) }).ToArray();
        if (string.IsNullOrWhiteSpace(profileId) || profileId.Length > 200 || values.Length is < 1 or > 32
            || values.Any(r => string.IsNullOrWhiteSpace(r.HostPath) || r.HostPath.Length > 4096 || !Enum.IsDefined(r.Kind)
                || r.Boundaries.Count == 0 || r.Boundaries.Any(b => !Enum.IsDefined(b)) || r.Boundaries.Distinct().Count() != r.Boundaries.Count)
            || Enum.GetValues<AgentProcedureAuthorityBoundary>().Where(b => b != AgentProcedureAuthorityBoundary.RemoteAuthority || requiresRemoteAuthority)
                .Any(b => !values.Any(r => r.Boundaries.Contains(b))))
            throw new ArgumentException("The complete finite actual writer-fence inventory is required.");
        ProfileId = profileId; Resources = Array.AsReadOnly(values); RequiresRemoteAuthority = requiresRemoteAuthority;
    }
    public string ProfileId { get; }
    public IReadOnlyList<AgentProcedureAuthorityFenceResource> Resources { get; }
    public bool RequiresRemoteAuthority { get; }
}
