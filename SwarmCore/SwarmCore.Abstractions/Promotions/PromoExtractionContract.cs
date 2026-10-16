using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SwarmCore.Abstractions.Promotions;

/// <summary>
/// The JSON shape a model returns when reading one promotional message.
/// Everything here is a proposal; <see cref="PromoExtractionValidator"/>
/// turns it into bounded, source-grounded deals before a host persists it.
/// </summary>
public sealed class PromoExtractionResponse
{
    [JsonPropertyName("isPromotional")] public bool IsPromotional { get; set; }
    [JsonPropertyName("merchant")] public string? Merchant { get; set; }
    [JsonPropertyName("deals")] public List<PromoExtractedDeal> Deals { get; set; } = [];
}

public sealed class PromoExtractedDeal
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("discountPercent")] public double? DiscountPercent { get; set; }
    [JsonPropertyName("discountAmount")] public double? DiscountAmount { get; set; }
    [JsonPropertyName("minimumPurchase")] public double? MinimumPurchase { get; set; }
    [JsonPropertyName("restrictions")] public string? Restrictions { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("expiresOn")] public string? ExpiresOn { get; set; }
    [JsonPropertyName("matchedInterests")] public List<string>? MatchedInterests { get; set; }
    [JsonPropertyName("relevance")] public double? Relevance { get; set; }
}

/// <summary>A validated deal ready for persistence. Null fields were absent or rejected.</summary>
public sealed record PromoValidatedDeal(
    string Title,
    string? Description,
    string? PromoCode,
    decimal? DiscountPercent,
    decimal? DiscountAmount,
    decimal? MinimumPurchase,
    string? Restrictions,
    string? Url,
    string Category,
    PromoExpiration Expiration,
    IReadOnlyList<string> MatchedInterests,
    double InterestScore);

/// <summary>
/// Deterministic guard between the model and the store. It bounds text,
/// rejects links that do not appear in the message, keeps only interests the
/// user actually has, and clamps numbers so a hallucinated field cannot reach
/// the Today page.
/// </summary>
public static class PromoExtractionValidator
{
    public const int MaxDealsPerMessage = 6;
    public const int MaxTitleLength = 120;
    public const int MaxDescriptionLength = 400;
    public const int MaxCodeLength = 40;
    public const int MaxRestrictionsLength = 300;

    public static IReadOnlyList<PromoValidatedDeal> Validate(
        PromoExtractionResponse? response,
        DateTime receivedUtc,
        IReadOnlyCollection<string> messageLinks,
        IReadOnlyCollection<string> knownInterests,
        TimeZoneInfo? localZone = null)
    {
        var merchant = response?.Merchant;
        if (response is null || !response.IsPromotional || response.Deals.Count == 0)
            return [];

        var linkSet = new HashSet<string>(messageLinks.Select(NormalizeLink), StringComparer.OrdinalIgnoreCase);
        var interestSet = new HashSet<string>(knownInterests.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()), StringComparer.OrdinalIgnoreCase);
        var results = new List<PromoValidatedDeal>();
        var seenTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var deal in response.Deals)
        {
            var title = Bound(deal.Title, MaxTitleLength);
            if (title is null) continue;
            if (!seenTitles.Add(title + "|" + (Bound(deal.Code, MaxCodeLength) ?? string.Empty))) continue;

            var matched = (deal.MatchedInterests ?? [])
                .Select(m => m?.Trim() ?? string.Empty)
                .Where(m => m.Length > 0 && interestSet.Contains(m))
                .Select(m => interestSet.First(k => string.Equals(k, m, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var relevance = deal.Relevance is double r && !double.IsNaN(r) ? Math.Clamp(r, 0, 1) : 0;
            // A claimed match with no supporting interest is a hallucination;
            // relevance without a match is at most a weak signal.
            if (matched.Count == 0) relevance = Math.Min(relevance, 0.49);

            var url = deal.Url is { } candidate && linkSet.Contains(NormalizeLink(candidate))
                ? candidate.Trim()
                : null;
            var description = Bound(deal.Description, MaxDescriptionLength);
            var category = PromoDealCategories.Normalize(deal.Category)
                           ?? PromoDealCategories.Classify(merchant, title, description);

            results.Add(new PromoValidatedDeal(
                title,
                description,
                Bound(deal.Code, MaxCodeLength),
                ClampMoney(deal.DiscountPercent, max: 100),
                ClampMoney(deal.DiscountAmount, max: 100_000),
                ClampMoney(deal.MinimumPurchase, max: 1_000_000),
                Bound(deal.Restrictions, MaxRestrictionsLength),
                url,
                category,
                PromoDealLifecycle.ResolveExpiration(deal.ExpiresOn, receivedUtc, localZone),
                matched,
                relevance));

            if (results.Count >= MaxDealsPerMessage) break;
        }

        return results;
    }

    /// <summary>Short, human discount label derived from the validated numbers.</summary>
    public static string? DescribeDiscount(decimal? percent, decimal? amount, decimal? minimumPurchase)
    {
        string? core = percent is > 0 ? $"{percent.Value:0.#}% off"
            : amount is > 0 ? $"${amount.Value:0.##} off"
            : null;
        if (core is null) return null;
        return minimumPurchase is > 0 ? $"{core} orders over ${minimumPurchase.Value:0.##}" : core;
    }

    private static string? Bound(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var collapsed = Regex.Replace(value.Trim(), @"\s+", " ");
        return collapsed.Length <= max ? collapsed : collapsed[..max].TrimEnd() + "…";
    }

    private static decimal? ClampMoney(double? value, double max)
    {
        if (value is not double v || double.IsNaN(v) || double.IsInfinity(v) || v <= 0) return null;
        return (decimal)Math.Round(Math.Min(v, max), 2);
    }

    private static string NormalizeLink(string link)
        => link.Trim().TrimEnd('/');
}
