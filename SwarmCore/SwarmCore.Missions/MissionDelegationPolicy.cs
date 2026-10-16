using SwarmCore.Agent.Continual;

namespace SwarmCore.Missions;

/// <summary>Pure admission rules. The host re-evaluates these inside its atomic writer transaction.</summary>
public static class MissionDelegationPolicy
{
    public static string? ValidateCreate(ContinualObjectiveSnapshot parent, IReadOnlyList<ContinualObjectiveSnapshot> existingTree,
        ContinualObjectiveStart child, MissionDelegationLimits limits, DateTimeOffset now)
    {
        if (limits.MaximumChildren < 1 || limits.MaximumConcurrency < 1 || limits.MaximumDepth < 1) return "invalid-delegation-limits";
        if (!ParentActive(parent) || parent.LeaseExpiresAtUtc <= now) return "parent-not-active";
        if (child.Mission?.Delegation is not { } d || child.Mission.ParentId != parent.Id) return "missing-delegation-parent";
        if (child.PartitionKey != parent.PartitionKey || child.Authority.MissionResources?.PartitionKey != parent.PartitionKey) return "cross-profile-delegation";
        if (d.ParentAuthorityRevision != parent.Authority.MissionResources?.Revision || d.ParentConstraintVersion != parent.Mission?.ConstraintVersion) return "parent-authority-changed";
        if (d.Depth != (parent.Mission?.Delegation?.Depth ?? 0) + 1 || d.Depth > limits.MaximumDepth) return "delegation-depth-limit";
        var lineage = child.Authority.MissionResources!.DelegationLineage;
        if (lineage.Distinct().Count() != lineage.Count || !lineage.Contains(parent.Id)) return "delegation-cycle";
        if (existingTree.Count >= limits.MaximumChildren) return "delegation-count-limit";
        if ((d.Spec.BudgetToolCalls is not null || d.Spec.BudgetReference is not null) && child.Mission.BudgetReference is null) return "budget-reservation-adapter-unavailable";
        if (d.Spec.BudgetToolCalls < 0 || parent.Mission?.BudgetReference is { } budget && child.Mission.BudgetReference != budget) return "child-budget-outside-parent";
        if (string.IsNullOrWhiteSpace(d.Spec.DelegationId) || d.Spec.DelegationId.Length > 200
            || string.IsNullOrWhiteSpace(d.Spec.Objective) || d.Spec.Objective.Length > 32000
            || d.Spec.SuccessCriteria.Count == 0 || d.Spec.AllowedToolIds.Count == 0) return "invalid-child-specification";
        if (d.Spec.DependencyIds.Distinct().Count() != d.Spec.DependencyIds.Count
            || d.Spec.DependencyIds.Any(id => !existingTree.Any(c => c.Id == id && c.Mission?.ParentId == parent.Id && c.PartitionKey == parent.PartitionKey))) return "invalid-dependency";
        if (child.Mission.DeadlineUtc <= now || parent.Mission?.DeadlineUtc is { } deadline && (child.Mission.DeadlineUtc is null || child.Mission.DeadlineUtc > deadline)) return "invalid-child-deadline";
        var actualAuthority = child.Authority.MissionResources;
        var parentAuthority = parent.Authority.MissionResources!;
        if (actualAuthority is null || actualAuthority.Grants.Any(g => !parentAuthority.Evaluate(new(g.Resource, g.Effects, parent.PartitionKey)).Allowed)
            || !actualAuthority.Grants.SequenceEqual(d.Spec.Resources)
            || parentAuthority.Exclusions.Any(e => !actualAuthority.Exclusions.Contains(e))
            || !actualAuthority.DelegationLineage.SequenceEqual(parentAuthority.DelegationLineage.Append(parent.Id))
            || parentAuthority.DelegationAuthorityRevisions.Append(new KeyValuePair<Guid, int>(parent.Id, parentAuthority.Revision))
                .Any(p => !actualAuthority.DelegationAuthorityRevisions.TryGetValue(p.Key, out var revision) || revision != p.Value)
            || child.Authority.AuthorityId != parent.Authority.AuthorityId || child.Authority.ScopeKind != parent.Authority.ScopeKind
            || child.Authority.EffectiveScopeValues.Except(parent.Authority.EffectiveScopeValues).Any()) return "child-authority-not-intersected";
        if (d.Spec.Resources.Any(g => !parent.Authority.MissionResources!.Evaluate(new(g.Resource, g.Effects, parent.PartitionKey)).Allowed)) return "resource-outside-parent";
        return null;
    }

    public static string? CanClaim(ContinualObjectiveSnapshot child, ContinualObjectiveSnapshot parent,
        IReadOnlyList<ContinualObjectiveSnapshot> tree, MissionDelegationLimits limits, DateTimeOffset now)
    {
        if (child.Mission?.Delegation is not { } d) return null;
        if (d.RetiredByParent) return "child-retired-by-parent";
        if (!ParentActive(parent) || child.PartitionKey != parent.PartitionKey || child.Mission.ParentId != parent.Id) return "parent-not-active";
        if (d.ParentAuthorityRevision != parent.Authority.MissionResources?.Revision || d.ParentConstraintVersion != parent.Mission?.ConstraintVersion) return "parent-authority-changed";
        if (child.Mission.DeadlineUtc <= now) return null; // Existing processor records deadline expiry before any model/tool work.
        if (d.Spec.DependencyIds.Any(id => !tree.Any(c => c.Id == id && c.PartitionKey == child.PartitionKey
            && c.Mission?.ParentId == parent.Id && IsVerified(c)))) return "dependencies-unresolved";
        var active = tree.Where(c => c.Id != child.Id && c.LeaseToken is not null && c.LeaseExpiresAtUtc > now).ToArray();
        if (active.Length >= limits.MaximumConcurrency) return "child-concurrency-limit";
        if (active.Any(c => WritableConflict(child, c))) return "writable-resource-in-use";
        return null;
    }

    public static bool WritableConflict(ContinualObjectiveSnapshot first, ContinualObjectiveSnapshot second)
    {
        const MissionEffect write = MissionEffect.Modify | MissionEffect.Remove | MissionEffect.Execute | MissionEffect.Install | MissionEffect.Send | MissionEffect.Deploy | MissionEffect.Financial | MissionEffect.DestroyProductionData;
        var a = first.Authority.MissionResources?.Grants.Where(g => (g.Effects & write) != 0) ?? [];
        var b = second.Authority.MissionResources?.Grants.Where(g => (g.Effects & write) != 0) ?? [];
        return a.Any(x => b.Any(y => Overlaps(x.Resource, y.Resource)));
    }
    private static bool Overlaps(MissionResource a, MissionResource b)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (a.Kind == b.Kind && string.Equals(a.Identity, b.Identity, comparison)) return true;
        bool Contains(MissionResource directory, MissionResource file) => directory.Kind == MissionResourceKind.Directory
            && file.Kind is MissionResourceKind.File or MissionResourceKind.Directory
            && file.Identity.StartsWith(directory.Identity.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison);
        return Contains(a, b) || Contains(b, a);
    }
    public static string JoinFingerprint(IReadOnlyList<Guid> childIds, IReadOnlyList<ContinualObjectiveSnapshot> children)
        => MissionVerification.Digest(System.Text.Json.JsonSerializer.Serialize(children.Where(c => childIds.Count == 0 || childIds.Contains(c.Id)).OrderBy(c => c.Id)
            .Select(c => new { c.Id, State = c.Status is ContinualObjectiveStatus.InProgress or ContinualObjectiveStatus.RetryScheduled ? "active" : c.Status.ToString(),
                c.LastFailure, c.Mission!.ConstraintVersion, AuthorityRevision = c.Authority.MissionResources?.Revision,
                Receipts = c.Receipts.Where(r => r.State == ContinualToolReceiptState.Completed).Select(r => new { r.ToolCallId, r.State, r.IsError, r.WasDispatched, r.Result, r.ArtifactHashes, r.ObservedStates }),
                Verification = c.VerificationHistory.LastOrDefault() })));
    public static bool ShouldWakeJoin(ContinualObjectiveSnapshot parent, IReadOnlyList<ContinualObjectiveSnapshot> children)
        => parent.JoinWait is not { } wait || parent.Mission?.ConstraintVersion != wait.ParentConstraintVersion
            || parent.Authority.MissionResources?.Revision != wait.ParentAuthorityRevision
            || JoinFingerprint(wait.ChildIds, children) != wait.ProgressFingerprint;
    public static bool IsVerified(ContinualObjectiveSnapshot child) => child.Mission?.Delegation?.RetiredByParent != true && child.Status == ContinualObjectiveStatus.Completed
        && MissionVerification.ValidateCompletion(child, child.VerificationHistory.LastOrDefault(), child.LastOutput) is null;
    public static bool ParentActive(ContinualObjectiveSnapshot parent) => parent.Status is ContinualObjectiveStatus.InProgress
        or ContinualObjectiveStatus.RetryScheduled or ContinualObjectiveStatus.VerificationPending;
}
