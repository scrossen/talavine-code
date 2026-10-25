namespace SwarmCore.Coding.Routing;

/// <summary>Shared per-turn selection and retry rules. Hosts supply user intent.</summary>
public static class CodingRoutingPolicy
{
    public static bool CanHandOff(CodingTurnRequest request, bool configured)
        => !request.SelectionPinned && !request.RequireSelectedRoute && (request.AllowHandOff ?? configured);

    // Lane names and descriptions are presentation metadata, not new execution routes.
    // Model IDs remain case-sensitive because compatible servers may distinguish them.
    public static bool WasAttempted(CodingLane candidate, IEnumerable<CodingLane> tried)
        => tried.Any(previous => previous.Name.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase)
            || previous.Provider.Equals(candidate.Provider, StringComparison.OrdinalIgnoreCase)
                && previous.Model.Equals(candidate.Model, StringComparison.Ordinal));

    public static bool StopsOnRefusal(CodingTurnRequest request, LaneOutcomeKind outcome)
        => request.StopOnRefusal && outcome is LaneOutcomeKind.Refused or LaneOutcomeKind.PartialRefusal;

    /// <summary>
    /// The lanes a hand-off may continue on, in order: every lane not yet tried, in lane order; after a decline
    /// (<see cref="LaneOutcomeKind.Refused"/> or <see cref="LaneOutcomeKind.PartialRefusal"/>) the lanes marked
    /// <see cref="CodingLane.PreferOnDecline"/> come first, so a model's refusal is retried where it is least likely to recur.
    /// </summary>
    public static IEnumerable<CodingLane> HandOffCandidates(IEnumerable<CodingLane> lanes, IReadOnlyList<CodingLane> tried, LaneOutcomeKind outcome)
    {
        var untried = lanes.Where(l => !WasAttempted(l, tried));
        return outcome is LaneOutcomeKind.Refused or LaneOutcomeKind.PartialRefusal
            ? untried.OrderByDescending(l => l.PreferOnDecline) // stable: lane order kept within each group
            : untried;
    }
}
