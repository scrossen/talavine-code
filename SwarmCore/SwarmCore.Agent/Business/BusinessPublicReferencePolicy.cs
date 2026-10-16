using System.Text.Json;
using SwarmCore.Agent.Marketing;

namespace SwarmCore.Agent.Business;

/// <summary>
/// Links retained public research about a separate organization to a customer's case as
/// attributed reference evidence. The reference keeps its own source URL, quotes and retrieval
/// times. It never becomes customer identity, a qualification criterion match, a contact route,
/// a supplier agreement, consent or outreach authorization.
/// </summary>
public static class BusinessPublicReferencePolicy
{
    public const string SourceIdentityPrefix = "public-reference:";
    public const string QuoteFactPrefix = "Attributed public reference quote: ";
    public const string ObservationFactPrefix = "Public reference observation: ";
    public const string Relationship = "unverified";
    public const string Interpretation = "Published public source mentions attributed to a separate organization; "
        + "not customer identity, supplier agreement, endorsement, consent or outreach authorization.";
    public const int MaximumQuotes = 8;

    public static bool IsReference(BusinessEvidenceFact fact)
        => fact.SourceIdentity.StartsWith(SourceIdentityPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Builds the case evidence from a saved research record. Published contact routes stay with
    /// the research record; they are counted but never copied into another organization's case.
    /// </summary>
    public static BusinessEvidenceFact ReferenceEvidence(Guid prospectId, string domain, string displayName,
        string sourceUrl, IReadOnlyList<LeadEvidence> evidence, DateTimeOffset now)
    {
        if (prospectId == Guid.Empty || string.IsNullOrWhiteSpace(domain) || domain.Length > 253
            || !LeadResearchUrlPolicy.TryGetBusinessUri(sourceUrl, out var source) || LeadResearchUrlPolicy.Domain(source!) != domain)
            throw new ArgumentException("A public reference needs its saved research identity and a source page on the same business domain.");
        if (evidence is null || evidence.Count == 0 || evidence.Any(x => x is null))
            throw new ArgumentException("A public reference needs retained attributed evidence.");
        var quotes = evidence.Where(x => x.Phrase != LeadDiscovery.ContactRouteEvidencePhrase).ToArray();
        var routes = evidence.Count - quotes.Length;
        if (quotes.Length is 0 or > MaximumQuotes) throw new ArgumentException("A public reference needs one to eight retained quotes.");
        foreach (var quote in quotes)
        {
            if (string.IsNullOrWhiteSpace(quote.Phrase) || string.IsNullOrWhiteSpace(quote.Quote) || quote.Quote.Length > 4000
                || quote.RetrievedAtUtc == default || quote.RetrievedAtUtc.Offset != TimeSpan.Zero || quote.RetrievedAtUtc > now
                || !LeadResearchUrlPolicy.TryGetBusinessUri(quote.SourceUrl, out var quoted) || LeadResearchUrlPolicy.Domain(quoted!) != domain)
                throw new ArgumentException("Each retained quote needs its literal text, same-domain source page and actual UTC retrieval time.");
        }
        var observed = quotes.Max(x => x.RetrievedAtUtc);
        string[] facts =
        [
            ..quotes.Select(x => QuoteFactPrefix + JsonSerializer.Serialize(x)),
            ObservationFactPrefix + JsonSerializer.Serialize(new
            {
                Organization = displayName[..Math.Min(displayName.Length, 300)], Domain = domain, ProspectId = prospectId,
                Relationship, QuoteCount = quotes.Length, PublishedRouteCount = routes, Interpretation,
            }),
        ];
        return new(SourceIdentityPrefix + domain + ":" + prospectId.ToString("N"), sourceUrl, observed, facts);
    }
}
