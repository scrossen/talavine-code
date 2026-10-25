using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.PersonalMemory;

public sealed record MemoryCaptureResult(
    bool Captured,
    MemoryRejectionReason Reason,
    string? Detail,
    MemoryClaim? Claim,
    bool Duplicate = false);

public sealed record MemoryRememberResult(bool Remembered, string Message, MemoryFact? Fact, MemoryRejectionReason Reason);

public enum MemoryItemState
{
    Current = 0,
    Suppressed = 1,
    Superseded = 2,
    Expired = 3,
}

public sealed record MemoryInventoryItem(MemoryFact Fact, MemoryDigestSection Section, MemoryItemState State, bool InDigest);

/// <summary>Everything a "What it knows about you" page shows.</summary>
public sealed record MemoryInventory(
    MemoryScope Scope,
    bool Enabled,
    IReadOnlyList<MemoryInventoryItem> Items,
    IReadOnlyList<MemoryClaim> Suggestions,
    MemoryProfileDigest Digest);

public enum MemoryEditOutcome
{
    Applied = 0,
    NotFound = 1,
    Conflict = 2,
    Rejected = 3,
}

public sealed record MemoryEditResult(MemoryEditOutcome Outcome, string? Detail = null, MemoryFact? Fact = null)
{
    public bool Succeeded => Outcome == MemoryEditOutcome.Applied;
}

/// <summary>
/// The host-facing facade: capture (explicit remember, post-turn extraction, imports, conversation
/// summaries), recall reads, and the person's controls (edit, suppress, forget, confirm/dismiss
/// suggestions). Capture and recall honor the global switch, host availability and the turn's
/// incognito flag; the control operations always work so a person can inspect and delete what is
/// stored even after turning memory off.
/// </summary>
public sealed class PersonalMemoryService
{
    private static readonly MemoryClaimStatus[] SuggestionStatuses = [MemoryClaimStatus.AwaitingConfirmation, MemoryClaimStatus.AwaitingCorroboration];

    private readonly IPersonalMemoryStore _store;
    private readonly MemoryConsolidationService _consolidation;
    private readonly PersonalMemoryOptions _options;
    private readonly IPersonalMemoryAvailability _availability;
    private readonly ILlmRouter? _router;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public PersonalMemoryService(
        IPersonalMemoryStore store,
        MemoryConsolidationService consolidation,
        PersonalMemoryOptions options,
        IPersonalMemoryAvailability? availability = null,
        ILlmRouter? router = null,
        TimeProvider? time = null,
        ILogger<PersonalMemoryService>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _consolidation = consolidation ?? throw new ArgumentNullException(nameof(consolidation));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _availability = availability ?? new AlwaysEnabledPersonalMemoryAvailability();
        _router = router;
        _time = time ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public PersonalMemoryOptions Options => _options;

    /// <summary>True when memory may be read or written for this scope/turn.</summary>
    public async ValueTask<bool> IsActiveAsync(MemoryScope scope, MemoryTurnContext? turn, CancellationToken ct = default)
    {
        if (!_options.Enabled || scope.IsEmpty) return false;
        if (turn is not null && (turn.Incognito || turn.Scope != scope)) return false;
        return await _availability.IsEnabledAsync(scope, ct).ConfigureAwait(false);
    }

    // ---------------- capture ----------------

    /// <summary>Capture one statement as a claim (imports, email, host-specific producers).</summary>
    public Task<MemoryCaptureResult> CaptureAsync(MemoryCaptureRequest request, MemoryTurnContext? turn = null, CancellationToken ct = default)
        => CaptureCoreAsync(request, turn, ignoreSuppressions: false, ct);

    private async Task<MemoryCaptureResult> CaptureCoreAsync(MemoryCaptureRequest request, MemoryTurnContext? turn, bool ignoreSuppressions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (turn?.Incognito == true) return new(false, MemoryRejectionReason.Incognito, null, null);
        if (!await IsActiveAsync(request.Scope, turn, ct).ConfigureAwait(false))
            return new(false, MemoryRejectionReason.Disabled, null, null);

        var now = _time.GetUtcNow();
        IReadOnlyList<MemorySuppression> suppressions = ignoreSuppressions
            ? []
            : await _store.GetSuppressionsAsync(request.Scope, ct).ConfigureAwait(false);
        var decision = MemoryAdmissionPolicy.Evaluate(request, suppressions, _options.Admission, now);
        if (!decision.Admitted) return new(false, decision.Reason, decision.Detail, null);

        var appended = await _store.AppendClaimAsync(decision.Claim!, ct).ConfigureAwait(false);
        return new(true, MemoryRejectionReason.None, null, appended.Claim, Duplicate: !appended.Created);
    }

    /// <summary>
    /// Explicit "remember this". The text must be supported by the person's own words in this turn
    /// (<see cref="MemoryTurnContext.AuthenticatedUserMessage"/>), so injected content cannot make
    /// the assistant "remember" something the person never said. Applied immediately.
    /// </summary>
    public async Task<MemoryRememberResult> RememberAsync(MemoryTurnContext turn, string text, MemoryCategory? category = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        if (turn.Incognito) return new(false, "Memory is off for this temporary conversation; nothing was saved.", null, MemoryRejectionReason.Incognito);
        if (!await IsActiveAsync(turn.Scope, turn, ct).ConfigureAwait(false))
            return new(false, "Memory is turned off.", null, MemoryRejectionReason.Disabled);
        var normalized = MemoryTextScreen.Normalize(text);
        // No category named: the person's own answer-style preference ("Always keep answers short") is a
        // response preference, not an instruction to refuse.
        var resolvedCategory = category is { } c && c != MemoryCategory.ConversationSummary ? c : MemoryUserIntent.InferCategory(normalized);
        // An answer-style preference may be restated with preference framing ("User prefers short,
        // bullet-point answers" for "keep your answers short, bullet points"); everything else must use
        // the person's own words.
        var supported = !string.IsNullOrWhiteSpace(turn.AuthenticatedUserMessage)
            && (resolvedCategory == MemoryCategory.ResponsePreference
                ? MemoryUserSupport.IsSupportedStylePreference(turn.AuthenticatedUserMessage, normalized)
                : MemoryUserSupport.IsSupported(turn.AuthenticatedUserMessage, normalized));
        if (!supported)
            return new(false, "Only something the user said in this message can be remembered. Restate it using the user's own words.", null, MemoryRejectionReason.Unsupported);
        var now = _time.GetUtcNow();
        // An explicit request in the person's own words overrides an earlier "forget".
        var capture = await CaptureCoreAsync(new MemoryCaptureRequest
        {
            Scope = turn.Scope,
            Text = normalized,
            Category = resolvedCategory,
            Authority = MemorySourceAuthority.UserAuthored,
            Method = MemoryCaptureMethod.ExplicitRemember,
            Confidence = 1.0,
            IdempotencyKey = $"remember:{turn.TurnId ?? Hash(turn.AuthenticatedUserMessage)}:{MemoryAdmissionPolicy.GeneratedKey(resolvedCategory, normalized)}",
            Evidence = new MemoryEvidence
            {
                SourceKind = MemoryExtraction.SourceKind,
                SourceId = turn.TurnId ?? Hash(turn.AuthenticatedUserMessage),
                Authority = MemorySourceAuthority.UserAuthored,
                ObservedAt = now,
                Origin = "user:" + turn.Scope.SubjectId,
                SourceTitle = turn.ConversationId,
                Excerpt = normalized,
            },
        }, turn, ignoreSuppressions: true, ct).ConfigureAwait(false);
        if (!capture.Captured) return new(false, Describe(capture.Reason, capture.Detail), null, capture.Reason);

        var report = await _consolidation.ApplyClaimsNowAsync(turn.Scope, [capture.Claim!], ct).ConfigureAwait(false);
        var factId = report.ClaimResults.TryGetValue(capture.Claim!.Id, out var id) ? id : capture.Claim.ResultFactId;
        var fact = factId is { } fid ? await _store.GetFactAsync(turn.Scope, fid, ct).ConfigureAwait(false) : null;
        if (fact is null)
            return new(false, "The memory was recorded but could not be applied yet; it will be reconciled later.", null, MemoryRejectionReason.None);
        // Say when this replaced an earlier memory, so the assistant does not tell the person the old one is
        // still saved (or offer to delete what is already history).
        var replaced = fact.SupersedesId is { } previousId ? await _store.GetFactAsync(turn.Scope, previousId, ct).ConfigureAwait(false) : null;
        var message = replaced is null
            ? "Remembered."
            : $"Remembered. This replaces the earlier memory \"{replaced.Text}\" ({replaced.Reference}), which is no longer used and is kept only as history.";
        return new(true, message, fact, MemoryRejectionReason.None);
    }

    /// <summary>
    /// Post-turn extraction from the person's own message (host runs this in the background after
    /// a turn completes). Requires a router; without one this is a no-op.
    /// </summary>
    public async Task<IReadOnlyList<MemoryCaptureResult>> CaptureTurnAsync(MemoryTurnContext turn, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        if (_router is null || turn.Incognito || string.IsNullOrWhiteSpace(turn.AuthenticatedUserMessage) || string.IsNullOrWhiteSpace(turn.TurnId))
            return [];
        if (!await IsActiveAsync(turn.Scope, turn, ct).ConfigureAwait(false)) return [];

        var known = await _store.GetFactsAsync(turn.Scope, MemoryFactQuery.Current, ct).ConfigureAwait(false);
        var input = new MemoryExtractionInput(turn.Scope, turn.AuthenticatedUserMessage, turn.TurnId, _time.GetUtcNow(), known, turn.ConversationId);
        string raw;
        try
        {
            raw = await _router.GenerateResponseAsync(_options.FunctionType, MemoryExtraction.BuildPrompt(input), _options.ApplyRoute(new LlmRouterOptions
            {
                SourceType = "personal-memory-extraction",
                SourceId = MemoryConsolidationService.ScopeHash(turn.Scope),
                Temperature = 0f,
                MaxTokens = 800,
                Interactivity = LlmInteractivity.Background,
                PrivacyClass = _options.PrivacyClass,
                ForceJson = true,
                RequestCancellationToken = ct,
            })).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Personal memory extraction skipped; the LLM call failed.");
            return [];
        }

        if (!MemoryExtraction.TryParse(raw, out var candidates)) return [];
        var validated = MemoryExtraction.Validate(input, candidates);
        var results = new List<MemoryCaptureResult>();
        foreach (var request in validated.Accepted)
            results.Add(await CaptureAsync(request, turn, ct).ConfigureAwait(false));
        return results;
    }

    /// <summary>Record an agent-written summary of a conversation for the "recent conversations" digest section.</summary>
    public Task<MemoryCaptureResult> CaptureConversationSummaryAsync(MemoryTurnContext turn, string conversationId, string summary, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        var now = _time.GetUtcNow();
        return CaptureAsync(new MemoryCaptureRequest
        {
            Scope = turn.Scope,
            Text = summary,
            Category = MemoryCategory.ConversationSummary,
            Authority = MemorySourceAuthority.Agent,
            Method = MemoryCaptureMethod.ConversationSummary,
            Confidence = 0.7,
            Key = "conversation:" + Hash(conversationId)[..16],
            IdempotencyKey = $"summary:{conversationId}:{Hash(MemoryTextScreen.NormalizeForComparison(summary))[..16]}",
            Evidence = new MemoryEvidence
            {
                SourceKind = "conversation",
                SourceId = conversationId,
                Authority = MemorySourceAuthority.Agent,
                ObservedAt = now,
                Origin = "agent:conversation-summary",
            },
        }, turn, ct);
    }

    /// <summary>
    /// Summarize the person's own messages of a conversation into the "recent conversations" section
    /// (shared prompt and contract: <see cref="MemoryConversationSummary"/>). Returns null when memory is
    /// off for the turn, there is no router, the model failed, or its answer broke the contract.
    /// <paramref name="routing"/> lets a host pin the conversation's model; defaults to background routing.
    /// </summary>
    public async Task<MemoryCaptureResult?> SummarizeConversationAsync(
        MemoryTurnContext turn,
        string conversationId,
        IReadOnlyList<string> userMessagesOldestFirst,
        LlmRouterOptions? routing = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        if (_router is null || turn.Incognito || userMessagesOldestFirst.Count == 0) return null;
        if (!await IsActiveAsync(turn.Scope, turn, ct).ConfigureAwait(false)) return null;
        string raw;
        try
        {
            raw = await _router.GenerateResponseAsync(_options.FunctionType, MemoryConversationSummary.BuildPrompt(userMessagesOldestFirst),
                _options.ApplyRoute(routing ?? new LlmRouterOptions
                {
                    SourceType = "personal-memory-summary",
                    SourceId = MemoryConsolidationService.ScopeHash(turn.Scope),
                    Temperature = 0f,
                    MaxTokens = 120,
                    Interactivity = LlmInteractivity.Background,
                    PrivacyClass = _options.PrivacyClass,
                    RequestCancellationToken = ct,
                })).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Conversation summary for personal memory skipped; the LLM call failed.");
            return null;
        }
        var summary = MemoryConversationSummary.Normalize(raw);
        if (summary is null) return null;
        return await CaptureConversationSummaryAsync(turn, conversationId, summary, ct).ConfigureAwait(false);
    }

    // ---------------- recall ----------------

    /// <summary>The bounded digest for the turn, or null when memory is off/incognito. Honors forget rules.</summary>
    public async Task<MemoryProfileDigest?> GetDigestAsync(MemoryTurnContext turn, CancellationToken ct = default)
    {
        if (!await IsActiveAsync(turn.Scope, turn, ct).ConfigureAwait(false)) return null;
        var now = _time.GetUtcNow();
        var facts = await _store.GetFactsAsync(turn.Scope, new MemoryFactQuery { CurrentOnly = true, AsOf = now }, ct).ConfigureAwait(false);
        var suppressions = await _store.GetSuppressionsAsync(turn.Scope, ct).ConfigureAwait(false);
        return MemoryProfileDigestBuilder.Build(turn.Scope, facts, _options.Digest, now, suppressions);
    }

    /// <summary>Every recallable fact (not budget-limited), honoring forget rules; empty when memory is off/incognito.</summary>
    public async Task<IReadOnlyList<MemoryFact>> GetRecallableFactsAsync(MemoryTurnContext turn, CancellationToken ct = default)
    {
        if (!await IsActiveAsync(turn.Scope, turn, ct).ConfigureAwait(false)) return [];
        var now = _time.GetUtcNow();
        var facts = await _store.GetFactsAsync(turn.Scope, new MemoryFactQuery { CurrentOnly = true, AsOf = now }, ct).ConfigureAwait(false);
        var suppressions = await _store.GetSuppressionsAsync(turn.Scope, ct).ConfigureAwait(false);
        return facts.Where(f => f.Scope == turn.Scope && MemoryProfileDigestBuilder.IsRecallable(f, _options.Digest, now, suppressions))
            .OrderBy(f => MemoryTrust.SectionFor(f.Category))
            .ThenBy(f => f, MemoryProfileDigestBuilder.PriorityComparer.Instance)
            .ToList();
    }

    /// <summary>Relevance search over recallable facts; empty when memory is off/incognito. Honors forget rules.</summary>
    public async Task<IReadOnlyList<MemorySearchHit>> SearchAsync(MemoryTurnContext turn, string query, int limit = 8, CancellationToken ct = default)
    {
        if (!await IsActiveAsync(turn.Scope, turn, ct).ConfigureAwait(false)) return [];
        var now = _time.GetUtcNow();
        var facts = await _store.GetFactsAsync(turn.Scope, new MemoryFactQuery { CurrentOnly = true, AsOf = now }, ct).ConfigureAwait(false);
        var suppressions = await _store.GetSuppressionsAsync(turn.Scope, ct).ConfigureAwait(false);
        var pool = facts.Where(f => f.Scope == turn.Scope && MemoryProfileDigestBuilder.IsRecallable(f, _options.Digest, now, suppressions)).ToList();
        return MemoryRelevanceRanker.Rank(pool, query, Math.Clamp(limit, 1, 50), minimumScore: 0.5);
    }

    // ---------------- control page ----------------

    /// <summary>Everything stored for the scope, including history and suggestions. Not gated by the memory switch.</summary>
    public async Task<MemoryInventory> GetInventoryAsync(MemoryScope scope, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        var facts = (await _store.GetFactsAsync(scope, MemoryFactQuery.All, ct).ConfigureAwait(false)).Where(f => f.Scope == scope).ToList();
        var suppressions = await _store.GetSuppressionsAsync(scope, ct).ConfigureAwait(false);
        var digest = MemoryProfileDigestBuilder.Build(scope, facts, _options.Digest, now, suppressions);
        var inDigest = digest.FactIds.ToHashSet();
        var items = facts
            .Select(f => new MemoryInventoryItem(f, MemoryTrust.SectionFor(f.Category), StateOf(f, now), inDigest.Contains(f.Id)))
            .OrderBy(i => i.State).ThenBy(i => i.Section).ThenByDescending(i => i.Fact.UpdatedAt).ThenBy(i => i.Fact.Id)
            .ToList();
        // A forgotten statement is never offered back as a suggestion.
        var suggestions = (await _store.GetClaimsAsync(scope, SuggestionStatuses, 200, ct).ConfigureAwait(false))
            .Where(c => c.Scope == scope && !MemoryAdmissionPolicy.IsSuppressed(c.Key ?? string.Empty, c.Text, c.Category,
                suppressions.Where(s => s.IsForgetRule).ToList(), now))
            .ToList();
        var enabled = _options.Enabled && await _availability.IsEnabledAsync(scope, ct).ConfigureAwait(false);
        return new MemoryInventory(scope, enabled, items, suggestions, digest);
    }

    /// <summary>The person edits a fact. Recorded as a user-authored supersede so history is kept.</summary>
    public async Task<MemoryEditResult> EditAsync(MemoryScope scope, Guid factId, long expectedVersion, string newText, MemoryCategory? category = null, CancellationToken ct = default)
    {
        using var _ = await _consolidation.Gate.AcquireAsync(scope.Key, ct).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var fact = await _store.GetFactAsync(scope, factId, ct).ConfigureAwait(false);
        if (fact is null || fact.Scope != scope) return new(MemoryEditOutcome.NotFound);
        if (fact.Version != expectedVersion) return new(MemoryEditOutcome.Conflict, "The memory changed since it was shown.");
        if (fact.SupersededById is not null) return new(MemoryEditOutcome.Conflict, "A newer version of this memory exists.");

        var resolvedCategory = category is { } c && c != MemoryCategory.ConversationSummary ? c : fact.Category == MemoryCategory.ConversationSummary ? MemoryCategory.Other : fact.Category;
        var decision = MemoryAdmissionPolicy.Evaluate(new MemoryCaptureRequest
        {
            Scope = scope,
            Text = newText,
            Category = resolvedCategory,
            Authority = MemorySourceAuthority.UserAuthored,
            Method = MemoryCaptureMethod.UserEdit,
            Confidence = 1.0,
            Key = fact.Key,
            EventAt = fact.EventAt,
            IdempotencyKey = $"edit:{fact.Id:N}:{fact.Version}",
            Evidence = new MemoryEvidence
            {
                SourceKind = "memory-control",
                SourceId = $"{fact.Id:N}@{fact.Version}",
                Authority = MemorySourceAuthority.UserAuthored,
                ObservedAt = now,
                Origin = "user:" + scope.SubjectId,
            },
        }, [], _options.Admission, now); // the person's own edit overrides earlier forget rules
        if (!decision.Admitted) return new(MemoryEditOutcome.Rejected, Describe(decision.Reason, decision.Detail));

        var appended = await _store.AppendClaimAsync(decision.Claim!, ct).ConfigureAwait(false);
        var claim = appended.Claim;
        if (claim.Status != MemoryClaimStatus.Pending) return new(MemoryEditOutcome.Conflict, "This edit was already applied.");

        var replacement = new MemoryFact
        {
            Id = MemoryIds.ForFact("edit", fact.Id, [claim.Id]),
            Scope = scope,
            Key = fact.Key,
            Category = resolvedCategory,
            Text = claim.Text,
            Authority = MemorySourceAuthority.UserAuthored,
            Evidence = [.. fact.Evidence, claim.Evidence],
            Confidence = 1.0,
            ValidFrom = now,
            EventAt = fact.EventAt,
            StaleAfter = null,
            SupersedesId = fact.Id,
            Suppressed = fact.Suppressed,
            UserConfirmed = true,
            Sensitive = claim.Sensitive,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var result = await _store.CommitAsync(scope, MemoryChangeSet.Of(
            new MemoryFactInsert(replacement),
            new MemoryFactUpdate(fact.Id, fact.Version, new MemoryFactPatch
            {
                SupersededById = MemoryField<Guid?>.Set(replacement.Id),
                ValidUntil = MemoryField<DateTimeOffset?>.Set(now),
            }),
            new MemoryClaimTransition(claim.Id, claim.Version, MemoryClaimStatus.Applied, replacement.Id, "user-edit")), ct).ConfigureAwait(false);
        if (!result.Succeeded) return new(MemoryEditOutcome.Conflict, result.Detail);
        return new(MemoryEditOutcome.Applied, null, await _store.GetFactAsync(scope, replacement.Id, ct).ConfigureAwait(false));
    }

    /// <summary>"Don't bring this up" (or undo it). The fact stays visible on the control page.</summary>
    public async Task<MemoryEditResult> SetSuppressedAsync(MemoryScope scope, Guid factId, long expectedVersion, bool suppressed, CancellationToken ct = default)
    {
        using var _ = await _consolidation.Gate.AcquireAsync(scope.Key, ct).ConfigureAwait(false);
        var fact = await _store.GetFactAsync(scope, factId, ct).ConfigureAwait(false);
        if (fact is null || fact.Scope != scope) return new(MemoryEditOutcome.NotFound);
        if (fact.Version != expectedVersion) return new(MemoryEditOutcome.Conflict, "The memory changed since it was shown.");
        var result = await _store.CommitAsync(scope, MemoryChangeSet.Of(
            new MemoryFactUpdate(factId, expectedVersion, new MemoryFactPatch { Suppressed = MemoryField<bool>.Set(suppressed) })), ct).ConfigureAwait(false);
        return result.Succeeded
            ? new(MemoryEditOutcome.Applied, null, await _store.GetFactAsync(scope, factId, ct).ConfigureAwait(false))
            : new(MemoryEditOutcome.Conflict, result.Detail);
    }

    /// <summary>
    /// Forget: hard-deletes the fact, its earlier versions, every other stored copy of the same
    /// statement (a duplicate consolidated on another device, a superseded duplicate), the claims that
    /// produced them and any claim with the same words (including suggestions that never became a fact),
    /// and records suppression rules so the same statements are not relearned or recalled.
    /// </summary>
    public async Task<MemoryEditResult> ForgetAsync(MemoryScope scope, Guid factId, long expectedVersion, CancellationToken ct = default)
    {
        using var _ = await _consolidation.Gate.AcquireAsync(scope.Key, ct).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var fact = await _store.GetFactAsync(scope, factId, ct).ConfigureAwait(false);
        if (fact is null || fact.Scope != scope) return new(MemoryEditOutcome.NotFound);
        if (fact.Version != expectedVersion) return new(MemoryEditOutcome.Conflict, "The memory changed since it was shown.");

        var chain = new List<MemoryFact> { fact };
        var cursor = fact;
        while (cursor.SupersedesId is { } previousId && chain.Count < 100)
        {
            var previous = await _store.GetFactAsync(scope, previousId, ct).ConfigureAwait(false);
            if (previous is null || chain.Any(f => f.Id == previous.Id)) break;
            chain.Add(previous);
            cursor = previous;
        }
        var chainTexts = chain.Select(f => MemoryTextScreen.NormalizeForComparison(f.Text)).ToHashSet(StringComparer.Ordinal);
        var candidates = await _store.GetForgetCandidatesAsync(scope, chainTexts, chain.Select(f => f.Id).ToArray(), ct).ConfigureAwait(false);
        // Other rows carrying the same statement: never leave a copy behind that could be recalled.
        foreach (var copy in candidates.SameStatementFacts.Where(f => f.Scope == scope && chain.All(c => c.Id != f.Id)))
            chain.Add(copy);
        var chainIds = chain.Select(f => f.Id).ToHashSet();
        var claims = candidates.Claims
            .Where(c => c.Scope == scope
                && ((c.ResultFactId is { } r && chainIds.Contains(r)) || chainTexts.Contains(MemoryTextScreen.NormalizeForComparison(c.Text))))
            .DistinctBy(c => c.Id)
            .ToList();

        var changes = new List<MemoryChange>();
        changes.AddRange(chain.Select(f => new MemoryFactDelete(f.Id, f.Version)));
        changes.AddRange(claims.Select(c => new MemoryClaimDelete(c.Id, c.Version)));
        // A surviving fact must not keep pointing at a deleted successor: the person's own earlier
        // statement becomes current again; anything else stays closed as history.
        foreach (var survivor in candidates.SupersededByForgotten
                     .Where(f => f.Scope == scope && !chainIds.Contains(f.Id) && f.SupersededById is { } next && chainIds.Contains(next))
                     .DistinctBy(f => f.Id))
        {
            changes.Add(new MemoryFactUpdate(survivor.Id, survivor.Version, new MemoryFactPatch
            {
                SupersededById = MemoryField<Guid?>.Set(null),
                ValidUntil = MemoryField<DateTimeOffset?>.Set(MemoryTrust.IsUserOwned(survivor) ? null : survivor.ValidUntil ?? now),
            }));
        }
        foreach (var text in chain.Select(f => (f.Key, f.Text, f.Category)).Distinct())
        {
            changes.Add(new MemorySuppressionInsert(new MemorySuppression
            {
                Scope = scope,
                Key = MemoryAdmissionPolicy.IsGeneratedKey(text.Key) ? null : text.Key,
                // A fingerprint, not the words: forgetting must not leave the forgotten text behind.
                TextPattern = MemoryForgetFingerprint.Create(text.Text),
                Reason = "forgotten by the user",
                CreatedAt = now,
            }));
        }
        var result = await _store.CommitAsync(scope, new MemoryChangeSet(changes), ct).ConfigureAwait(false);
        return result.Succeeded ? new(MemoryEditOutcome.Applied) : new(MemoryEditOutcome.Conflict, result.Detail);
    }

    /// <summary>The person confirms a suggestion (agent observation or uncorroborated third-party claim).</summary>
    public async Task<MemoryEditResult> ConfirmSuggestionAsync(MemoryScope scope, Guid claimId, long expectedVersion, string? correctedText = null, CancellationToken ct = default)
    {
        var original = await _store.GetClaimAsync(scope, claimId, ct).ConfigureAwait(false);
        if (original is null || original.Scope != scope) return new(MemoryEditOutcome.NotFound);
        if (original.Version != expectedVersion) return new(MemoryEditOutcome.Conflict, "The suggestion changed since it was shown.");
        if (original.Status is not (MemoryClaimStatus.AwaitingConfirmation or MemoryClaimStatus.AwaitingCorroboration))
            return new(MemoryEditOutcome.Conflict, "This suggestion was already handled.");

        var now = _time.GetUtcNow();
        var suppressions = await _store.GetSuppressionsAsync(scope, ct).ConfigureAwait(false);
        var category = original.Category == MemoryCategory.ConversationSummary ? MemoryCategory.Other : original.Category;
        var decision = MemoryAdmissionPolicy.Evaluate(new MemoryCaptureRequest
        {
            Scope = scope,
            Text = string.IsNullOrWhiteSpace(correctedText) ? original.Text : correctedText,
            Category = category,
            Authority = MemorySourceAuthority.UserAuthored,
            Method = MemoryCaptureMethod.UserEdit,
            Confidence = 1.0,
            Key = original.Key,
            EventAt = original.EventAt,
            ValidUntil = original.ValidUntil,
            IdempotencyKey = $"confirm:{original.Id:N}",
            Evidence = new MemoryEvidence
            {
                SourceKind = "memory-control",
                SourceId = $"confirm:{original.Id:N}",
                Authority = MemorySourceAuthority.UserAuthored,
                ObservedAt = now,
                Origin = "user:" + scope.SubjectId,
            },
        }, suppressions, _options.Admission, now);
        if (!decision.Admitted) return new(MemoryEditOutcome.Rejected, Describe(decision.Reason, decision.Detail));

        var appended = await _store.AppendClaimAsync(decision.Claim!, ct).ConfigureAwait(false);
        Guid? factId = appended.Claim.ResultFactId;
        if (appended.Claim.Status == MemoryClaimStatus.Pending)
        {
            var report = await _consolidation.ApplyClaimsNowAsync(scope, [appended.Claim], ct).ConfigureAwait(false);
            factId = report.ClaimResults.TryGetValue(appended.Claim.Id, out var id) ? id : null;
        }
        if (factId is null) return new(MemoryEditOutcome.Conflict, "The confirmation could not be applied yet.");

        var transition = await _store.CommitAsync(scope, MemoryChangeSet.Of(
            new MemoryClaimTransition(original.Id, original.Version, MemoryClaimStatus.Applied, factId, "confirmed-by-user")), ct).ConfigureAwait(false);
        if (!transition.Succeeded) return new(MemoryEditOutcome.Conflict, transition.Detail);
        return new(MemoryEditOutcome.Applied, null, await _store.GetFactAsync(scope, factId.Value, ct).ConfigureAwait(false));
    }

    /// <summary>The person dismisses a suggestion; it is rejected and suppressed from being re-suggested.</summary>
    public async Task<MemoryEditResult> DismissSuggestionAsync(MemoryScope scope, Guid claimId, long expectedVersion, CancellationToken ct = default)
    {
        var claim = await _store.GetClaimAsync(scope, claimId, ct).ConfigureAwait(false);
        if (claim is null || claim.Scope != scope) return new(MemoryEditOutcome.NotFound);
        if (claim.Version != expectedVersion) return new(MemoryEditOutcome.Conflict, "The suggestion changed since it was shown.");
        var result = await _store.CommitAsync(scope, MemoryChangeSet.Of(
            new MemoryClaimTransition(claim.Id, claim.Version, MemoryClaimStatus.Rejected, null, "dismissed-by-user"),
            new MemorySuppressionInsert(new MemorySuppression
            {
                Scope = scope,
                TextPattern = MemoryForgetFingerprint.Create(claim.Text),
                Reason = "suggestion dismissed by the user",
                CreatedAt = _time.GetUtcNow(),
                // Dismissing an agent/third-party suggestion stops it being suggested again; it never
                // blocks the person's own later statement with the same words.
                MaxAuthority = claim.Authority == MemorySourceAuthority.UserAuthored ? MemorySourceAuthority.TrustedContact : claim.Authority,
            })), ct).ConfigureAwait(false);
        return result.Succeeded ? new(MemoryEditOutcome.Applied) : new(MemoryEditOutcome.Conflict, result.Detail);
    }

    internal static MemoryItemState StateOf(MemoryFact fact, DateTimeOffset now)
        => fact.SupersededById is not null ? MemoryItemState.Superseded
            : fact.ValidUntil is { } until && until <= now ? MemoryItemState.Expired
            : fact.Suppressed ? MemoryItemState.Suppressed
            : MemoryItemState.Current;

    internal static string Describe(MemoryRejectionReason reason, string? detail) => reason switch
    {
        MemoryRejectionReason.Secret => "Not saved: memories never store passwords, codes, account numbers or other secrets.",
        MemoryRejectionReason.Instruction => "Not saved: a memory must be a statement about the user, not an instruction. " + detail,
        MemoryRejectionReason.Suppressed => "Not saved: the user previously asked to forget this.",
        MemoryRejectionReason.TooShort => "Not saved: too short to be a useful memory.",
        MemoryRejectionReason.TooLong => "Not saved: memories must be short statements.",
        MemoryRejectionReason.Incognito => "Memory is off for this temporary conversation; nothing was saved.",
        MemoryRejectionReason.Disabled => "Memory is turned off.",
        MemoryRejectionReason.Unsupported => "Not saved: it is not supported by the user's own words.",
        _ => "Not saved: " + (detail ?? reason.ToString()),
    };

    private static string Hash(string? value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty))).ToLowerInvariant();
}
