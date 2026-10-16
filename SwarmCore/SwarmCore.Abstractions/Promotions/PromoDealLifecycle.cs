using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SwarmCore.Abstractions.Promotions;

/// <summary>
/// Sealed lifecycle rules for promotional deals tracked from marketing mail.
/// Hosts supply the messages, the model call, persistence, and the trash
/// operation; these rules decide when a deal expires, when its source message
/// may be removed, and when the record itself is purged. Keeping the rules
/// here means every host (and every test) ages a deal the same way.
/// </summary>
public static class PromoDealLifecycle
{
    /// <summary>A deal without a stated end date is assumed to last this long after its message arrived.</summary>
    public static readonly TimeSpan DefaultValidity = TimeSpan.FromDays(30);

    /// <summary>Expired deals are retained this long so the user can still see what ended, then purged.</summary>
    public static readonly TimeSpan PurgeDelay = TimeSpan.FromDays(10);

    /// <summary>Deals older than this at intake are not worth extracting; they have almost certainly ended.</summary>
    public static readonly TimeSpan IntakeLookback = TimeSpan.FromDays(30);

    /// <summary>
    /// Resolves the instant a deal stops being active. A stated date-only
    /// expiration means "valid through that day" in the user's time zone, so
    /// the deal expires at the following local midnight. Missing or unparseable
    /// dates fall back to <see cref="DefaultValidity"/> after the message
    /// arrived and are reported as inferred.
    /// </summary>
    public static PromoExpiration ResolveExpiration(
        string? statedExpiresOn,
        DateTime receivedUtc,
        TimeZoneInfo? localZone = null)
    {
        var zone = localZone ?? TimeZoneInfo.Local;
        var received = DateTime.SpecifyKind(receivedUtc, DateTimeKind.Utc);

        if (TryParseIsoDate(statedExpiresOn, out var date))
        {
            var localEnd = date.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            // Zones that spring forward at midnight (Santiago, Asunción, Havana, Beirut, Cairo
            // in some years) have no 00:00 that day; the day then ends at the first instant
            // that exists, and ConvertTimeToUtc would otherwise throw.
            while (zone.IsInvalidTime(localEnd))
                localEnd = localEnd.AddHours(1);
            var expiresUtc = TimeZoneInfo.ConvertTimeToUtc(localEnd, zone);
            // A model may "read" an expiration from last year's copy of the same
            // campaign. Anything that ended before the message arrived is not a
            // usable date; fall back to the default window instead of creating
            // an already-expired deal that would immediately trash the mail.
            if (expiresUtc > received)
                return new PromoExpiration(expiresUtc, Inferred: false);
        }

        return new PromoExpiration(received + DefaultValidity, Inferred: true);
    }

    /// <summary>Parses a YYYY-MM-DD (or ISO date-time) value produced by the extractor.</summary>
    public static bool TryParseIsoDate(string? value, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim();
        if (DateOnly.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return true;
        if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dateTime))
        {
            date = DateOnly.FromDateTime(dateTime);
            return true;
        }
        return false;
    }

    /// <summary>True once the deal's end instant has passed.</summary>
    public static bool IsExpired(DateTime expiresAtUtc, DateTime nowUtc) => nowUtc >= expiresAtUtc;

    /// <summary>The instant an expired deal's record should be deleted.</summary>
    public static DateTime PurgeAfter(DateTime expiredAtUtc) => expiredAtUtc + PurgeDelay;

    /// <summary>
    /// A source message may be moved to Trash only when every deal it carried
    /// has ended and the user has not flagged it for their own reasons. A
    /// message that still carries one live deal stays, even if its other
    /// offers ended.
    /// </summary>
    public static bool ShouldTrashSourceMessage(
        IReadOnlyCollection<PromoDealState> dealsInMessage,
        bool messageIsFlagged)
    {
        if (messageIsFlagged || dealsInMessage.Count == 0) return false;
        return dealsInMessage.All(d => d.Status is PromoDealStatuses.Expired);
    }

    /// <summary>
    /// Stable identity for a deal inside one message, so re-running extraction
    /// against the same mail (a retry, a re-scan after restart) updates the
    /// existing row instead of duplicating it.
    /// </summary>
    public static string BuildDealKey(Guid accountId, Guid messageId, string title, string? promoCode)
    {
        var normalized = NormalizeForKey(title) + "|" + NormalizeForKey(promoCode ?? string.Empty);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"{accountId:N}:{messageId:N}:{Convert.ToHexString(hash.AsSpan(0, 12))}";
    }

    /// <summary>
    /// Presentation order: deals in the user's priority categories first (food
    /// and grocery by default), then deals matching their interests, then the
    /// ones ending soonest, so the card leads with what matters and what is
    /// about to disappear.
    /// </summary>
    public static IReadOnlyList<T> Rank<T>(IEnumerable<T> deals, Func<T, PromoDealRankInputs> select)
        => deals
            .OrderByDescending(d => select(d).PriorityCategory)
            .ThenByDescending(d => select(d).MatchesInterest)
            .ThenByDescending(d => select(d).InterestScore)
            .ThenBy(d => select(d).ExpiresAtUtc)
            .ToList();

    /// <summary>Token overlap at or above this counts two same-merchant offers as one.</summary>
    public const double DuplicateTitleOverlap = 0.6;

    /// <summary>
    /// Merchants resend the same campaign under slightly different subject
    /// lines. Two deals from one merchant are the same offer when they end on
    /// the same day, carry the same code (or none), and their titles share most
    /// of their words. The newest message wins so the card opens the latest
    /// copy; the older rows still age and clean up on their own schedule.
    /// </summary>
    public static IReadOnlyList<T> CollapseDuplicates<T>(IEnumerable<T> deals, Func<T, PromoDealIdentity> select)
    {
        var kept = new List<(T Item, PromoDealIdentity Identity, HashSet<string> Tokens)>();
        foreach (var candidate in deals.OrderByDescending(d => select(d).ReceivedAtUtc))
        {
            var identity = select(candidate);
            var tokens = Tokenize(identity.Title);
            var duplicate = kept.Any(k => IsSameOffer(k.Identity, k.Tokens, identity, tokens));
            if (!duplicate) kept.Add((candidate, identity, tokens));
        }
        return kept.Select(k => k.Item).ToList();
    }

    private static bool IsSameOffer(PromoDealIdentity a, HashSet<string> aTokens, PromoDealIdentity b, HashSet<string> bTokens)
    {
        if (!string.Equals(NormalizeForKey(a.Merchant), NormalizeForKey(b.Merchant), StringComparison.Ordinal)) return false;
        if (DateOnly.FromDateTime(a.ExpiresAtUtc) != DateOnly.FromDateTime(b.ExpiresAtUtc)) return false;
        // A copy that adds a code is a different offer from the one without it;
        // hiding the code would cost the user money.
        if (!string.Equals(a.PromoCode?.Trim() ?? string.Empty, b.PromoCode?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase)) return false;
        if (aTokens.SetEquals(bTokens)) return true;
        if (aTokens.Count == 0 || bTokens.Count == 0) return false;
        var overlap = aTokens.Intersect(bTokens).Count();
        var union = aTokens.Count + bTokens.Count - overlap;
        return union > 0 && (double)overlap / union >= DuplicateTitleOverlap;
    }

    private static HashSet<string> Tokenize(string title)
        => NormalizeForKey(title)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 1 && t is not ("on" or "of" or "the" or "and" or "for" or "to" or "in" or "at" or "up"))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Human wording for how long a deal has left; hosts format the date part themselves.</summary>
    public static string DescribeTimeLeft(DateTime expiresAtUtc, DateTime nowUtc, bool inferred)
    {
        var remaining = expiresAtUtc - nowUtc;
        string core;
        if (remaining <= TimeSpan.Zero) core = "Ended";
        else if (remaining < TimeSpan.FromHours(24)) core = "Ends today";
        else if (remaining < TimeSpan.FromHours(48)) core = "Ends tomorrow";
        else if (remaining < TimeSpan.FromDays(14)) core = $"{(int)Math.Ceiling(remaining.TotalDays)} days left";
        else core = $"About {(int)Math.Round(remaining.TotalDays / 7)} weeks left";
        return inferred ? core + " (end date guessed)" : core;
    }

    private static string NormalizeForKey(string value)
        => Regex.Replace(value.Replace('–', '-').Replace('—', '-').Trim().ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
}

public readonly record struct PromoExpiration(DateTime ExpiresAtUtc, bool Inferred);

public readonly record struct PromoDealState(string Status);

public readonly record struct PromoDealRankInputs(bool MatchesInterest, double InterestScore, DateTime ExpiresAtUtc, bool PriorityCategory = false);

/// <summary>The fields that identify one offer for cross-message duplicate collapsing.</summary>
public readonly record struct PromoDealIdentity(string Merchant, string Title, string? PromoCode, DateTime ExpiresAtUtc, DateTime ReceivedAtUtc);

public static class PromoDealStatuses
{
    public const string Active = "active";
    public const string Expired = "expired";
}
