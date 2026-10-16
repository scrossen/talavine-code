using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.CodingTasks;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;

namespace SwarmCore.Missions;

/// <summary>Joins a host activation to authoritative, profile-bound mission proof.</summary>
public sealed class CodingRepairActivationService(IMissionStore missions, ICodingWorkspaceService workspaces,
    ICurrentTurnContextAccessor context, IOptions<CodingTaskOptions> options, ICodingRepairActivationHost host)
{
    public async Task<CodingRepairActivationResult> ExecuteAsync(Guid missionId, bool activate, CancellationToken ct)
    {
        CodingRepairActivationResult Blocked(string reason) => new() { MissionId = missionId, Detail = reason };
        var caller = context.Current;
        if (caller?.ProfileId is not { } profile || caller.RepairAuthority is null || caller.MissionObjectiveId is not null)
            return Blocked("Activation requires the host's top-level repair authority and authenticated profile.");
        var mission = await missions.GetAsync(missionId, ct);
        var run = mission?.Mission?.CodingRun;
        if (run is null || run.ProfileId != profile || run.ConversationId != caller.ConversationId)
            return Blocked("The coding mission does not belong to this repair conversation and profile.");
        if (mission!.Status != ContinualObjectiveStatus.Completed || run.Phase != CodingRunPhase.Verified
            || run.Verification is not { } proof || run.Workspace is not { UseExistingCheckout: false } workspace)
            return Blocked("A completed, verified isolated coding workspace is required.");
        // Status remains readable after relaunch: the newly compiled host is bound to the
        // activated workspace, whereas the original mission names its parent repository.
        var existing = await host.GetStatusAsync(missionId, run.RunId, proof.TreeDigest, ct);
        if (!activate || existing.State != "NotActivated") return existing;
        if (!SamePath(options.Value.DefaultRepoPath, workspace.RepositoryPath))
            return Blocked("The verified patch is outside the configured repair repository.");
        async Task Validate(CancellationToken cancel)
        {
            var current = await missions.GetAsync(missionId, cancel);
            if (current is null || current.Version != mission.Version || current.Status != ContinualObjectiveStatus.Completed
                || proof.ConstraintVersion != mission.Mission!.ConstraintVersion
                || proof.AuthorityRevision != mission.Authority.MissionResources?.Revision
                || proof.CriteriaDigest != MissionVerification.CriteriaDigest(mission)
                || proof.EvidenceDigest != MissionVerification.EvidenceDigest(mission))
                throw new InvalidOperationException("Coding acceptance changed; verify the mission again before activation.");
            var capture = await workspaces.CaptureAsync(workspace, cancel);
            if (capture.TreeDigest != proof.TreeDigest || capture.BoundaryViolations.Count != 0
                || VerifiedCodingTaskExecutor.ValidateFinalEvidence(mission, capture) is not null)
                throw new InvalidOperationException("Verified source changed; activation was stopped. Reverify the retained workspace.");
        }
        try
        {
            await Validate(ct);
            return await host.ActivateAsync(missionId, run, Validate, ct);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        { return Blocked(ex.Message); }
    }

    private static bool SamePath(string? left, string right) => !string.IsNullOrWhiteSpace(left)
        && string.Equals(Path.TrimEndingDirectorySeparator(SandboxPathResolver.ResolvePhysicalPath(Path.GetFullPath(left))),
            Path.TrimEndingDirectorySeparator(SandboxPathResolver.ResolvePhysicalPath(Path.GetFullPath(right))),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
