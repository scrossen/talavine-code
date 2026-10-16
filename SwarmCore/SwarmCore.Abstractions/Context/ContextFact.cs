using System.ComponentModel.DataAnnotations.Schema;

namespace SwarmCore.Abstractions.Context;

/// <summary>
/// Durable, source-backed fact extracted from the user's local data or
/// confirmed through user feedback. This is the canonical personal context unit.
/// </summary>
public sealed class ContextFact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    public string NormalizedKey { get; set; } = "";
    public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// When the described state became true (event time when the source gives
    /// one, otherwise the observation time). Null means "as observed".
    /// </summary>
    public DateTimeOffset? ValidFrom { get; set; }

    /// <summary>
    /// Facts sharing a StateKey describe one evolving state (the status of a
    /// project, a shipment, an open commitment). Shared evidence semantics
    /// resolve current alternatives by effective time and authority; observation
    /// recency alone is not proof of replacement.
    /// </summary>
    public string? StateKey { get; set; }

    /// <summary>
    /// Set when a newer fact with the same <see cref="StateKey"/> replaced this
    /// one. Superseded facts are excluded from current-state retrieval and
    /// remain available to timeline and provenance reads.
    /// </summary>
    public Guid? SupersededById { get; set; }
    public double Confidence { get; set; } = 0.5;
    public bool IsUserConfirmed { get; set; }
    public bool IsSensitive { get; set; }
    public string TagsJson { get; set; } = "[]";
    public string MetadataJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? WriterDeviceId { get; set; }
    public long? WriterMonotonicTicks { get; set; }

    public string SourceId { get; set; } = "";
    public string SourceKind { get; set; } = "";
    public string SourceTitle { get; set; } = "";
    public Guid? SourceAccountId { get; set; }
    public string? SourceRecordId { get; set; }
    public string? SourceUri { get; set; }
    public string? SourceExcerpt { get; set; }

    [NotMapped]
    public double RetrievalScore { get; set; }

    /// <summary>Computed for the requested effective/knowledge time; never synced as source truth.</summary>
    [NotMapped]
    public string StateStatus { get; set; } = "unknown";
}
