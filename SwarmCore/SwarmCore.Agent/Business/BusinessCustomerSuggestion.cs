using SwarmCore.Agent.Marketing;

namespace SwarmCore.Agent.Business;

/// <summary>Optional AI extracts source-supported draft fields. This DTO has no authority, claims, terms or account fields.</summary>
public sealed record BusinessCustomerSuggestion(string Offer,string Audience,string SearchQuery,string[] FitPhrases,
    string OfferSourceId,string OfferQuote,string AudienceSourceId,string AudienceQuote)
{
    public static bool Valid(BusinessCustomerSuggestion? value,IReadOnlyList<BusinessCustomerPlanSource> sources)
    {
        if(value is null||value.Offer is null||value.Audience is null||value.SearchQuery is null||value.FitPhrases is null||value.Offer.Length>4000||value.Audience.Length>2000)return false;
        bool Grounded(string text,string id,string quote)=>!string.IsNullOrWhiteSpace(text)&&!string.IsNullOrWhiteSpace(quote)
            &&quote.Length<=4000&&quote.Contains(text,StringComparison.Ordinal)
            &&sources.Any(s=>s.Id==id&&s.Quote?.Contains(quote,StringComparison.Ordinal)==true);
        if(!Grounded(value.Offer,value.OfferSourceId,value.OfferQuote)||!Grounded(value.Audience,value.AudienceSourceId,value.AudienceQuote))return false;
        try{new LeadDiscoveryRequest{Queries=[value.SearchQuery],RequiredPhrases=value.FitPhrases,MaxResultsPerQuery=3}.Validate();}catch(ArgumentException){return false;}
        return value.FitPhrases.All(p=>value.Audience.Contains(p,StringComparison.OrdinalIgnoreCase)||value.SearchQuery.Contains(p,StringComparison.OrdinalIgnoreCase));
    }
}
