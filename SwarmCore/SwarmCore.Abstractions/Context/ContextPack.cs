namespace SwarmCore.Abstractions.Context;

/// <summary>
/// A source-backed bundle of context prepared for one consumer and purpose.
/// This is the boundary between Talavine's memory layer and agents, UI
/// surfaces, MCP tools, peer pairing, and automation suggestions.
/// </summary>
public sealed record ContextPack
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset BuiltAt { get; init; } = DateTimeOffset.UtcNow;
    public required string Purpose { get; init; }
    public required string Audience { get; init; }
    public IReadOnlyList<ContextItem> Items { get; init; } = [];
    public IReadOnlyList<ContextSource> Sources { get; init; } = [];
    public ContextPolicy Policy { get; init; } = ContextPolicy.Default;
    public int EstimatedTokens { get; init; }
    public ContextPackAuditResult? Audit { get; init; }
    public ContextQueryPlan? Plan { get; init; }
    public ContextCoverage Coverage { get; init; } = new();
    /// <summary>Measured complete formatted prompt characters, including evidence metadata and fences.</summary>
    public int WireCharacters { get; init; }

    /// <summary>
    /// True when a pack containing any evidence payload lacks a successful safety-audit
    /// certification. Every model-facing or external consumer must stop at
    /// this boundary rather than treating a failed or missing audit receipt as
    /// advisory metadata. Only packs with neither items nor sources are empty;
    /// orphan source metadata can itself disclose private evidence.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasBlockingAuditFailure
        => (Items.Count > 0 || Sources.Count > 0) && Audit?.Passed != true;
}

public sealed record ContextItem
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Text { get; init; }
    public double Relevance { get; init; }
    public double Confidence { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool IsUserConfirmed { get; init; }
    public bool IsActionable { get; init; }
    public IReadOnlyList<string> SourceIds { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? TemporalStatus { get; init; }
    public DateTimeOffset? EffectiveAt { get; init; }
}

public sealed record ContextSource
{
    public IReadOnlyList<ContextFactSupport> FactSupports { get; init; } = [];
    public string? TemporalIntent { get; init; }
    public DateTimeOffset? EffectiveAt { get; init; }
    public DateTimeOffset? KnownAt { get; init; }
    public string? SourceVersion { get; init; }
    public bool? SensitiveAccess { get; init; }
    /// <summary>Reserved once by the consumer; filtering must never retarget an existing alias.</summary>
    public string? Alias { get; init; }
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public Guid? AccountId { get; init; }
    public string? RecordId { get; init; }
    public string? Uri { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public string? Excerpt { get; init; }
}

public sealed record ContextFactSupport(Guid FactId, string Fingerprint, string? EvidenceSpan = null);

public sealed record ContextPolicy
{
    public static ContextPolicy Default { get; } = new();

    public bool IncludeSensitive { get; init; }
    public bool RequireApprovalForExternalUse { get; init; } = true;
    public ContextRedactionLevel RedactionLevel { get; init; } = ContextRedactionLevel.Internal;
    /// <summary>
    /// Optional source-account allowlist. When present, context derived from
    /// any other account (or with no account provenance) is excluded. A
    /// drafting surface should supply only its sending account unless the
    /// user explicitly approves additional accounts for that operation.
    /// </summary>
    public IReadOnlySet<Guid>? AllowedSourceAccountIds { get; init; }
    // Context-item text admission budget, estimated at four characters/token.
    // Source metadata, ids, aliases, JSON/XML scaffolding, and transport
    // envelopes are intentionally outside this number; it is not a wire-token
    // or provider-billing estimate. Matches the agent build-pack tool's 2500 so
    // packs built via the engine directly (e.g. the per-turn injector) aren't
    // trimmed to a much thinner grounding than the tool path produces.
    public int MaxTokens { get; init; } = 2500;
    public int MaxSources { get; init; } = 32;
    /// <summary>Optional cooperative retrieval deadline per lane. Timed-out lanes report partial coverage; audit and source revalidation still run.</summary>
    public TimeSpan? RetrievalLaneTimeout { get; init; }
    /// <summary>Optional ordinal item-kind prefix admitted before token budgeting. Other candidates cannot crowd out the requested evidence family.</summary>
    public string? RequiredItemKindPrefix { get; init; }
    /// <summary>Complete model-facing payload estimate at four characters/token, separate from item admission.</summary>
    public int MaxWireTokens { get; init; } = 8192;
}

public enum ContextRedactionLevel
{
    Internal = 0,
    UserVisible = 1,
    PeerShareable = 2,
    ExternalTool = 3
}

public sealed record ContextPackAuditResult
{
    public double Score { get; init; } = 1.0;
    public bool Passed { get; init; } = true;
    public bool ShouldAttemptRepair { get; init; }
    public IReadOnlyList<ContextPackAuditIssue> Issues { get; init; } = [];
    public IReadOnlyDictionary<string, string> Signals { get; init; } = new Dictionary<string, string>();
}

public sealed record ContextPackAuditIssue
{
    public required string Code { get; init; }
    public ContextPackAuditSeverity Severity { get; init; } = ContextPackAuditSeverity.Warning;
    public required string Message { get; init; }
    public string? ItemId { get; init; }
    public string? SourceId { get; init; }
    public string? SuggestedAction { get; init; }
}

public enum ContextPackAuditSeverity
{
    Info = 0,
    Warning = 1,
    Major = 2,
    Critical = 3
}

public sealed record ContextPackRepairPlan
{
    public IReadOnlyList<string> RemoveItemIds { get; init; } = [];
    public IReadOnlyList<string> PrioritizeItemIds { get; init; } = [];
    public IReadOnlyList<string> DemoteItemIds { get; init; } = [];
    public string? Rationale { get; init; }
    public double Confidence { get; init; }
}
