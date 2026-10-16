namespace SwarmCore.Agent.Business;

/// <summary>
/// Host-authenticated identity of one durable operator work request. A work hash alone is
/// never an idempotency key: operation, trigger, source payload and principals must also match.
/// </summary>
public sealed record OperatorWorkOccupancyIdentity(
    int TenantId,
    int EmployeeId,
    long TriggerId,
    string OperationId,
    string OperatorId,
    string AgentId,
    string PayloadSha256,
    string WorkSha256,
    string SourceId);

/// <summary>Access mode of a host-resolved resource; these values are not an effect severity ladder.</summary>
public enum OperatorWorkResourceAccess { Unknown, Read, Write }

/// <summary>
/// One resource in the COMPLETE host-resolved footprint of a work request. The host must map
/// aliases and every possible write to the same scoped domain/target identity. This record
/// neither authorizes a tool nor proves that an effect occurred.
/// </summary>
public sealed record OperatorWorkResource(string Domain, string Target, OperatorWorkResourceAccess Access);

/// <summary>Current host-proven occupancy of a retained objective.</summary>
public enum OperatorWorkOccupancyState
{
    Unknown,
    QuiescentRetained,
    LiveLease,
    UncertainEffect,
}

public sealed record OperatorWorkOccupancyRequest(
    OperatorWorkOccupancyIdentity? Identity,
    bool FootprintComplete,
    IReadOnlyList<OperatorWorkResource>? Resources);

public sealed record OperatorWorkOccupancyExisting(
    OperatorWorkOccupancyIdentity? Identity,
    Guid? ObjectiveId,
    bool FootprintComplete,
    IReadOnlyList<OperatorWorkResource>? Resources,
    OperatorWorkOccupancyState State);

public enum OperatorWorkOccupancyDecisionKind { StartDistinct, JoinOwnObjective, Defer }

public enum OperatorWorkOccupancyReason
{
    Disjoint,
    ExactOwnObjective,
    InvalidIncoming,
    UnknownExisting,
    IdentityDrift,
    DuplicateOwnObjective,
    SameSource,
    LiveLease,
    UncertainEffect,
    IncompleteFootprint,
    OverlappingResource,
}

public sealed record OperatorWorkOccupancyDecision(
    OperatorWorkOccupancyDecisionKind Kind,
    OperatorWorkOccupancyReason Reason,
    Guid? ObjectiveId = null);

/// <summary>
/// Pure collision policy. Hosts authenticate every identity, derive and atomically reserve
/// complete resource footprints, and recheck current authority before dispatch. In particular,
/// StartDistinct is not itself a reservation, a capability grant or no-dispatch proof. This
/// policy never stops, supersedes or resets a retained objective or its verification history.
/// </summary>
public static class OperatorWorkOccupancyPolicy
{
    public static OperatorWorkOccupancyDecision Decide(
        OperatorWorkOccupancyRequest incoming,
        IReadOnlyList<OperatorWorkOccupancyExisting> existing)
    {
        if (incoming is null || !ValidIdentity(incoming.Identity))
            return Defer(OperatorWorkOccupancyReason.InvalidIncoming);
        if (!ValidFootprint(incoming.FootprintComplete, incoming.Resources))
            return Defer(OperatorWorkOccupancyReason.IncompleteFootprint);
        if (existing is null || existing.Count > 1024)
            return Defer(OperatorWorkOccupancyReason.UnknownExisting);

        var request = incoming.Identity!;
        Guid? ownObjective = null;
        foreach (var prior in existing)
        {
            var old = prior?.Identity;
            if (prior is null || !ValidIdentity(old) || prior.ObjectiveId is null || prior.ObjectiveId == Guid.Empty
                || old!.TenantId != request.TenantId)
                return Defer(OperatorWorkOccupancyReason.UnknownExisting);
            if (!ValidFootprint(prior.FootprintComplete, prior.Resources))
                return Defer(OperatorWorkOccupancyReason.IncompleteFootprint);
            if (!Enum.IsDefined(prior.State))
                return Defer(OperatorWorkOccupancyReason.UnknownExisting);

            var retainedIdentity = old!;
            if (retainedIdentity == request)
            {
                if (!SameFootprint(incoming.Resources!, prior.Resources!))
                    return Defer(OperatorWorkOccupancyReason.IdentityDrift);
                if (prior.State == OperatorWorkOccupancyState.UncertainEffect)
                    return Defer(OperatorWorkOccupancyReason.UncertainEffect);
                if (prior.State == OperatorWorkOccupancyState.Unknown)
                    return Defer(OperatorWorkOccupancyReason.UnknownExisting);
                if (ownObjective is not null)
                    return Defer(OperatorWorkOccupancyReason.DuplicateOwnObjective);
                ownObjective = prior.ObjectiveId;
                continue;
            }
            if (retainedIdentity.OperationId == request.OperationId || retainedIdentity.TriggerId == request.TriggerId)
                return Defer(OperatorWorkOccupancyReason.IdentityDrift);
            if (retainedIdentity.SourceId == request.SourceId)
                return Defer(OperatorWorkOccupancyReason.SameSource);
            if (prior.State == OperatorWorkOccupancyState.LiveLease)
                return Defer(OperatorWorkOccupancyReason.LiveLease);
            if (prior.State == OperatorWorkOccupancyState.UncertainEffect)
                return Defer(OperatorWorkOccupancyReason.UncertainEffect);
            if (prior.State != OperatorWorkOccupancyState.QuiescentRetained)
                return Defer(OperatorWorkOccupancyReason.UnknownExisting);
            if (Overlaps(incoming.Resources!, prior.Resources!))
                return Defer(OperatorWorkOccupancyReason.OverlappingResource);
        }

        return ownObjective is { } objectiveId
            ? new(OperatorWorkOccupancyDecisionKind.JoinOwnObjective,
                OperatorWorkOccupancyReason.ExactOwnObjective, objectiveId)
            : new(OperatorWorkOccupancyDecisionKind.StartDistinct,
                OperatorWorkOccupancyReason.Disjoint);
    }

    private static OperatorWorkOccupancyDecision Defer(OperatorWorkOccupancyReason reason)
        => new(OperatorWorkOccupancyDecisionKind.Defer, reason);

    private static bool ValidIdentity(OperatorWorkOccupancyIdentity? identity)
        => identity is { TenantId: > 0, EmployeeId: > 0, TriggerId: > 0 }
            && ValidText(identity.OperationId, 150) && ValidText(identity.OperatorId, 450)
            && ValidText(identity.AgentId, 450) && ValidText(identity.SourceId, 200)
            && ValidHash(identity.PayloadSha256) && ValidHash(identity.WorkSha256);

    private static bool ValidFootprint(bool complete, IReadOnlyList<OperatorWorkResource>? resources)
    {
        if (!complete || resources is null || resources.Count is < 1 or > 128) return false;
        var seen = new HashSet<(string Domain, string Target)>();
        foreach (var resource in resources)
        {
            if (resource is null || !ValidText(resource.Domain, 100) || !ValidText(resource.Target, 256)
                || resource.Access is not (OperatorWorkResourceAccess.Read or OperatorWorkResourceAccess.Write)
                || !seen.Add((resource.Domain, resource.Target))) return false;
        }
        return true;
    }

    private static bool Overlaps(IReadOnlyList<OperatorWorkResource> incoming,
        IReadOnlyList<OperatorWorkResource> existing)
    {
        foreach (var left in incoming)
            foreach (var right in existing)
                if (left.Domain == right.Domain && left.Target == right.Target
                    && (left.Access == OperatorWorkResourceAccess.Write || right.Access == OperatorWorkResourceAccess.Write))
                    return true;
        return false;
    }

    private static bool SameFootprint(IReadOnlyList<OperatorWorkResource> incoming,
        IReadOnlyList<OperatorWorkResource> existing)
        => incoming.Count == existing.Count && incoming.All(resource => existing.Contains(resource));

    private static bool ValidText(string? value, int max)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= max
            && value.All(c => !char.IsControl(c));

    private static bool ValidHash(string? value)
        => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
}
