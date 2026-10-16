using System.Text.Json;
using SwarmCore.Abstractions.CodingTasks;
using SwarmCore.Agent.Continual;

namespace SwarmCore.Missions;

public static class CodingRunPolicy
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static bool IsReady(ContinualObjectiveSnapshot state)
        => state.Mission?.CodingRun is null or { Phase: CodingRunPhase.Ready, Workspace: not null };

    private static CodingRunState Assignment(CodingRunState run) => run with
    { Phase = CodingRunPhase.Registered, PreparationOwner = null, PreparationExpiresAtUtc = null,
        Workspace = null, Verification = null, Failure = null };

    private static bool SameCodingAssignment(CodingRunState left, CodingRunState right)
        => JsonSerializer.Serialize(Assignment(left), Json) == JsonSerializer.Serialize(Assignment(right), Json);

    public static bool SameMissionAssignment(ContinualObjectiveSnapshot saved, ContinualObjectiveStart incoming)
    {
        if (saved.Mission?.CodingRun is { } coding && incoming.Mission?.CodingRun is { } requested)
            // The durable request is immutable; later host steering and workspace preparation are mutable state.
            return SameCodingAssignment(coding, requested) && saved.Mission.OwnerId == incoming.Mission.OwnerId
                && saved.Mission.ParentId == incoming.Mission.ParentId;
        return JsonSerializer.Serialize(saved.Mission, Json) == JsonSerializer.Serialize(incoming.Mission, Json)
            && saved.ObjectiveText == incoming.ObjectiveText;
    }

    public static void ValidateRegistration(ContinualObjectiveStart start, Guid profile)
    {
        if (start.Mission?.CodingRun is not { } run) return;
        if (run.RunId == Guid.Empty || run.ProfileId != profile || string.IsNullOrWhiteSpace(run.ConversationId)
            || run.Request.Mode != CodingTaskMode.Verified || string.IsNullOrWhiteSpace(run.Request.TaskKey)
            || run.Request.AcceptanceCriteria.Count == 0 || !Path.IsPathFullyQualified(run.Request.RepoPath ?? "")
            || !Path.IsPathFullyQualified(run.WorkspaceRoot) || run.Phase != CodingRunPhase.Registered
            || run.Workspace is not null || run.Verification is not null || run.PreparationOwner is not null
            || run.PreparationExpiresAtUtc is not null || run.Failure is not null
            || run.RequestHash != MissionVerification.Digest(JsonSerializer.Serialize(run.Request)))
            throw new ArgumentException("Coding registration must contain the immutable host-bound request without prepared state.");
    }

    public static ContinualObjectiveSnapshot? Apply(ContinualObjectiveSnapshot state, long expectedVersion,
        CodingRunState updated, MissionAuthority? preparedResources, DateTimeOffset now)
        {
            if (state.Version != expectedVersion || state.Mission?.CodingRun is not { } prior
                || !SameCodingAssignment(prior, updated)) return null;
            var authority = state.Authority;
            var reopen = updated.Phase == CodingRunPhase.Ready && state.Status == ContinualObjectiveStatus.Completed
                && prior.Phase is CodingRunPhase.Ready or CodingRunPhase.Verified;
            switch (updated.Phase)
            {
                case CodingRunPhase.Preparing:
                    if (ContinualObjectiveStateMachine.IsTerminal(state.Status) || state.Status == ContinualObjectiveStatus.Paused
                        || prior.Workspace is not null || prior.Phase is not (CodingRunPhase.Registered or CodingRunPhase.Preparing)
                        || prior.PreparationExpiresAtUtc > now && prior.PreparationOwner != updated.PreparationOwner
                        || string.IsNullOrWhiteSpace(updated.PreparationOwner) || updated.PreparationExpiresAtUtc is null
                        || updated.PreparationExpiresAtUtc <= now || updated.Workspace is not null
                        || updated.Verification is not null || preparedResources is not null) return null;
                    break;
                case CodingRunPhase.Ready:
                    if (reopen)
                    {
                        if (preparedResources is not null || updated.Verification is not null
                            || updated.PreparationOwner is not null || updated.PreparationExpiresAtUtc is not null
                            || string.IsNullOrWhiteSpace(updated.Failure)
                            || JsonSerializer.Serialize(prior.Workspace, Json) != JsonSerializer.Serialize(updated.Workspace, Json)) return null;
                        break;
                    }
                    if (ContinualObjectiveStateMachine.IsTerminal(state.Status) || state.Status == ContinualObjectiveStatus.Paused
                        || prior.Phase != CodingRunPhase.Preparing || string.IsNullOrWhiteSpace(prior.PreparationOwner)
                        || prior.PreparationExpiresAtUtc is null || prior.PreparationExpiresAtUtc <= now
                        || updated.PreparationOwner is not null || updated.PreparationExpiresAtUtc is not null
                        || updated.Verification is not null || !ValidCodingWorkspace(prior, updated.Workspace)) return null;
                    if (prior.Request.UseExistingCheckout && prior.Request.AdmittedPaths.Count == 0 && preparedResources is null)
                    {
                        // Legacy host callers need no derived authority for an unchanged existing checkout scope.
                    }
                    else
                    {
                        if (!ValidPreparedAuthority(state, updated.Workspace!, preparedResources)) return null;
                        authority = authority with { MissionResources = preparedResources };
                    }
                    break;
                case CodingRunPhase.Verified:
                    if (prior.Phase is not (CodingRunPhase.Ready or CodingRunPhase.Verified)
                        || state.Status != ContinualObjectiveStatus.Completed || preparedResources is not null
                        || updated.PreparationOwner is not null || updated.PreparationExpiresAtUtc is not null
                        || JsonSerializer.Serialize(prior.Workspace, Json) != JsonSerializer.Serialize(updated.Workspace, Json)
                        || updated.Verification is not { } proof || string.IsNullOrWhiteSpace(proof.TreeDigest)
                        || proof.VerifiedAtUtc > now || proof.ConstraintVersion != state.Mission.ConstraintVersion
                        || proof.AuthorityRevision != state.Authority.MissionResources?.Revision
                        || proof.CriteriaDigest != MissionVerification.CriteriaDigest(state)
                        || proof.EvidenceDigest != MissionVerification.EvidenceDigest(state)
                        || MissionVerification.ValidateCompletion(state, state.VerificationHistory.LastOrDefault(), state.LastOutput) is not null
                        || VerifiedCodingTaskExecutor.ValidateFinalEvidence(state, new(proof.TreeDigest, proof.Diff, proof.ChangedFiles)) is not null
                        || !proof.ReceiptReferences.Order(StringComparer.Ordinal).SequenceEqual(
                            MissionVerification.Criteria(state).SelectMany(c => MissionVerification.Check(state, c).EvidenceReferences)
                                .Distinct().Order(StringComparer.Ordinal))) return null;
                    break;
                case CodingRunPhase.Blocked:
                    if (prior.Phase is not (CodingRunPhase.Registered or CodingRunPhase.Preparing)
                        || state.Mission.DeadlineUtc is null || state.Mission.DeadlineUtc > now
                        || ContinualObjectiveStateMachine.IsTerminal(state.Status) || preparedResources is not null
                        || updated.Workspace is not null || updated.Verification is not null
                        || updated.PreparationOwner is not null || updated.PreparationExpiresAtUtc is not null) return null;
                    updated = updated with { Failure = "The assigned coding deadline expired before workspace preparation completed." };
                    break;
                default: return null;
            }
            var expired = updated.Phase == CodingRunPhase.Blocked;
            var delegation = state.Mission.Delegation;
            if (delegation is not null && prior.Workspace is null && updated.Workspace is { } preparedWorkspace)
                delegation = delegation with { WritableBaseHashes = preparedWorkspace.BaselineFiles.ToDictionary(
                    pair => Path.GetFullPath(Path.Combine(preparedWorkspace.WorkspacePath, pair.Key)), pair => pair.Value[(pair.Value.IndexOf(':') + 1)..]) };
            return state with { Mission = state.Mission with { CodingRun = updated, Delegation = delegation },
                Authority = authority, UpdatedAtUtc = now,
                Status = expired ? ContinualObjectiveStatus.Failed : reopen ? ContinualObjectiveStatus.RetryScheduled : state.Status,
                NextAttemptAtUtc = expired ? null : reopen ? now : state.NextAttemptAtUtc,
                LastFailure = expired ? updated.Failure : state.LastFailure,
                VerificationCandidatePending = reopen ? false : state.VerificationCandidatePending,
                ContinuationFeedback = reopen || expired ? updated.Failure : state.ContinuationFeedback };
        }

    private static bool ValidCodingWorkspace(CodingRunState prior, CodingWorkspaceIdentity? workspace)
    {
        if (workspace is null || workspace.RunId != prior.RunId || workspace.UseExistingCheckout != prior.Request.UseExistingCheckout
            || string.IsNullOrWhiteSpace(workspace.BaseCommit) || string.IsNullOrWhiteSpace(workspace.BaselineTreeDigest)
            || !workspace.AdmittedPaths.SequenceEqual(prior.Request.AdmittedPaths)) return false;
        try
        {
            var repo = SandboxPathResolver.ResolvePhysicalPath(prior.Request.RepoPath!);
            var root = SandboxPathResolver.ResolvePhysicalPath(prior.WorkspaceRoot);
            var expected = prior.Request.UseExistingCheckout ? repo : Path.Combine(root, prior.RunId.ToString("N"));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(repo, prior.Request.RepoPath, comparison) && string.Equals(root, prior.WorkspaceRoot, comparison)
                && string.Equals(repo, workspace.RepositoryPath, comparison)
                && string.Equals(expected, workspace.WorkspacePath, comparison)
                && string.Equals(expected, SandboxPathResolver.ResolvePhysicalPath(workspace.WorkspacePath), comparison)
                && (prior.Request.UseExistingCheckout || workspace.Branch == "codex/coding-" + prior.RunId.ToString("N"));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool ValidPreparedAuthority(ContinualObjectiveSnapshot state, CodingWorkspaceIdentity workspace, MissionAuthority? prepared)
    {
        try
        {
            return prepared is not null && state.Authority.MissionResources is { } original
                && JsonSerializer.Serialize(prepared, Json) == JsonSerializer.Serialize(PrepareAuthority(original, workspace), Json);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Preserve all exclusions and also apply source-tree preservation constraints to its isolated copy.</summary>
    public static MissionAuthority PrepareAuthority(MissionAuthority original, CodingWorkspaceIdentity workspace)
    {
        if (workspace.UseExistingCheckout && workspace.AdmittedPaths.Count == 0) return original;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var source = workspace.RepositoryPath.TrimEnd(Path.DirectorySeparatorChar);
        var mapped = original.Exclusions.Where(g => g.Resource.Kind is MissionResourceKind.File or MissionResourceKind.Directory
            && (string.Equals(g.Resource.Identity, source, comparison)
                || g.Resource.Identity.StartsWith(source + Path.DirectorySeparatorChar, comparison)))
            .Select(g => g with { Resource = g.Resource with { Identity = workspace.WorkspacePath + g.Resource.Identity[source.Length..] } });
        const MissionEffect mutable = MissionEffect.Modify | MissionEffect.Remove | MissionEffect.Install;
        var grants = new List<MissionResourceGrant>
        {
            new(new(MissionResourceKind.Directory, workspace.WorkspacePath),
                CodingEffects(original, workspace, new(MissionResourceKind.Directory, workspace.WorkspacePath),
                    MissionEffect.Inspect | MissionEffect.Execute | (workspace.AdmittedPaths.Count == 0 ? mutable : MissionEffect.None)),
                "verified-coding-isolated-workspace", true)
        };
        if (workspace.AdmittedPaths.Count > 0)
            foreach (var relative in workspace.AdmittedPaths.Concat(workspace.GeneratedOutputDirectories).Distinct(StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)
                    || relative.Split(['/', '\\']).Contains("..")) throw new ArgumentException("Invalid admitted workspace path.");
                var full = Path.GetFullPath(Path.Combine(workspace.WorkspacePath, relative));
                var root = workspace.WorkspacePath.TrimEnd(Path.DirectorySeparatorChar);
                if (!string.Equals(full, root, comparison) && !full.StartsWith(root + Path.DirectorySeparatorChar, comparison)
                    || !string.Equals(full, SandboxPathResolver.ResolvePhysicalPath(full), comparison))
                    throw new ArgumentException("Admitted workspace path changed its physical boundary.");
                var directory = Directory.Exists(full) || relative.EndsWith('/') || relative.EndsWith('\\')
                    || workspace.GeneratedOutputDirectories.Contains(relative);
                var resource = new MissionResource(directory ? MissionResourceKind.Directory : MissionResourceKind.File, full);
                grants.Add(new(resource, CodingEffects(original, workspace, resource, mutable),
                    "verified-coding-admitted-path", true));
            }
        return original with
        {
            Grants = grants,
            Exclusions = original.Exclusions.Concat(mapped).Distinct().ToArray()
        };
    }
    private static MissionEffect CodingEffects(MissionAuthority original, CodingWorkspaceIdentity workspace, MissionResource resource, MissionEffect requested)
    {
        if (!workspace.UseExistingCheckout && original.DelegationLineage.Count == 0) return requested;
        if (!workspace.UseExistingCheckout)
        {
            var relative = Path.GetRelativePath(workspace.WorkspacePath, resource.Identity);
            resource = resource with { Identity = Path.GetFullPath(Path.Combine(workspace.RepositoryPath, relative)) };
        }
        var allowed = MissionEffect.None;
        foreach (var effect in new[] { MissionEffect.Inspect, MissionEffect.Execute, MissionEffect.Modify, MissionEffect.Remove, MissionEffect.Install })
            if ((requested & effect) != 0 && original.Evaluate(new(resource, effect, original.PartitionKey)).Allowed) allowed |= effect;
        return allowed;
    }

}
