using SwarmCore.Agent.Business;

namespace SwarmCore.Agent.Marketing;

public sealed record LeadSearchHit(string Title, string Url);
public sealed record LeadPage(string Url, string Title, string Text, IReadOnlyList<string> ContactRoutes,
    DateTimeOffset RetrievedAtUtc);
public sealed record LeadEvidence(string Phrase, string Quote, string SourceUrl, DateTimeOffset RetrievedAtUtc);
/// <summary>Reference research retains attributed public facts about a separate organization; it is never an outreach candidate.</summary>
public enum LeadResearchUseScope { Outreach, Reference }
public sealed record ResearchedLead(string Domain, string Name, string SourceUrl,
    IReadOnlyList<LeadEvidence> Evidence, IReadOnlyList<string> ContactRoutes, string Query,
    LeadResearchUseScope UseScope = LeadResearchUseScope.Outreach);
public sealed record LeadDiscoveryIssue(string Source, string Reason);
/// <summary>SavedDomains names the businesses this run persisted, so a host can prove exactly which records it created.</summary>
public sealed record LeadDiscoveryResult(int Examined, int Saved, int Duplicates, int NoFit,
    IReadOnlyList<LeadDiscoveryIssue> Issues, IReadOnlyList<string>? SavedDomains = null);

/// <summary>Search phrases and fit phrases come from the owner's objective, never from retrieved pages.</summary>
public sealed class LeadDiscoveryRequest
{
    public string[] Queries { get; set; } = [];
    public string[] RequiredPhrases { get; set; } = [];
    public string[] ExcludedDomains { get; set; } = [];
    public int MaxResultsPerQuery { get; set; } = 5;

    public void Validate()
    {
        if (Queries is null || Queries.Length is < 1 or > 5
            || Queries.Any(q => string.IsNullOrWhiteSpace(q) || q.Length > 300))
            throw new ArgumentException("Supply 1–5 public business search queries, each at most 300 characters.");
        if (RequiredPhrases is null || RequiredPhrases.Length is < 1 or > 8
            || RequiredPhrases.Any(p => string.IsNullOrWhiteSpace(p) || p.Trim().Length is < 3 or > 100))
            throw new ArgumentException("Supply 1–8 fit phrases (3–100 characters). Every phrase must occur on the fetched page.");
        if (ExcludedDomains is null || ExcludedDomains.Length > 100
            || ExcludedDomains.Any(d => string.IsNullOrWhiteSpace(d) || d.Length > 253))
            throw new ArgumentException("Supply at most 100 excluded domains.");
        if (MaxResultsPerQuery is < 1 or > 10)
            throw new ArgumentException("Choose 1–10 search results per query.");
    }
}

/// <summary>One host-reviewed exact page, no search. The reviewed closure and use scope are host data, never model-chosen.</summary>
public sealed class LeadExactResearchRequest
{
    public string Url { get; set; } = "";
    public string[] RequiredPhrases { get; set; } = [];
    public LeadResearchUseScope UseScope { get; set; } = LeadResearchUseScope.Outreach;
    /// <summary>Bounded operator reason retained as the discovery query; it is not source text.</summary>
    public string Reason { get; set; } = "";

    public void Validate()
    {
        if (!LeadResearchUrlPolicy.TryGetBusinessUri(Url, out _))
            throw new ArgumentException("Supply one exact public business page URL.");
        if (RequiredPhrases is null || RequiredPhrases.Length is < 1 or > 8
            || RequiredPhrases.Any(p => string.IsNullOrWhiteSpace(p) || p.Trim().Length is < 3 or > 100))
            throw new ArgumentException("Supply 1-8 literal phrases (3-100 characters). Every phrase must occur on the fetched page.");
        if (!Enum.IsDefined(UseScope)) throw new ArgumentException("Choose a defined research use scope.");
        if (string.IsNullOrWhiteSpace(Reason) || Reason.Length > 300 || Reason.Any(char.IsControl))
            throw new ArgumentException("Supply a bounded research reason of at most 300 characters.");
    }
}

public enum LeadExactResearchOutcome { Saved, Duplicate, NoFit, Unavailable, IdentityChanged }
public sealed record LeadExactResearchResult(LeadExactResearchOutcome Outcome, string Domain, string? ReturnedUrl,
    DateTimeOffset? RetrievedAtUtc, string? NormalizedTextSha256, int EvidenceCount, int RouteCount, string? Issue);

public interface ILeadDiscoveryWeb
{
    Task<IReadOnlyList<LeadSearchHit>> SearchAsync(string query, int count, CancellationToken ct);
    // Must return bounded document text with non-content nodes removed; search snippets are not fetched evidence.
    Task<LeadPage> ReadAsync(string url, CancellationToken ct);
}

public interface ILeadProspectSink
{
    Task<bool> ContainsDomainAsync(string domain, CancellationToken ct);
    // False means an existing prospect won the tenant/domain uniqueness race; never overwrite it.
    Task<bool> SaveAsync(ResearchedLead lead, CancellationToken ct);
}

/// <summary>
/// Deterministic research tool for a durable agent turn. No generated facts, email guessing,
/// model calls, sending, subscriptions, or implicit buying intent. Each candidate is checkpointed
/// to the host sink before advancing, so replay can skip already saved businesses.
/// </summary>
public sealed class LeadDiscovery(ILeadDiscoveryWeb web, ILeadProspectSink sink)
{
    public const string ContactRouteEvidencePhrase = "Observed published contact route";
    public async Task<LeadDiscoveryResult> RunAsync(LeadDiscoveryRequest request, CancellationToken ct = default)
    {
        request.Validate();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<LeadDiscoveryIssue>();
        var savedDomains = new List<string>();
        var examined = 0; var saved = 0; var duplicates = 0; var noFit = 0;
        foreach (var query in request.Queries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<LeadSearchHit> hits;
            try { hits = await web.SearchAsync(query.Trim(), request.MaxResultsPerQuery, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { issues.Add(new(query, "Search timed out; retry this query.")); continue; }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { issues.Add(new(query, "Search unavailable; retry this query.")); continue; }
            if (hits.Count == 0) issues.Add(new(query, "No results returned; broaden the query or check the search connection."));
            foreach (var hit in hits.Take(request.MaxResultsPerQuery))
            {
                ct.ThrowIfCancellationRequested();
                if (!LeadResearchUrlPolicy.TryGetBusinessUri(hit.Url, out var uri)) continue;
                var domain = LeadResearchUrlPolicy.Domain(uri!);
                if (request.ExcludedDomains.Any(d => LeadResearchUrlPolicy.IsDomain(domain, d.Trim()))) continue;
                if (!seen.Add(uri!.AbsoluteUri)) { duplicates++; continue; }
                if (await sink.ContainsDomainAsync(domain, ct).ConfigureAwait(false)) { duplicates++; continue; }
                examined++;
                LeadPage page;
                try { page = await web.ReadAsync(uri.AbsoluteUri, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { issues.Add(new(domain, "Page timed out; no evidence recorded.")); continue; }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { issues.Add(new(uri.GetLeftPart(UriPartial.Path), "Page unavailable or refused; no evidence recorded.")); continue; }
                if (!LeadResearchUrlPolicy.TryGetBusinessUri(page.Url, out var finalUri)
                    || LeadResearchUrlPolicy.Domain(finalUri!) != domain)
                { issues.Add(new(domain, "The page changed business identity; research the destination separately.")); continue; }
                if (BuildLead(page, domain, request.RequiredPhrases, query, LeadResearchUseScope.Outreach) is not { } lead)
                { noFit++; continue; }
                if (await sink.SaveAsync(lead, ct).ConfigureAwait(false)) { saved++; savedDomains.Add(domain); } else duplicates++;
            }
        }
        return new(examined, saved, duplicates, noFit, issues, savedDomains);
    }

    /// <summary>
    /// Reads exactly one host-reviewed page without any search, applying the same literal fit,
    /// exact quote, route-publication and domain-identity rules as discovery. A saved domain is
    /// not refetched, so replay repeats no network effect. No model call, guessing or sending.
    /// </summary>
    public async Task<LeadExactResearchResult> ResearchExactAsync(LeadExactResearchRequest request, CancellationToken ct = default)
    {
        request.Validate();
        LeadResearchUrlPolicy.TryGetBusinessUri(request.Url, out var uri);
        var domain = LeadResearchUrlPolicy.Domain(uri!);
        if (await sink.ContainsDomainAsync(domain, ct).ConfigureAwait(false))
            return new(LeadExactResearchOutcome.Duplicate, domain, null, null, null, 0, 0, "This business domain already has saved research; the existing record was retained without a new fetch.");
        LeadPage page;
        try { page = await web.ReadAsync(uri!.AbsoluteUri, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return new(LeadExactResearchOutcome.Unavailable, domain, null, null, null, 0, 0, "Page timed out; no evidence recorded."); }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        { return new(LeadExactResearchOutcome.Unavailable, domain, null, null, null, 0, 0, "Page unavailable or refused; no evidence recorded."); }
        if (!LeadResearchUrlPolicy.TryGetBusinessUri(page.Url, out var finalUri) || LeadResearchUrlPolicy.Domain(finalUri!) != domain)
            return new(LeadExactResearchOutcome.IdentityChanged, domain, page.Url, page.RetrievedAtUtc, null, 0, 0, "The page changed business identity; research the destination separately.");
        var textHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Normalize(page.Text))));
        if (BuildLead(page, domain, request.RequiredPhrases, request.Reason.Trim(), request.UseScope) is not { } lead)
            return new(LeadExactResearchOutcome.NoFit, domain, page.Url, page.RetrievedAtUtc, textHash, 0, 0, "At least one required phrase is absent from the fetched page; nothing was saved.");
        var saved = await sink.SaveAsync(lead, ct).ConfigureAwait(false);
        return new(saved ? LeadExactResearchOutcome.Saved : LeadExactResearchOutcome.Duplicate, domain, page.Url, page.RetrievedAtUtc, textHash,
            lead.Evidence.Count, lead.ContactRoutes.Count, saved ? null : "A competing save retained the existing record; nothing was overwritten.");
    }

    private static ResearchedLead? BuildLead(LeadPage page, string domain, string[] requiredPhrases, string query, LeadResearchUseScope useScope)
    {
        var text = Normalize(page.Text);
        var evidence = new List<LeadEvidence>();
        foreach (var phrase in requiredPhrases.Select(p => Normalize(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var offset = text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
            if (offset < 0) break;
            var start = Math.Max(0, offset - 90);
            var quote = text.Substring(start, Math.Min(text.Length - start, phrase.Length + 180));
            evidence.Add(new(phrase, quote, page.Url, page.RetrievedAtUtc));
        }
        if (evidence.Count != requiredPhrases.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).Count()) return null;
        var routes = page.ContactRoutes.Take(10).ToArray();
        var publications = BusinessProspectingPolicy.Routes(routes, page.Url, page.RetrievedAtUtc);
        // Publication is a separate observation, never a fit phrase or contact permission.
        // Keep the exact fetched page and time with each route before host persistence.
        evidence.AddRange(publications.Select(route => new LeadEvidence(ContactRouteEvidencePhrase, route.Evidence.Quote, page.Url, page.RetrievedAtUtc)));
        return new ResearchedLead(domain, string.IsNullOrWhiteSpace(page.Title) ? domain : page.Title[..Math.Min(page.Title.Length, 300)],
            page.Url, evidence, routes, query, useScope);
    }

    public static string Normalize(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>Business-page discovery, not logged-in social scraping. Network adapters also enforce public IPs.</summary>
public static class LeadResearchUrlPolicy
{
    private static readonly string[] Social = ["x.com", "twitter.com", "linkedin.com", "reddit.com", "facebook.com", "instagram.com"];
    public static bool TryGetBusinessUri(string? value, out Uri? uri)
    {
        if (value is null || value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme is not ("http" or "https")
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.HostNameType != UriHostNameType.Dns
            || !uri.Host.Contains('.') || uri.Host.EndsWith('.') || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || IsSocial(uri.IdnHost)) { uri = null; return false; }
        return true;
    }
    private static bool IsSocial(string host) => Social.Any(d => IsDomain(host, d));
    public static string Domain(Uri uri) => uri.IdnHost.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
        ? uri.IdnHost[4..].ToLowerInvariant() : uri.IdnHost.ToLowerInvariant();
    public static bool IsDomain(string host, string domain) => host.Equals(domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
}
