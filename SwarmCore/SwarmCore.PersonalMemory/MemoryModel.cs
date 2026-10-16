using System.Text.Json.Serialization;
using SwarmCore.Abstractions.Knowledge;

namespace SwarmCore.PersonalMemory;

/// <summary>
/// Who a memory is about and which tenant/partition owns it. Every store read and
/// write is keyed by the full scope; two scopes never see each other's rows.
/// Comparison is ordinal: hosts pass stable identifiers (user id, profile id,
/// tenant id), never display names or e-mail addresses that can change case.
/// </summary>
public readonly record struct MemoryScope
{
    public const string DefaultPartition = "default";
    private const int MaxPartLength = 200;

    /// <param name="tenantId">Owning tenant/organization (desktop hosts use a fixed local tenant id).</param>
    /// <param name="subjectId">The person the memory is about (user id / profile id).</param>
    /// <param name="partition">Optional app/profile partition (e.g. "talavine", "crm", "swarmcode").</param>
    public MemoryScope(string tenantId, string subjectId, string? partition = null)
    {
        TenantId = Validate(tenantId, nameof(tenantId));
        SubjectId = Validate(subjectId, nameof(subjectId));
        Partition = string.IsNullOrWhiteSpace(partition) ? DefaultPartition : Validate(partition, nameof(partition));
    }

    public string TenantId { get; }
    public string SubjectId { get; }
    public string Partition { get; }

    /// <summary>True for <c>default(MemoryScope)</c>, which no store may accept.</summary>
    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrEmpty(TenantId) || string.IsNullOrEmpty(SubjectId);

    /// <summary>Stable composite key suitable for an index column or dictionary key.</summary>
    [JsonIgnore]
    public string Key => $"{TenantId}\u001f{SubjectId}\u001f{Partition}";

    public override string ToString() => $"{TenantId}/{SubjectId}/{Partition}";

    private static string Validate(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Memory scope parts must be non-empty.", name);
        var trimmed = value.Trim();
        if (trimmed.Length > MaxPartLength || trimmed.Any(char.IsControl))
            throw new ArgumentException("Memory scope parts must be short and contain no control characters.", name);
        return trimmed;
    }
}

/// <summary>
/// Who vouches for a statement. Numeric order equals
/// <see cref="KnowledgeTrustPolicy.AuthorityRank"/> so the shared trust rule and this
/// enum can never disagree.
/// </summary>
public enum MemorySourceAuthority
{
    /// <summary>Anyone else: inbound mail from an unknown sender, web pages, imported documents.</summary>
    ThirdParty = 0,

    /// <summary>Written by an agent/model. Never evidence about the person on its own.</summary>
    Agent = 1,

    /// <summary>A contact the person has marked trusted (host-determined).</summary>
    TrustedContact = 2,

    /// <summary>The person's own words (chat turn, their own sent mail, an explicit edit).</summary>
    UserAuthored = 3,
}

/// <summary>How a claim entered the system.</summary>
public enum MemoryCaptureMethod
{
    /// <summary>The person asked the assistant to remember something (tool or UI).</summary>
    ExplicitRemember = 0,

    /// <summary>Background extraction from the person's own words in a chat turn.</summary>
    PostTurnExtraction = 1,

    /// <summary>Email, document, CRM or other import.</summary>
    Import = 2,

    /// <summary>An agent-written summary of a conversation (recent-conversations section only).</summary>
    ConversationSummary = 3,

    /// <summary>Something an agent inferred. Becomes a suggestion awaiting the person's confirmation.</summary>
    AgentObservation = 4,

    /// <summary>The person edited or confirmed an item on a "what it knows about you" page.</summary>
    UserEdit = 5,
}

/// <summary>Coarse kind of a memory; drives the digest section and staleness defaults.</summary>
public enum MemoryCategory
{
    Identity = 0,
    ResponsePreference = 1,
    Preference = 2,
    Relationship = 3,
    Interest = 4,
    Routine = 5,
    Project = 6,
    Plan = 7,
    ConversationSummary = 8,
    Other = 9,
}

/// <summary>Sections of the bounded profile digest, in render order.</summary>
public enum MemoryDigestSection
{
    /// <summary>Who the person is: identity, relationships, interests, routines, general preferences.</summary>
    Identity = 0,

    /// <summary>How they like answers.</summary>
    ResponsePreferences = 1,

    /// <summary>Current projects and plans.</summary>
    ProjectsAndPlans = 2,

    /// <summary>Recent conversation summaries.</summary>
    RecentConversations = 3,
}

/// <summary>Lifecycle of a claim.</summary>
public enum MemoryClaimStatus
{
    /// <summary>Captured; not yet consolidated.</summary>
    Pending = 0,

    /// <summary>Became (or changed) a fact; <see cref="MemoryClaim.ResultFactId"/> is set.</summary>
    Applied = 1,

    /// <summary>Already known; evidence may have been attached to the existing fact.</summary>
    NoChange = 2,

    /// <summary>Rejected by policy (suppressed, conflicts with higher authority, screened, expired).</summary>
    Rejected = 3,

    /// <summary>Third-party single-source claim waiting for an independent second source or user confirmation.</summary>
    AwaitingCorroboration = 4,

    /// <summary>Agent observation shown to the person as a suggestion; never auto-promoted.</summary>
    AwaitingConfirmation = 5,
}

/// <summary>
/// One piece of provenance. <see cref="SourceId"/> must be resolvable by the host
/// (message id, turn id, import record id). Hosts that address messages by
/// (account, message) must set <see cref="SourceAccountId"/> as well.
/// </summary>
public sealed record MemoryEvidence
{
    public required string SourceKind { get; init; }
    public required string SourceId { get; init; }
    public required MemorySourceAuthority Authority { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
    public string? SourceAccountId { get; init; }
    public string? SourceUri { get; init; }
    public string? SourceTitle { get; init; }

    /// <summary>Short verbatim excerpt (bounded by the host). Data only; never rendered as instructions.</summary>
    public string? Excerpt { get; init; }

    /// <summary>
    /// Who actually originated the content (sender address, import feed, user id). Two
    /// evidence rows with the same origin are not independent corroboration.
    /// Defaults to <c>SourceKind:SourceId</c>.
    /// </summary>
    public string? Origin { get; init; }

    [JsonIgnore]
    public string IndependenceKey => string.IsNullOrWhiteSpace(Origin)
        ? $"{SourceKind}:{SourceAccountId}:{SourceId}".ToLowerInvariant()
        : Origin.Trim().ToLowerInvariant();

    [JsonIgnore]
    public string IdentityKey => $"{SourceKind}|{SourceAccountId}|{SourceId}".ToLowerInvariant();
}

/// <summary>
/// A captured statement with provenance. Claims are evidence, not truth: only
/// consolidation turns them into <see cref="MemoryFact"/>s, and only through the
/// deterministic verifier.
/// </summary>
public sealed record MemoryClaim
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required MemoryScope Scope { get; init; }

    /// <summary>Host/caller key that makes capture idempotent per scope (e.g. "turn:{id}:0").</summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>Optional stable topic key ("home-city", "employer"); same key + different text means the fact changed.</summary>
    public string? Key { get; init; }
    public required MemoryCategory Category { get; init; }
    public required string Text { get; init; }
    public required MemorySourceAuthority Authority { get; init; }
    public required MemoryCaptureMethod Method { get; init; }
    public required MemoryEvidence Evidence { get; init; }
    public double Confidence { get; init; } = 0.7;
    public DateTimeOffset ObservedAt { get; init; }
    public DateTimeOffset? ValidFrom { get; init; }
    public DateTimeOffset? ValidUntil { get; init; }
    public DateTimeOffset? StaleAfter { get; init; }

    /// <summary>When the planned/described event happens (for "is going to Singapore in July").</summary>
    public DateTimeOffset? EventAt { get; init; }
    public bool Sensitive { get; init; }
    public bool UserConfirmed { get; init; }
    public MemoryClaimStatus Status { get; init; } = MemoryClaimStatus.Pending;
    public Guid? ResultFactId { get; init; }
    public string? StatusReason { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Store-assigned optimistic concurrency version (0 = not yet stored).</summary>
    public long Version { get; init; }
}

/// <summary>
/// A consolidated statement about the subject. Facts are never silently replaced:
/// a change produces a new fact and stamps the old one with
/// <see cref="SupersededById"/> and <see cref="ValidUntil"/> (same semantics as
/// <c>ContextFact.SupersededById</c>/<c>ValidFrom</c>/<c>ExpiresAt</c>).
/// </summary>
public sealed record MemoryFact
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required MemoryScope Scope { get; init; }
    public required string Key { get; init; }
    public required MemoryCategory Category { get; init; }
    public required string Text { get; init; }
    public required MemorySourceAuthority Authority { get; init; }
    public IReadOnlyList<MemoryEvidence> Evidence { get; init; } = [];
    public double Confidence { get; init; } = 0.7;
    public DateTimeOffset? ValidFrom { get; init; }
    public DateTimeOffset? ValidUntil { get; init; }
    public DateTimeOffset? StaleAfter { get; init; }
    public DateTimeOffset? EventAt { get; init; }
    public Guid? SupersededById { get; init; }
    public Guid? SupersedesId { get; init; }

    /// <summary>"Don't bring this up": kept and visible on the control page, never recalled.</summary>
    public bool Suppressed { get; init; }
    public bool UserConfirmed { get; init; }
    public bool Sensitive { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Store-assigned optimistic concurrency version (0 = not yet stored).</summary>
    public long Version { get; init; }

    /// <summary>Distinct independent origins across evidence.</summary>
    [JsonIgnore]
    public int IndependentSourceCount => Math.Max(1, Evidence.Select(e => e.IndependenceKey).Distinct(StringComparer.Ordinal).Count());

    /// <summary>Short stable reference shown in digests and accepted by tools ("m:1a2b3c4d").</summary>
    [JsonIgnore]
    public string Reference => MemoryReferences.For(Id);
}

/// <summary>
/// A rule that prevents a forgotten statement from being relearned. Matching uses the
/// shared <c>KnowledgeSuppressionPolicy</c>: same key + containing text, or identical
/// text under any key.
/// </summary>
public sealed record MemorySuppression
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required MemoryScope Scope { get; init; }
    public string? Key { get; init; }
    public string? TextPattern { get; init; }
    public MemoryCategory? Category { get; init; }
    public required string Reason { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// When set, the rule only blocks statements whose authority is at most this value. A dismissed
    /// agent/third-party suggestion records the suggestion's authority here, so the person's own later
    /// statement with the same words is still admitted. Null (a "forget" by the person) blocks every
    /// authority except the person's explicit remember/edit made after the rule.
    /// </summary>
    public MemorySourceAuthority? MaxAuthority { get; init; }

    /// <summary>True for a rule the person created by forgetting (applies to every authority).</summary>
    [JsonIgnore]
    public bool IsForgetRule => MaxAuthority is null;
}

/// <summary>
/// Deterministic, name-based ids. Two devices (or two replicas of a host store) that process the same
/// claim produce the same claim and fact ids, so replication merges the rows instead of duplicating
/// them (a fresh <see cref="Guid.NewGuid"/> per device would leave two current copies of one fact).
/// </summary>
public static class MemoryIds
{
    public static Guid Derive(string kind, params string[] parts)
    {
        var material = kind + "|" + string.Join("|", parts.Select(p => p.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + p));
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material));
        var bytes = hash[..16];
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50); // version 5-style marker (name-based)
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(bytes);
    }

    /// <summary>The claim id for (scope, idempotency key): identical on every replica.</summary>
    public static Guid ForClaim(MemoryScope scope, string idempotencyKey) => Derive("claim", scope.Key, idempotencyKey);

    /// <summary>The id of a fact produced from <paramref name="claimIds"/> by <paramref name="operation"/>.</summary>
    public static Guid ForFact(string operation, Guid? previousFactId, IEnumerable<Guid> claimIds, string? text = null)
        => Derive("fact:" + operation,
            previousFactId?.ToString("N") ?? "-",
            string.Join(",", claimIds.Select(id => id.ToString("N")).OrderBy(x => x, StringComparer.Ordinal)),
            text is null ? "-" : MemoryTextScreen.NormalizeForComparison(text));
}

/// <summary>Short references for facts ("m:" + first 8 hex digits of the id).</summary>
public static class MemoryReferences
{
    public const string Prefix = "m:";

    public static string For(Guid id) => Prefix + id.ToString("N")[..8];

    /// <summary>Resolves a full GUID, "m:xxxxxxxx" or bare 8-hex prefix against candidate facts. Ambiguous → null.</summary>
    public static MemoryFact? Resolve(string? reference, IEnumerable<MemoryFact> facts)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var value = reference.Trim();
        if (Guid.TryParse(value, out var full))
            return facts.FirstOrDefault(f => f.Id == full);
        if (value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) value = value[Prefix.Length..];
        if (value.Length < 8 || !value.All(char.IsAsciiHexDigit)) return null;
        var matches = facts.Where(f => f.Id.ToString("N").StartsWith(value, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>
    /// Facts a plain description ("Maya's college move") refers to: every distinctive term of the description
    /// appears in the fact. Used to resolve a reversible action, or to offer candidates back to the model.
    /// </summary>
    public static IReadOnlyList<MemoryFact> MatchDescription(string? description, IEnumerable<MemoryFact> facts)
    {
        var wanted = MemoryRelevanceRanker.DistinctiveTerms(description);
        if (wanted.Count == 0) return [];
        return facts.Where(f => wanted.All(MemoryRelevanceRanker.DistinctiveTerms(f.Text).Contains)).ToList();
    }
}
