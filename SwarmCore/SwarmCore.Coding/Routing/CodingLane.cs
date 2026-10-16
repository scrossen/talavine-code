namespace SwarmCore.Coding.Routing;

/// <summary>
/// One named model route the coding harness can run a turn on. Lanes are
/// host configuration: a subscription CLI (for example provider "ClaudeCode",
/// model "claude-code:default"), a BYOK cloud provider (provider "OpenRouter",
/// model "anthropic/claude-sonnet-4.5"), a local runner, or a native coding
/// agent CLI (provider "ClaudeCodeNative") that runs its own tools in the repository.
/// </summary>
public sealed record CodingLane(string Name, string Provider, string Model)
{
    /// <summary>Optional human description shown by hosts in lane listings.</summary>
    public string? Description { get; init; }

    /// <summary>Routes sharing a provider connection can share one picker entry; their models remain separate execution routes.</summary>
    public string? SelectionGroup { get; init; }

    public string SelectionLabel => SelectionGroup ?? Name;

    /// <summary>Lower numbers are tried first when a turn needs a hand-off.</summary>
    public int Priority { get; init; }

    /// <summary>
    /// Tried first when another lane declines a request (a refusal or partial refusal), ahead of the ordinary lane
    /// order. For routes whose models answer without the content restrictions others apply, so a decline elsewhere
    /// does not end the person's request. It changes hand-off order only; it grants no authority and is never
    /// selected first for a new request.
    /// </summary>
    public bool PreferOnDecline { get; init; }

    public string Route => $"{Provider}/{Model}";

    /// <summary>
    /// Key for <see cref="LaneHealthTracker"/> cooldowns. Null uses <see cref="Name"/>. Hosts whose lane
    /// names are positions (Talavine's <c>scout-1</c>) set it to the resolved route so a cooldown follows
    /// the route that failed, not whatever route holds that position next time.
    /// </summary>
    public string? HealthKey { get; init; }

    public string HealthKeyOrName => string.IsNullOrWhiteSpace(HealthKey) ? Name : HealthKey;

    /// <summary>
    /// True for lanes that run an external coding agent with its own tools and loop
    /// (Claude Code, Codex) instead of the SwarmCore engine. Provider ids ending in
    /// "Native" are native lanes.
    /// </summary>
    public bool IsNative => Provider.EndsWith("Native", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Provider choices for hosts with a separate model picker. The selected route is retained verbatim.</summary>
public static class CodingLaneSelections
{
    public static IReadOnlyList<CodingLane> ForPicker(IReadOnlyList<CodingLane> lanes, string? selectedName = null)
    {
        var selected = lanes.FirstOrDefault(l => l.Name == selectedName);
        var groups = new HashSet<string>(StringComparer.Ordinal);
        return lanes.Where(l => l.SelectionGroup is null || groups.Add(l.SelectionGroup))
            .Select(l => l.SelectionGroup is not null && selected?.SelectionGroup == l.SelectionGroup ? selected : l)
            .ToArray();
    }
}

/// <summary>Host configuration for multi-model coding turns.</summary>
public sealed class CodingLaneOptions
{
    public const string SectionName = "CodingLanes";

    public List<CodingLane> Lanes { get; set; } = [];

    /// <summary>Lane used when a request names none. Null means the first configured lane.</summary>
    public string? DefaultLane { get; set; }

    /// <summary>
    /// When a lane refuses or cannot complete a request, continue the same
    /// conversation on the next lane. Work the first lane already completed is
    /// never repeated: the hand-off is a continuation turn, not a re-run.
    /// </summary>
    public bool HandOffOnRefusal { get; set; } = true;

    /// <summary>Upper bound on lanes tried for one user request, including the first.</summary>
    public int MaxLanesPerRequest { get; set; } = 3;

    /// <summary>
    /// How long a lane whose route failed (CLI missing or signed out, quota, auth, network)
    /// is skipped before it is tried again. A lane the user names explicitly is always tried.
    /// </summary>
    public TimeSpan UnavailableLaneCooldown { get; set; } = TimeSpan.FromMinutes(10);
}
