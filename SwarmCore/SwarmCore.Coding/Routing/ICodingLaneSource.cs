using Microsoft.Extensions.Options;

namespace SwarmCore.Coding.Routing;

/// <summary>
/// The lanes a coding request may run on, plus the hand-off policy, as one snapshot.
/// <see cref="Lanes"/> is ordered by <see cref="CodingLane.Priority"/>.
/// </summary>
public sealed record CodingLaneSet
{
    public static CodingLaneSet Empty { get; } = new();

    public IReadOnlyList<CodingLane> Lanes { get; init; } = [];
    /// <summary>Only a source that knows the offered-route snapshot is complete may assert this. Execution does not grant from it.</summary>
    public bool AdmissionEvidenceComplete { get; init; }

    /// <summary>Lane used when a request names none. Null means the first lane.</summary>
    public string? DefaultLane { get; init; }

    public bool HandOffOnRefusal { get; init; } = true;

    public int MaxLanesPerRequest { get; init; } = 3;

    public TimeSpan UnavailableLaneCooldown { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Scope for <see cref="LaneHealthTracker"/> cooldowns of these lanes. A multi-profile host sets
    /// it (for example to the profile id) so one profile's failed route does not cool another
    /// profile's same-named lane. Null is the process-wide scope.
    /// </summary>
    public string? HealthScope { get; init; }

    public CodingLane? Find(string? name) => string.IsNullOrWhiteSpace(name) ? null
        : Lanes.FirstOrDefault(l => string.Equals(l.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The named lane, else the configured default, else the first lane.</summary>
    public CodingLane? Resolve(string? name)
    {
        if (Lanes.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(name)) return Find(name);
        return Find(DefaultLane) ?? Lanes[0];
    }

    public static CodingLaneSet From(CodingLaneOptions options) => new()
    {
        Lanes = options.Lanes.OrderBy(l => l.Priority).ToList(),
        AdmissionEvidenceComplete = true,
        DefaultLane = options.DefaultLane,
        HandOffOnRefusal = options.HandOffOnRefusal,
        MaxLanesPerRequest = options.MaxLanesPerRequest,
        UnavailableLaneCooldown = options.UnavailableLaneCooldown,
    };
}

/// <summary>
/// Where the coding harness gets its lanes. The orchestrator, lane-health cooldowns, and
/// hand-off depend only on this source and <c>IAgentTurnRunner</c>, never on a particular
/// configuration section. The default, <see cref="OptionsCodingLaneSource"/>, reads
/// <see cref="CodingLaneOptions"/> (the <c>CodingLanes</c> section SwarmCode seeds). A host may
/// implement it over its own model router instead: Talavine plans to build lanes from
/// Model Scout's recommendations so coding turns follow the same governed routing as the
/// rest of the assistant. Register a host source before calling
/// <see cref="ServiceCollectionExtensions.AddSwarmCoreCodingHarness"/> (the harness default is a
/// TryAdd) or replace the descriptor afterwards. A scoped source is supported: the
/// orchestrator resolves it inside a scope.
/// </summary>
public interface ICodingLaneSource
{
    /// <summary>The current lanes and policy. Must be cheap: hosts call it to list lanes in UI.</summary>
    CodingLaneSet GetLanes();

    /// <summary>
    /// Lanes for one conversation's request. Hosts whose routing is asynchronous or
    /// conversation-specific override this; the default returns <see cref="GetLanes"/>.
    /// </summary>
    ValueTask<CodingLaneSet> GetLanesAsync(string conversationId, CancellationToken ct = default)
        => ValueTask.FromResult(GetLanes());

    /// <summary>Passive resolution using the same ranking. Unsupported sources return incomplete
    /// coverage; never fall back to an ordinary resolution that may persist audits or start maintenance.</summary>
    ValueTask<CodingLaneSet> GetAdmissionLanesAsync(string conversationId, CancellationToken ct = default)
        => ValueTask.FromResult(CodingLaneSet.Empty);
}

/// <summary>Lanes from <see cref="CodingLaneOptions"/> (configuration section <c>CodingLanes</c>).</summary>
public sealed class OptionsCodingLaneSource(IOptions<CodingLaneOptions> options) : ICodingLaneSource
{
    public CodingLaneSet GetLanes() => CodingLaneSet.From(options.Value);
    public ValueTask<CodingLaneSet> GetAdmissionLanesAsync(string conversationId, CancellationToken ct = default)
        => ValueTask.FromResult(GetLanes());
}
