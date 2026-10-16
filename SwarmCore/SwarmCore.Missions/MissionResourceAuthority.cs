using SwarmCore.Agent.Continual;

namespace SwarmCore.Missions;

/// <summary>Trusted host assignment adapter. Do not expose these inputs as model-created grants.</summary>
public static class MissionResourceAuthority
{
    public static MissionAuthority LocalWorkspace(Guid profile, string principal, string assignment, string directory)
        => new()
        {
            PrincipalId = principal, PartitionKey = profile.ToString("D"), AssignmentReference = assignment,
            Grants = [new(new(MissionResourceKind.Directory, SandboxPathResolver.ResolvePhysicalPath(directory)),
                MissionEffect.Inspect | MissionEffect.Modify | MissionEffect.Remove | MissionEffect.Execute | MissionEffect.Install,
                "host-assigned-workspace")],
        };

    public static MissionResource File(string path)
        => new(MissionResourceKind.File, SandboxPathResolver.ResolvePhysicalPath(path));
}
