using System.Text.Json;
using SwarmCore.Abstractions.Security;
using SwarmCore.Agent.Continual;
namespace SwarmCore.Missions;

public static class SecurityRunPolicy
{
    public static bool HasVerifiedCoverage(ContinualObjectiveSnapshot state)
    {
        if (state.Status != ContinualObjectiveStatus.Completed || state.Mission?.SecurityRun is not { Phase: SecurityRunPhase.Complete, Report: { } report } run
            || report.Phase != SecurityRunPhase.Complete || report.Error is not null || string.IsNullOrWhiteSpace(report.TargetTreeDigest)
            || report.MissionId != state.Id || report.RunId != run.RunId || report.RequestHash != run.RequestHash
            || report.RepositoryPath != run.Request.RepositoryPath || report.ExecutionWorkspacePath != run.Workspace?.WorkspacePath) return false;
        var requested = run.Request.ReproductionChecks.Select(c => c.RuleId)
            .Concat(run.Request.AuditDependencies ? ["dotnet-package-audit"] : Array.Empty<string>()).ToArray();
        return requested.Length > 0 && report.Coverage.Count > 0
            && report.Coverage.All(c => c.Status == SecurityCoverageStatus.Completed && c.EvidenceReferences.Count > 0
                && c.EvidenceReferences.All(id => report.Commands.Any(command => command.Id == id
                    && command.Settled && !command.Cancelled && !command.TimedOut && command.ExitCode is not null)))
            && requested.All(scanner => report.Coverage.Any(c => c.Scanner == scanner));
    }
    private static SecurityRunState Assignment(SecurityRunState run) => run with
    { Phase = SecurityRunPhase.Registered, Owner = null, OwnerExpiresUtc = null, Attempt = 0, Report = null, Workspace = null };
    public static bool IsReady(ContinualObjectiveSnapshot state) => state.Mission?.SecurityRun is null;
    private static bool Same(SecurityRunState a, SecurityRunState b)
        => JsonSerializer.Serialize(Assignment(a)) == JsonSerializer.Serialize(Assignment(b));
    public static bool SameMissionAssignment(ContinualObjectiveSnapshot saved, ContinualObjectiveStart incoming)
        => saved.Mission?.SecurityRun is { } a && incoming.Mission?.SecurityRun is { } b
            ? Same(a,b) && saved.Mission.OwnerId == incoming.Mission.OwnerId && saved.Mission.ParentId == incoming.Mission.ParentId
            : CodingRunPolicy.SameMissionAssignment(saved,incoming);
    public static void ValidateRegistration(ContinualObjectiveStart start, Guid profile)
    {
        if (start.Mission?.SecurityRun is not { } run) return;
        if (run.RunId == Guid.Empty || run.ProfileId != profile || string.IsNullOrWhiteSpace(run.ConversationId)
            || string.IsNullOrWhiteSpace(run.Request.TaskKey) || !Path.IsPathFullyQualified(run.Request.RepositoryPath)
            || run.RequestHash != MissionVerification.Digest(JsonSerializer.Serialize(run.Request))
            || JsonSerializer.Serialize(run) != JsonSerializer.Serialize(Assignment(run))
            || start.Mission.ParentId is not null && (start.Mission.Delegation is null || !Path.IsPathFullyQualified(run.WorkspaceRoot ?? "")) || start.Mission.CodingRun is not null)
            throw new ArgumentException("Security registration requires an immutable authenticated assignment without execution state.");
    }
    public static ContinualObjectiveSnapshot? PrepareWorkspace(ContinualObjectiveSnapshot state, long expectedVersion,
        SwarmCore.Abstractions.CodingTasks.CodingWorkspaceIdentity workspace, DateTimeOffset now)
    {
        if (state.Version != expectedVersion || state.Mission?.SecurityRun is not { } run
            || state.Mission.ParentId is null || state.Mission.Delegation is null || run.Workspace is not null
            || run.Phase != SecurityRunPhase.Running || run.OwnerExpiresUtc <= now
            || state.Status is ContinualObjectiveStatus.Cancelled or ContinualObjectiveStatus.Paused or ContinualObjectiveStatus.Failed
            || state.Mission.DeadlineUtc <= now || workspace.UseExistingCheckout || workspace.RunId != run.RunId
            || workspace.RepositoryPath != run.Request.RepositoryPath || run.WorkspaceRoot is null
            || workspace.WorkspacePath != Path.Combine(run.WorkspaceRoot, run.RunId.ToString("N"))
            || workspace.WorkspacePath != SandboxPathResolver.ResolvePhysicalPath(workspace.WorkspacePath)
            || workspace.Branch != "codex/coding-" + run.RunId.ToString("N")) return null;
        return state with
        {
            Mission = state.Mission with { SecurityRun = run with { Workspace = workspace } },
            Authority = state.Authority with { MissionResources = CodingRunPolicy.PrepareAuthority(state.Authority.MissionResources!, workspace) },
            UpdatedAtUtc = now
        };
    }

    public static ContinualObjectiveSnapshot? Apply(ContinualObjectiveSnapshot state, long expectedVersion,
        SecurityRunState updated, DateTimeOffset now)
    {
        var settlingControl=state.Status is ContinualObjectiveStatus.Paused or ContinualObjectiveStatus.Cancelled
            && updated.Phase==SecurityRunPhase.Partial;
        if (state.Version != expectedVersion || state.Mission?.SecurityRun is not { } prior || !Same(prior,updated)
            || JsonSerializer.Serialize(prior.Workspace) != JsonSerializer.Serialize(updated.Workspace)
            || state.Status==ContinualObjectiveStatus.Failed
            || state.Status is ContinualObjectiveStatus.Paused or ContinualObjectiveStatus.Cancelled && !settlingControl)
            return null;
        var expiredRegistration=updated.Phase==SecurityRunPhase.Blocked && prior.Phase is SecurityRunPhase.Registered or SecurityRunPhase.Partial
            && prior.Owner is null && updated.Owner is null && updated.Attempt==prior.Attempt && state.Mission.DeadlineUtc<=now;
        if (updated.Phase == SecurityRunPhase.Running)
        {
            if (string.IsNullOrWhiteSpace(updated.Owner) || updated.OwnerExpiresUtc <= now || updated.OwnerExpiresUtc is null
                || state.Mission.DeadlineUtc <= now) return null;
            if (prior.Phase == SecurityRunPhase.Running && prior.OwnerExpiresUtc > now)
            {
                if (updated.Owner != prior.Owner || updated.Attempt != prior.Attempt) return null;
            }
            else if (updated.Attempt != prior.Attempt + 1 || updated.Owner == prior.Owner
                || updated.Attempt > (state.Mission.MaximumAttempts ?? int.MaxValue)) return null;
        }
        else if(!expiredRegistration)
        {
            if (prior.Phase != SecurityRunPhase.Running || prior.OwnerExpiresUtc <= now
                || updated.Owner != prior.Owner || updated.Attempt != prior.Attempt
                || updated.Phase is not (SecurityRunPhase.Complete or SecurityRunPhase.Partial or SecurityRunPhase.Blocked)) return null;
        }
        if (updated.Phase != SecurityRunPhase.Running && updated.Report is null) return null;
        if (prior.Report is { } oldReport && updated.Report is { } newReport
            && (newReport.Commands.Count < oldReport.Commands.Count || !oldReport.Commands.Select(c=>JsonSerializer.Serialize(c))
                .SequenceEqual(newReport.Commands.Take(oldReport.Commands.Count).Select(c=>JsonSerializer.Serialize(c))))) return null;
        if (updated.Report is { } report && (report.Phase != updated.Phase || report.RunId != prior.RunId || report.MissionId != state.Id
            || report.RequestHash != prior.RequestHash || report.RepositoryPath != prior.Request.RepositoryPath
            || report.ExecutionWorkspacePath != prior.Workspace?.WorkspacePath
            || report.Commands.Count > 256 || report.Findings.Count > 256
            || report.Findings.Select(f=>f.Fingerprint).Distinct().Count() != report.Findings.Count
            || report.Findings.Any(f=>f.Status == SecurityFindingStatus.Accepted
                || f.Status == SecurityFindingStatus.Fixed && (report.Repair?.Verification is null
                    || f.FixedTreeDigest != report.Repair.Verification.TreeDigest)))) return null;
        if (updated.Phase != SecurityRunPhase.Running) updated = updated with { Owner = null, OwnerExpiresUtc = null };
        return state with { Mission = state.Mission with { SecurityRun = updated }, UpdatedAtUtc = now,
            Status = settlingControl ? state.Status : updated.Phase == SecurityRunPhase.Complete ? ContinualObjectiveStatus.Completed
                : updated.Phase == SecurityRunPhase.Blocked ? ContinualObjectiveStatus.Failed : ContinualObjectiveStatus.RetryScheduled,
            NextAttemptAtUtc = null, LastOutput = "Security coverage and findings are stored in the mission report." };
    }
}
