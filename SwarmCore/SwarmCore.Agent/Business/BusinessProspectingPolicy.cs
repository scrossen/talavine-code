using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SwarmCore.Agent.Marketing;

namespace SwarmCore.Agent.Business;

public static class BusinessProspectingPolicy
{
    public const int MaximumAudiences=4;
    public const int CustomerFactQuoteLimit=240;
    public const int CustomerOfferQuoteLimit=360;
    public const string AudienceEvidenceRequiredMessage="The public source does not identify a supported customer role or business type. Enter your intended customers before confirming research; no audience was guessed.";
    // Generic role vocabulary recognizes only words already present in the source. It is
    // not a catalog of suggested markets, and unknown roles stay an owner decision.
    private static readonly HashSet<string> AudienceHeads=new(StringComparer.OrdinalIgnoreCase)
    {
        "consultant","consultants","contractor","contractors","freelancer","freelancers","professional","professionals",
        "owner","owners","founder","founders","entrepreneur","entrepreneurs","manager","managers","director","directors",
        "developer","developers","engineer","engineers","researcher","researchers","designer","designers","creator","creators",
        "teacher","teachers","educator","educators","student","students","coach","coaches","accountant","accountants",
        "lawyer","lawyers","attorney","attorneys","physician","physicians","nurse","nurses","dentist","dentists",
        "veterinarian","veterinarians","therapist","therapists","architect","architects","realtor","realtors",
        "technician","technicians","worker","workers","producer","producers","artist","artists","retailer","retailers",
        "team","teams","agency","agencies","firm","firms","clinic","clinics","practice","practices","shop","shops",
        "school","schools","hospital","hospitals","restaurant","restaurants","nonprofit","nonprofits",
        "business","businesses","organization","organizations","cooperative","cooperatives","manufacturer","manufacturers"
    };
    private static readonly HashSet<string> AudienceBoundaries=new(StringComparer.OrdinalIgnoreCase)
    {
        "a","an","the","your","our","their","his","her","its","my","this","that","these","those",
        "we","you","they","it","who","which","what","when","where","how","why","all","any","every",
        "for","by","to","from","with","without","in","on","at","of","and","or","but","as",
        "is","are","was","were","be","been","being","can","could","will","would","should","must",
        "serve","serves","serving","help","helps","helping","support","supports","supporting",
        "provide","provides","providing","deliver","delivers","delivering","build","built","designed",
        "use","uses","used","make","makes","run","runs","need","needs","want","wants","seeking",
        "choose","find","search","discover","reach","meet","hire","contact","connect","ask","asked","learn",
        "start","prepare","review","manage","managing","track","evaluate","get","keep","keeps","keeping","kept","see","visit","open",
        "save","browse","explore","anyone","someone","everyone"
    };
    private static readonly HashSet<string> NonAudienceModifiers=new(StringComparer.OrdinalIgnoreCase)
    {
        "ai","artificial","automated","virtual","software","model","models","password","passwords","credential",
        "file","files","document","documents","package","packages","download","downloads","browser","browsers",
        "storage","memory","data","database","databases","prompt","prompts","task","tasks","job","jobs"
    };
    private static readonly HashSet<string> AudienceHeadModifiers=new(StringComparer.OrdinalIgnoreCase)
    {"nonprofit","clinic","school","hospital","restaurant","shop","agency","business","organization"};
    private static readonly HashSet<string> GeneralAudienceHeads=new(StringComparer.OrdinalIgnoreCase)
    {"team","teams","business","businesses","organization","organizations"};
    private static readonly HashSet<string> GeneralAudienceModifiers=new(StringComparer.OrdinalIgnoreCase)
    {"shared","internal","own","connected","whole","all","other","existing","current","new","every","any","some","many","entire","general","better","right"};
    public static BusinessObservedSummary Summary(IReadOnlyList<BusinessCustomerPlanSource> sources,DateTimeOffset now)
    {
        var source=sources.FirstOrDefault(x=>x.Id=="website"&&!string.IsNullOrWhiteSpace(x.Quote))
            ??sources.FirstOrDefault(x=>x.Id=="brief"&&!string.IsNullOrWhiteSpace(x.Quote))
            ??sources.FirstOrDefault(x=>!string.IsNullOrWhiteSpace(x.Quote));
        return source is null?new("Business information is not yet available.",[]):new(Excerpt(source.Quote!,1200),[Evidence(source,Excerpt(source.Quote!,1200),now)]);
    }
    public static BusinessAudienceHypothesis[] Audiences(BusinessCustomerDraft draft,IReadOnlyList<BusinessCustomerPlanSource> sources,DateTimeOffset now)
    {
        var results=new List<BusinessAudienceHypothesis>();
        if(!string.IsNullOrWhiteSpace(draft.Audience)&&draft.Audience.Length<=100)
        {
            var source=sources.FirstOrDefault(x=>x.Quote?.Contains(draft.Audience,StringComparison.Ordinal)==true);
            if(source is not null)results.Add(Hypothesis(draft.Audience,draft.Audience,draft.FitPhrases.Length>0?draft.FitPhrases:[draft.Audience],source,draft.Audience,now));
        }
        foreach(var source in sources.Where(x=>x.Id=="website"&&x.Quote is not null))
        {
            foreach(var audience in ObservedAudienceSpans(source.Quote!))
            {
                if(audience.Length is <3 or >100||results.Any(x=>x.Label.Equals(audience,StringComparison.OrdinalIgnoreCase)))continue;
                results.Add(Hypothesis(audience,audience,[audience],source,audience,now));if(results.Count>=MaximumAudiences)break;
            }
            if(results.Count>=MaximumAudiences)break;
        }
        return results.Take(MaximumAudiences).ToArray();
    }
    private static IEnumerable<string> ObservedAudienceSpans(string text)
    {
        var words=Regex.Matches(text,@"\b[\p{L}][\p{L}\p{N}]*(?:[-'][\p{L}][\p{L}\p{N}]*)*\b").Cast<Match>().ToArray();
        for(var index=0;index<words.Length;index++)
        {
            if(!AudienceHeads.Contains(words[index].Value))continue;
            if(AudienceHeadModifiers.Contains(words[index].Value)&&index+1<words.Length&&AudienceHeads.Contains(words[index+1].Value)
                &&PhraseGap(text.AsSpan(words[index].Index+words[index].Length,words[index+1].Index-words[index].Index-words[index].Length)))continue;
            // A preceding role belongs to its own coordinated phrase. Sentence/list
            // punctuation is a hard boundary, so unrelated prose cannot become a label.
            var start=index;
            while(start>0&&index-start<5)
            {
                var previous=words[start-1];var gap=text.AsSpan(previous.Index+previous.Length,words[start].Index-previous.Index-previous.Length);
                if(AudienceBoundaries.Contains(previous.Value)||AudienceHeads.Contains(previous.Value)&&!AudienceHeadModifiers.Contains(previous.Value)||!PhraseGap(gap))break;
                start--;
            }
            // A bare or purely operational grouping is not a useful customer segment.
            // Retain only the exact specific modifier span, without inventing a market.
            if(GeneralAudienceHeads.Contains(words[index].Value))
            {
                while(start<index&&GeneralAudienceModifiers.Contains(words[start].Value))start++;
                if(start==index||words.Skip(start).Take(index-start).All(x=>GeneralAudienceModifiers.Contains(x.Value)))continue;
            }
            if(words[index].Value.ToLowerInvariant() is "manager" or "managers" or "worker" or "workers"
                &&words.Skip(start).Take(index-start).Any(x=>NonAudienceModifiers.Contains(x.Value)))continue;
            var first=words[start];var last=words[index];
            var value=text.Substring(first.Index,last.Index+last.Length-first.Index);
            if(value.Length is >=3 and <=100)yield return value;
        }
    }
    private static bool PhraseGap(ReadOnlySpan<char> gap)=>!gap.IsEmpty&&gap.IndexOfAny('\r','\n')<0&&gap.ToString().All(char.IsWhiteSpace);
    private static BusinessAudienceHypothesis Hypothesis(string label,string query,string[] fit,BusinessCustomerPlanSource source,string quote,DateTimeOffset now)
        =>new(Id(label+"|"+query),label,"Explore whether the reviewed offer is relevant to this audience.","This is a targeting hypothesis; need and buying intent have not been verified.",query,fit,[Evidence(source,quote,now)]);
    public static BusinessProspectingWorkspace? ValidateSuggestion(BusinessProspectingSuggestion? suggestion,IReadOnlyList<BusinessCustomerPlanSource> sources,DateTimeOffset now)
    {
        if(suggestion is null||suggestion.Audiences is null||suggestion.Audiences.Length is <1 or >MaximumAudiences)return null;
        var summarySource=Source(sources,suggestion.SummarySourceId,suggestion.SummaryQuote,1200);if(summarySource is null)return null;
        var audiences=new List<BusinessAudienceHypothesis>();
        foreach(var option in suggestion.Audiences)
        {
            if(option is null||!Bounded(option.Label,200)||!Bounded(option.UseCase,600)||!Bounded(option.PainHypothesis,600))return null;
            var source=Source(sources,option.SourceId,option.Quote,1000);if(source is null)return null;
            try{new LeadDiscoveryRequest{Queries=[option.SearchQuery],RequiredPhrases=option.FitPhrases,MaxResultsPerQuery=3}.Validate();}catch(ArgumentException){return null;}
            audiences.Add(new(Id(option.Label+"|"+option.SearchQuery),option.Label,option.UseCase,option.PainHypothesis,option.SearchQuery.Trim(),option.FitPhrases.Select(x=>x.Trim()).ToArray(),[Evidence(source,option.Quote,now)]));
        }
        if(audiences.Select(x=>x.Id).Distinct().Count()!=audiences.Count)return null;
        return new(new(suggestion.SummaryQuote,[Evidence(summarySource,suggestion.SummaryQuote,now)]),audiences.ToArray(),StatusMessage:"Suggested audiences are hypotheses to review, not qualified demand.");
    }
    public static BusinessProspectContactRoute[] Routes(IEnumerable<string> routes,string sourceUrl,DateTimeOffset observed)
    {
        if(!LeadResearchUrlPolicy.TryGetBusinessUri(sourceUrl,out var source))return [];
        var result=new List<BusinessProspectContactRoute>();
        foreach(var route in routes.Take(10))
        {
            if(string.IsNullOrWhiteSpace(route)||route.Length>2048||route.Any(char.IsControl))continue;
            if(route.StartsWith("mailto:",StringComparison.OrdinalIgnoreCase))
            {
                var email=route[7..];if(!MailAddress.TryCreate(email,out var address)||address.Address!=email)continue;
                result.Add(new(address.Address,BusinessProspectRouteKind.PublishedEmail,new("published-contact",sourceUrl,route,observed)));
            }
            else if(LeadResearchUrlPolicy.TryGetBusinessUri(route,out var url)&&LeadResearchUrlPolicy.Domain(url!)==LeadResearchUrlPolicy.Domain(source!))result.Add(new(route,BusinessProspectRouteKind.PublishedPage,new("published-contact",sourceUrl,route,observed)));
        }
        return result.DistinctBy(x=>x.Value,StringComparer.OrdinalIgnoreCase).ToArray();
    }
    /// <summary>Only retained publication evidence for this exact route and business can support a cited route.
    /// Missing legacy provenance is not reconstructed from a different fact or row creation time.</summary>
    public static BusinessProspectContactRoute[] CitedRoutes(IEnumerable<string> routes,IReadOnlyList<LeadEvidence> evidence,string expectedDomain)
        => routes.SelectMany(route=>
        {
            var publication=evidence.FirstOrDefault(x=>x is not null&&x.Phrase==LeadDiscovery.ContactRouteEvidencePhrase&&x.Quote==route
                &&x.RetrievedAtUtc!=default&&x.RetrievedAtUtc.Offset==TimeSpan.Zero
                &&LeadResearchUrlPolicy.TryGetBusinessUri(x.SourceUrl,out var source)&&LeadResearchUrlPolicy.Domain(source!)==expectedDomain);
            return publication is null?[]:Routes([route],publication.SourceUrl,publication.RetrievedAtUtc);
        }).ToArray();

    public static BusinessProspectDecisionMaker[] People(string text,string url,DateTimeOffset observed)
    {
        var result=new List<BusinessProspectDecisionMaker>();
        foreach(Match match in Regex.Matches(text,@"\b(?<name>[A-Z][a-z]{1,30}(?: [A-Z][a-z]{1,30}){1,3})\s*[,–-]\s*(?<role>(?:CEO|Founder|Co-Founder|Owner|President|Partner|Director|Manager|Chief [A-Za-z ]{1,40}))\b"))
        {result.Add(new(match.Groups["name"].Value,match.Groups["role"].Value,null,[new("published-person",url,match.Value,observed)]));if(result.Count==3)break;}
        return result.ToArray();
    }
    public static BusinessProspectDraft Draft(Guid prospectId,string company,BusinessProspectingEvidence[] facts,BusinessBriefDefinition brief,DateTimeOffset now,BusinessProspectingDraftSuggestion? suggestion=null)
    {
        var fact=facts.FirstOrDefault();var quote=fact?.Quote??"";var offer=brief.Offer;
        // A substring can erase a negation, eligibility condition or price qualifier.
        // Optional selection must preserve each complete original passage exactly.
        if(suggestion is not null&&fact is not null&&suggestion.FactSourceId==fact.SourceId
            &&suggestion.FactQuote==fact.Quote&&suggestion.OfferQuote==brief.Offer)
        {quote=suggestion.FactQuote;offer=suggestion.OfferQuote;}
        // Same default customer tone as AgentCultureCharter: warm, professional and
        // brief. Sources remain attributed quotations, not instructions or promises.
        // Retain full evidence separately; never send a whole retrieved page as prose.
        var opening=fact is {IsCompletePassage:true}&&CompleteCustomerQuote(quote,CustomerFactQuoteLimit)
            ?"I noticed this on your website: “"+quote+"”"+QuoteSentenceEnding(quote):"I'm reaching out to see whether a conversation could be useful.";
        var offered=CompleteCustomerQuote(offer,CustomerOfferQuoteLimit)?"\n\nWe offer: “"+offer+"”"+QuoteSentenceEnding(offer):"";
        return new(prospectId,1,"A question for "+Excerpt(company,100),"Hello,\n\n"+opening+offered+"\n\nWould it be useful to discuss whether this fits your business?",fact is null?[]:[fact],now);
    }
    public static void ValidateDraftText(string subject,string body)
    {if(!Bounded(subject,200)||!Bounded(body,16000)||subject.Any(char.IsControl)||body.Any(c=>char.IsControl(c)&&c is not ('\n' or '\r' or '\t')))throw new ArgumentException("Use a subject of at most 200 characters and a draft of at most 16,000 characters. No sending is enabled.");}
    public static string Excerpt(string value,int maximum)=>value[..Math.Min(value.Length,maximum)].Trim();
    private static bool CompleteCustomerQuote(string? value,int maximum)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=maximum;
    private static string QuoteSentenceEnding(string value)=>value.TrimEnd().Last() is '.' or '?' or '!'?"":".";
    private static BusinessCustomerPlanSource? Source(IReadOnlyList<BusinessCustomerPlanSource> sources,string id,string quote,int maximum)=>sources.FirstOrDefault(x=>x.Id==id&&SourceQuote(x.Quote,quote,maximum));
    private static bool SourceQuote(string? source,string? quote,int max)=>!string.IsNullOrWhiteSpace(quote)&&quote.Length<=max&&source?.Contains(quote,StringComparison.Ordinal)==true;
    private static bool Bounded(string? value,int max)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=max;
    private static BusinessProspectingEvidence Evidence(BusinessCustomerPlanSource source,string quote,DateTimeOffset now)=>new(source.Id,source.Reference,quote,source.ObservedAtUtc??now);
    private static string Id(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();
}
