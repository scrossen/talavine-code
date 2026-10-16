using System.Text.Json;
using SwarmCore.Agent.Marketing;

namespace SwarmCore.Agent.Business;

public sealed record BusinessResearchObservation(BusinessEvidenceFact Evidence, string[] MatchedCriteria,
    bool ContactRouteVerified);

/// <summary>Extracts literal configured phrases from sanitized fetched body text. A phrase occurrence
/// is attributed source text, never verified need, consent, intent, or a customer confirmation.</summary>
public static class BusinessResearchObservationRules
{
    public static BusinessResearchObservation Observe(BusinessBriefDefinition brief, LeadPage page,
        string expectedDomain, string contactEmail, DateTimeOffset now)
    {
        brief.Validate();
        if (!LeadResearchUrlPolicy.TryGetBusinessUri(page.Url, out var uri)
            || LeadResearchUrlPolicy.Domain(uri!) != expectedDomain || page.Text is null || page.Text.Length > 100000
            || page.RetrievedAtUtc > now || page.RetrievedAtUtc < now.AddMinutes(-5))
            throw new ArgumentException("The fetched page must retain the business identity and an actual current retrieval time.");
        var route = "mailto:" + contactEmail;
        var verified = page.ContactRoutes.Any(x => string.Equals(x, route, StringComparison.OrdinalIgnoreCase));
        var text = LeadDiscovery.Normalize(page.Text);
        var quotes = new List<LeadEvidence>();
        foreach (var criterion in brief.QualificationCriteria.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var phrase = LeadDiscovery.Normalize(criterion);
            var offset = FindPhrase(text, phrase);
            if (offset < 0) continue;
            var start = Math.Max(0, offset - 90);
            quotes.Add(new(criterion, text.Substring(start, Math.Min(text.Length - start, phrase.Length + 180)), page.Url, page.RetrievedAtUtc));
        }
        // A changed page with no matches still supersedes the old observation. Preserve quotes even
        // when route verification fails, but never turn them into qualifying facts in that case.
        var matched = verified ? quotes.Select(x => x.Phrase).ToArray() : [];
        string[] facts = [..matched, ..quotes.Select(x => "Attributed public source quote: " + JsonSerializer.Serialize(x)), "Public research observation: " + JsonSerializer.Serialize(new
        { Title = page.Title[..Math.Min(page.Title.Length, 300)], ContactRoute = verified ? route : null, ContactRouteVerified = verified,
            Interpretation = "Literal public source mentions only; no buyer intent, contact permission or customer confirmation inferred." })];
        return new(new("public-research:" + expectedDomain, page.Url, page.RetrievedAtUtc, facts,
            Conflicting: !verified, CustomerConfirmed: false), matched, verified);
    }
    private static int FindPhrase(string text, string phrase)
    {
        var offset = 0;
        while (offset < text.Length)
        {
            var found = text.IndexOf(phrase, offset, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return -1;
            var end = found + phrase.Length;
            if ((found == 0 || !char.IsLetterOrDigit(text[found - 1]) || !char.IsLetterOrDigit(phrase[0]))
                && (end == text.Length || !char.IsLetterOrDigit(text[end]) || !char.IsLetterOrDigit(phrase[^1]))) return found;
            offset = found + 1;
        }
        return -1;
    }
}
