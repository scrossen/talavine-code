using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SwarmCore.Knowledge;

namespace SwarmCore.PersonalMemory;

/// <summary>A request to capture one statement as a claim.</summary>
public sealed record MemoryCaptureRequest
{
    public required MemoryScope Scope { get; init; }
    public required string Text { get; init; }
    public required MemoryCategory Category { get; init; }
    public required MemorySourceAuthority Authority { get; init; }
    public required MemoryCaptureMethod Method { get; init; }
    public required MemoryEvidence Evidence { get; init; }
    public double Confidence { get; init; } = 0.7;
    public string? Key { get; init; }
    public DateTimeOffset? ValidFrom { get; init; }
    public DateTimeOffset? ValidUntil { get; init; }
    public DateTimeOffset? EventAt { get; init; }
    public DateTimeOffset? StaleAfter { get; init; }

    /// <summary>Optional caller idempotency key; defaults to a hash of evidence identity + text.</summary>
    public string? IdempotencyKey { get; init; }
}

public enum MemoryRejectionReason
{
    None = 0,
    Disabled = 1,
    Incognito = 2,
    Empty = 3,
    TooShort = 4,
    TooLong = 5,
    Secret = 6,
    Instruction = 7,
    InvalidAuthority = 8,
    Suppressed = 9,
    Unsupported = 10,
    LowConfidence = 11,
    InvalidScope = 12,
}

public sealed record MemoryAdmissionDecision(
    bool Admitted,
    MemoryRejectionReason Reason,
    string? Detail,
    MemoryClaim? Claim)
{
    public static MemoryAdmissionDecision Reject(MemoryRejectionReason reason, string? detail = null) => new(false, reason, detail, null);
}

/// <summary>
/// Capture-time admission. Deterministic and side-effect free; the service persists admitted claims.
/// Admission never grants trust: the claim keeps its authority and becomes a fact only through
/// consolidation under <see cref="MemoryTrust"/>.
/// </summary>
public static partial class MemoryAdmissionPolicy
{
    public static MemoryAdmissionDecision Evaluate(
        MemoryCaptureRequest request,
        IReadOnlyList<MemorySuppression> suppressions,
        MemoryAdmissionOptions options,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Scope.IsEmpty) return MemoryAdmissionDecision.Reject(MemoryRejectionReason.InvalidScope);

        var authorityError = ValidateAuthority(request);
        if (authorityError is not null) return MemoryAdmissionDecision.Reject(MemoryRejectionReason.InvalidAuthority, authorityError);

        var text = MemoryTextScreen.Normalize(request.Text);
        if (text.Length == 0) return MemoryAdmissionDecision.Reject(MemoryRejectionReason.Empty);
        var max = request.Category == MemoryCategory.ConversationSummary ? options.MaxSummaryLength : options.MaxTextLength;
        if (text.Length < options.MinTextLength) return MemoryAdmissionDecision.Reject(MemoryRejectionReason.TooShort);
        if (text.Length > max) return MemoryAdmissionDecision.Reject(MemoryRejectionReason.TooLong);
        if (double.IsNaN(request.Confidence) || request.Confidence < options.MinConfidence)
            return MemoryAdmissionDecision.Reject(MemoryRejectionReason.LowConfidence);

        // Screen the raw text too: normalization collapses newlines that role markers rely on.
        var allowStyle = request.Category == MemoryCategory.ResponsePreference;
        foreach (var candidate in new[] { request.Text, text })
        {
            var finding = MemoryTextScreen.Screen(candidate, request.Authority, allowStyle, out var detail);
            if (finding == MemoryScreenFinding.Secret) return MemoryAdmissionDecision.Reject(MemoryRejectionReason.Secret, detail);
            if (finding != MemoryScreenFinding.None) return MemoryAdmissionDecision.Reject(MemoryRejectionReason.Instruction, detail);
        }

        // Evidence excerpts are rendered on control pages; they must not smuggle secrets either.
        if (MemoryTextScreen.ContainsSecret(request.Evidence.Excerpt))
            return MemoryAdmissionDecision.Reject(MemoryRejectionReason.Secret, "Evidence excerpt contains a credential.");

        var key = NormalizeKey(request.Key) ?? GeneratedKey(request.Category, text);
        if (IsSuppressed(key, text, request.Category, suppressions, now, request.Authority))
            return MemoryAdmissionDecision.Reject(MemoryRejectionReason.Suppressed, "Matches a forgotten memory.");

        var userConfirmed = request.Authority == MemorySourceAuthority.UserAuthored
            && request.Method is MemoryCaptureMethod.ExplicitRemember or MemoryCaptureMethod.UserEdit;
        var idempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? DefaultIdempotencyKey(request.Evidence, text)
            : request.IdempotencyKey.Trim();
        var claim = new MemoryClaim
        {
            // Name-based: every replica that captures the same statement creates the same row.
            Id = MemoryIds.ForClaim(request.Scope, idempotencyKey),
            Scope = request.Scope,
            IdempotencyKey = idempotencyKey,
            Key = key,
            Category = request.Category,
            Text = text,
            Authority = request.Authority,
            Method = request.Method,
            Evidence = request.Evidence with { Excerpt = Bound(request.Evidence.Excerpt, 400) },
            Confidence = Math.Clamp(request.Confidence, 0, 1),
            ObservedAt = request.Evidence.ObservedAt,
            ValidFrom = request.ValidFrom,
            ValidUntil = request.ValidUntil,
            EventAt = request.EventAt,
            StaleAfter = request.StaleAfter ?? MemoryTrust.DefaultStaleAfter(request.Category, request.Evidence.ObservedAt),
            Sensitive = MemoryTextScreen.IsSensitive(text),
            UserConfirmed = userConfirmed,
            Status = MemoryClaimStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };
        return new MemoryAdmissionDecision(true, MemoryRejectionReason.None, null, claim);
    }

    /// <summary>Method/authority combinations a host may legitimately produce.</summary>
    public static string? ValidateAuthority(MemoryCaptureRequest request)
    {
        if (request.Evidence is null) return "Evidence is required.";
        if (request.Evidence.Authority != request.Authority) return "Evidence authority must equal claim authority.";
        if (string.IsNullOrWhiteSpace(request.Evidence.SourceKind) || string.IsNullOrWhiteSpace(request.Evidence.SourceId))
            return "Evidence must name a resolvable source.";
        return request.Method switch
        {
            MemoryCaptureMethod.ExplicitRemember or MemoryCaptureMethod.UserEdit or MemoryCaptureMethod.PostTurnExtraction
                when request.Authority != MemorySourceAuthority.UserAuthored => "This capture method requires the person's own words.",
            MemoryCaptureMethod.ConversationSummary
                when request.Authority != MemorySourceAuthority.Agent || request.Category != MemoryCategory.ConversationSummary
                => "Conversation summaries are agent-authored and only in the conversation-summary category.",
            MemoryCaptureMethod.AgentObservation when request.Authority != MemorySourceAuthority.Agent
                => "Agent observations must carry agent authority.",
            MemoryCaptureMethod.Import when request.Authority == MemorySourceAuthority.Agent
                => "Imports cannot carry agent authority.",
            _ when request.Category == MemoryCategory.ConversationSummary && request.Method != MemoryCaptureMethod.ConversationSummary
                => "Only conversation summaries may use the conversation-summary category.",
            _ => null,
        };
    }

    /// <summary>
    /// True when a statement with this key/text/category is blocked by a rule. A rule with
    /// <see cref="MemorySuppression.MaxAuthority"/> (a dismissed suggestion) only blocks statements whose
    /// <paramref name="authority"/> is at most that value; pass null to evaluate every rule.
    /// </summary>
    public static bool IsSuppressed(string key, string text, MemoryCategory category, IReadOnlyList<MemorySuppression> suppressions, DateTimeOffset now,
        MemorySourceAuthority? authority = null)
    {
        foreach (var rule in suppressions)
        {
            if (rule.ExpiresAt is { } expires && expires <= now) continue;
            if (authority is { } incoming && rule.MaxAuthority is { } cap && MemoryTrust.Rank(incoming) > MemoryTrust.Rank(cap)) continue;
            if (Matches(rule, key, text, category)) return true;
        }
        return false;
    }

    /// <summary>
    /// Recall-time check: true when a stored fact must not be recalled because a rule forgets it. Recall
    /// honors forget rules even for rows that exist (a copy consolidated on another device, a row
    /// resurrected by replication), so "forget" holds everywhere. A fact the person explicitly
    /// re-remembered or edited after the rule was created is not blocked.
    /// </summary>
    public static bool IsForgotten(MemoryFact fact, IReadOnlyList<MemorySuppression>? suppressions, DateTimeOffset now)
    {
        if (suppressions is null || suppressions.Count == 0) return false;
        var effective = fact.UserConfirmed ? MemorySourceAuthority.UserAuthored : fact.Authority;
        DateTimeOffset? explicitUserAt = fact.UserConfirmed
            ? fact.Evidence.Where(e => e.Authority == MemorySourceAuthority.UserAuthored).Select(e => (DateTimeOffset?)e.ObservedAt).Max()
            : null;
        foreach (var rule in suppressions)
        {
            if (rule.Scope != fact.Scope) continue;
            if (rule.ExpiresAt is { } expires && expires <= now) continue;
            if (rule.MaxAuthority is { } cap && MemoryTrust.Rank(effective) > MemoryTrust.Rank(cap)) continue;
            if (explicitUserAt is { } at && at >= rule.CreatedAt) continue;
            if (Matches(rule, fact.Key, fact.Text, fact.Category)) return true;
        }
        return false;
    }

    private static bool Matches(MemorySuppression rule, string key, string text, MemoryCategory category)
    {
        if (MemoryForgetFingerprint.IsFingerprint(rule.TextPattern))
        {
            if (rule.Category is { } c && c != category) return false;
            var keyed = !string.IsNullOrWhiteSpace(rule.Key);
            if (keyed && !string.Equals(rule.Key, key, StringComparison.OrdinalIgnoreCase))
                return MemoryForgetFingerprint.SameStatement(rule.TextPattern!, text);
            return MemoryForgetFingerprint.Covers(rule.TextPattern!, text);
        }
        return LegacyMatches(rule, key, text, category);
    }

    private static bool LegacyMatches(MemorySuppression rule, string key, string text, MemoryCategory category)
        => KnowledgeSuppressionPolicy.Matches(
            key, text, category.ToString(), source: null,
            ruleKey: string.IsNullOrWhiteSpace(rule.Key) ? "*" : rule.Key,
            textPattern: rule.TextPattern,
            categoryPattern: rule.Category?.ToString(),
            sourcePattern: null,
            matchIdenticalTextAcrossKeys: true);

    /// <summary>Lowercase "[a-z0-9][a-z0-9:._-]{1,63}" or null.</summary>
    public static string? NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var normalized = key.Trim().ToLowerInvariant();
        return KeyRegex().IsMatch(normalized) ? normalized : null;
    }

    /// <summary>Content-addressed key for statements without a topic key ("auto:" prefix).</summary>
    public static string GeneratedKey(MemoryCategory category, string text)
    {
        var material = $"{category}|{MemoryTextScreen.NormalizeForComparison(text)}";
        return "auto:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..20].ToLowerInvariant();
    }

    public static bool IsGeneratedKey(string? key) => key?.StartsWith("auto:", StringComparison.Ordinal) == true;

    private static string DefaultIdempotencyKey(MemoryEvidence evidence, string text)
        => "h:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            evidence.IdentityKey + "|" + MemoryTextScreen.NormalizeForComparison(text))))[..32].ToLowerInvariant();

    private static string? Bound(string? value, int max)
        => value is null ? null : value.Length <= max ? value : value[..max];

    [GeneratedRegex("^[a-z0-9][a-z0-9:._-]{1,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyRegex();
}
