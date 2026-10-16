using SwarmCore.Abstractions.CodingTasks;
using SwarmCore.Abstractions.Security;
using SwarmCore.Agent.Continual;

namespace SwarmCore.Missions;

/// <summary>Tool presentation omits host verification inventories; internal inspection and persistence stay complete.</summary>
public static class MissionToolSnapshotProjection
{
    public static ContinualObjectiveSnapshot? ForTool(ContinualObjectiveSnapshot? snapshot)
    {
        if (snapshot?.Mission is not { } mission) return snapshot;
        var coding = mission.CodingRun;
        var security = mission.SecurityRun;
        if (coding?.Workspace is { } workspace)
            coding = coding with { Workspace = CodingTaskResultProjection.WorkspaceForPresentation(workspace) };
        if (security?.Report is { } report)
            security = security with { Report = SecurityReportProjection.Fit(report) };
        return snapshot with { Mission = mission with { CodingRun = coding, SecurityRun = security } };
    }
}
