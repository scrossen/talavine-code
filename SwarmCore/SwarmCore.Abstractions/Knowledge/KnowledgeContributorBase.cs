using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SwarmCore.Abstractions.Context;

namespace SwarmCore.Abstractions.Knowledge;

/// <summary>
/// Per-source-kind fingerprint cache so scoped contributors can skip rebuilding
/// when their source data hasn't changed between background passes. Facts and
/// the graph projection are tracked separately so a projection that did not
/// complete is retried without re-churning facts that already landed.
/// </summary>
public sealed class KnowledgeContributorState
{
    /// <summary>Fingerprint whose facts were emitted, per source kind.</summary>
    public ConcurrentDictionary<string, string> Fingerprints { get; } = new();

    /// <summary>Fingerprint whose graph projection completed, per source kind.</summary>
    public ConcurrentDictionary<string, string> GraphFingerprints { get; } = new();
}

/// <summary>
/// Shared machinery for single-source knowledge contributors: a retire-then-emit
/// reconcile gated by a cheap fingerprint, plus helpers for building
/// user-authored context-fact drafts. Each contributor owns every fact of its
/// <see cref="IKnowledgeContributor.SourceKind"/>, so reconcile retires the
/// whole kind and re-emits the current desired set.
///
/// <para>
/// Multi-source contributors that manage many sub-sources at once (e.g. the vibe
/// digest, one logical source per vibe) implement <see cref="IKnowledgeContributor"/>
/// directly instead of using this base, because retire-by-kind would wipe peers.
/// </para>
/// </summary>
public abstract class KnowledgeContributorBase : IKnowledgeContributor
{
    protected const double DefaultConfidence = 0.88;

    protected IContextFactIngestionService Ingestion { get; }
    protected IContextFactRepository Facts { get; }
    private readonly KnowledgeContributorState _state;
    private readonly ILogger _logger;

    protected KnowledgeContributorBase(
        IContextFactIngestionService ingestion,
        IContextFactRepository facts,
        KnowledgeContributorState state,
        ILogger logger)
    {
        Ingestion = ingestion;
        Facts = facts;
        _state = state;
        _logger = logger;
    }

    public abstract string SourceKind { get; }

    public async Task<int> ReconcileAsync(bool force = false, CancellationToken ct = default)
    {
        var build = await BuildAsync(ct);
        var factsCurrent = !force && IsCurrent(_state.Fingerprints, build.Fingerprint);
        var graphCurrent = build.ProjectGraph is null
            || (!force && IsCurrent(_state.GraphFingerprints, build.Fingerprint));
        if (factsCurrent && graphCurrent)
        {
            return 0;
        }

        var emitted = 0;
        if (!factsCurrent)
        {
            // Retire-then-emit keeps the kind's facts exactly in sync, including
            // records that were deleted or fell out of the relevant window. Only
            // runs when the fingerprint changed, so churn tracks real data changes.
            await Facts.ForgetBySourceKindAsync(SourceKind, ct);

            foreach (var draft in build.Drafts)
            {
                ct.ThrowIfCancellationRequested();
                await Ingestion.RecordAsync(draft, ct);
                emitted++;
            }

            _state.Fingerprints[SourceKind] = build.Fingerprint;
            if (emitted > 0)
            {
                _logger.LogInformation("Rebuilt {Kind} knowledge: {Facts} facts", SourceKind, emitted);
            }
        }

        // Project graph entities/relations (additive, idempotent — NOT part of
        // the retire-then-emit cycle). Closure built in BuildAsync over the
        // already-fetched data so there's no second fetch.
        if (!graphCurrent)
        {
            try
            {
                await build.ProjectGraph!(ct);
                _state.GraphFingerprints[SourceKind] = build.Fingerprint;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A graph writer defers by throwing (e.g. while a rebuild holds
                // the account) so that its periodic caller retries. Leaving the
                // graph fingerprint unrecorded makes the next pass project again
                // even though the facts above are already current.
                _logger.LogWarning(ex, "{Kind} knowledge-graph projection did not complete; will retry next pass", SourceKind);
            }
        }

        return emitted;
    }

    private bool IsCurrent(ConcurrentDictionary<string, string> fingerprints, string fingerprint)
        => fingerprints.TryGetValue(SourceKind, out var previous) && previous == fingerprint;

    /// <summary>Build the current desired fact set plus a change fingerprint.</summary>
    protected abstract Task<KnowledgeBuildResult> BuildAsync(CancellationToken ct);

    /// <summary>
    /// Build a user-authored context-fact draft. First-party PIM (the user's own
    /// calendar/tasks/contacts) is trusted enough that the claim gate promotes
    /// it, while still carrying a source reference back to the record.
    /// </summary>
    protected ContextFactDraft UserFact(
        string kind,
        string text,
        string normalizedKey,
        ContextSource source,
        DateTimeOffset observedAt,
        DateTimeOffset? expiresAt,
        bool sensitive,
        IReadOnlyDictionary<string, string>? extraMetadata = null,
        IReadOnlyList<string>? extraTags = null)
    {
        var metadata = new Dictionary<string, string>
        {
            ["sourceAuthority"] = KnowledgeSourceAuthorities.UserAuthored,
            [SourceKind + "Id"] = source.RecordId ?? string.Empty
        };
        if (extraMetadata is not null)
        {
            foreach (var kv in extraMetadata)
            {
                metadata[kv.Key] = kv.Value;
            }
        }

        var tags = new List<string> { SourceKind, KnowledgeSourceAuthorities.UserAuthored };
        if (extraTags is not null)
        {
            tags.AddRange(extraTags);
        }

        return new ContextFactDraft
        {
            Kind = kind,
            Text = text,
            NormalizedKey = normalizedKey,
            ObservedAt = observedAt,
            ExpiresAt = expiresAt,
            Confidence = DefaultConfidence,
            IsUserConfirmed = true,
            IsSensitive = sensitive,
            Tags = tags,
            Metadata = metadata,
            Source = source
        };
    }
}

/// <summary>
/// Result of a contributor build pass: the desired facts, a change fingerprint,
/// and an optional graph-projection continuation (closes over the data fetched
/// during the build so the graph step needs no second fetch). The continuation
/// runs when the fingerprint changed or its last projection did not complete.
/// </summary>
public sealed record KnowledgeBuildResult(
    IReadOnlyList<ContextFactDraft> Drafts,
    string Fingerprint,
    Func<CancellationToken, Task>? ProjectGraph = null);
