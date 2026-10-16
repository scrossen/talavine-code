namespace SwarmCore.Agent.Business;

public enum BusinessCustomerPlanProgress { Draft, Confirmed, Researching, Prepared, NeedsResearchRetry, NeedsResearchReview }
public sealed record BusinessCustomerGoalRequest(string Goal, string? Website = null, bool UseAi = true,
    int ResearchBudget = BusinessCustomerPlanPolicy.DefaultResearchBudget);
public sealed record BusinessWebsiteSuggestion(bool Ok, string? Website = null, string? Source = null, string? Error = null);
public sealed record BusinessCustomerDraft(string Name, BusinessKind Kind, string Offer, string Audience,
    string SearchQuery, string[] FitPhrases);
/// <summary>Attribution only. Public website text and model suggestions never constitute permission or commercial authority.</summary>
public sealed record BusinessCustomerPlanSource(string Id, string Label, string Reference, string? Quote = null,
    DateTimeOffset? ObservedAtUtc = null);
public sealed record BusinessCustomerPlan(Guid PlanId, int Revision, string Goal, string? Website, int BriefRevision,
    BusinessBriefDefinition? CurrentBrief, BusinessCustomerDraft Draft, BusinessCustomerPlanSource[] Sources,
    string[] MissingDecisions, BusinessCustomerPlanProgress Progress, long? OwnedTaskId = null,
    Guid[]? ProspectIds = null, string? ResearchSummary = null, string? NextAction = null,
    string InitialMode = "Review", bool OutboundEnabled = false, BusinessProspectingWorkspace? Prospecting = null,
    int ResearchBudget = BusinessCustomerPlanPolicy.DefaultResearchBudget);
public sealed record BusinessCustomerPlanResult(bool Ok, BusinessCustomerPlan? Plan = null, string? Error = null, bool Replay = false);

public static class BusinessCustomerPlanPolicy
{
    // Missing tails in legacy protected plans retain their original three-result allowance.
    public const int DefaultResearchBudget = 3;
    public const int MaximumProspects = 10;
    public const string FunctionType = SwarmCore.Abstractions.LlmRouting.FunctionTypes.BusinessSetupPlanning;
    /// <summary>A website alone is a research instruction, never an approved offer or account grant.</summary>
    public static BusinessCustomerGoalRequest NormalizeGoal(BusinessCustomerGoalRequest request)
    {
        var goal = request.Goal?.Trim() ?? "";
        var website = request.Website?.Trim();
        static string? PublicWebsite(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace)) return value;
            if (Marketing.LeadResearchUrlPolicy.TryGetBusinessUri(value, out var uri)) return uri!.AbsoluteUri;
            if (!value.Contains(":", StringComparison.Ordinal) && value.Contains('.', StringComparison.Ordinal)
                && Marketing.LeadResearchUrlPolicy.TryGetBusinessUri("https://" + value, out uri)) return uri!.AbsoluteUri;
            return value;
        }
        website = PublicWebsite(website);
        if (string.IsNullOrWhiteSpace(website))
        {
            var candidate = PublicWebsite(goal);
            if (Marketing.LeadResearchUrlPolicy.TryGetBusinessUri(candidate, out _)) { website = candidate; goal = ""; }
        }
        if (goal.Length == 0 && Marketing.LeadResearchUrlPolicy.TryGetBusinessUri(website, out _))
            goal = "Prepare a customer research plan and draft sales material for this business.";
        return request with { Goal = goal, Website = string.IsNullOrWhiteSpace(website) ? null : website };
    }
    public static void ValidateGoal(BusinessCustomerGoalRequest request)
    {
        if(request.ResearchBudget is <1 or >MaximumProspects)
            throw new ArgumentException("Choose a research budget of 1–10 companies for this saved plan.");
        if(string.IsNullOrWhiteSpace(request.Goal)||request.Goal.Length>2000||request.Goal.Any(c=>char.IsControl(c)&&c is not ('\n' or '\r' or '\t')))
            throw new ArgumentException("Describe your business goal in at most 2000 characters.");
        if(!string.IsNullOrWhiteSpace(request.Website)&&!Marketing.LeadResearchUrlPolicy.TryGetBusinessUri(request.Website,out _))
            throw new ArgumentException("Choose an ordinary public business website without sign-in or private network access.");
    }
    /// <summary>Fill absent research choices with the owner's exact audience words. Adds no inferred facts, identity or permission.</summary>
    public static BusinessCustomerDraft FromOwnerAudience(BusinessCustomerDraft draft)
    {
        var audience=Marketing.LeadDiscovery.Normalize(draft.Audience??"");
        return draft with
        {
            SearchQuery=string.IsNullOrWhiteSpace(draft.SearchQuery)&&audience.Length is >0 and <=300?audience:draft.SearchQuery?.Trim()??"",
            FitPhrases=draft.FitPhrases is not {Length:>0}&&audience.Length is >=3 and <=100?[audience]:draft.FitPhrases?.Select(x=>x?.Trim()??"").ToArray()??[]
        };
    }
    public static string[] Missing(BusinessCustomerDraft draft)
    {
        var missing=new List<string>();
        if(string.IsNullOrWhiteSpace(draft.Name))missing.Add("Business name");
        if(!Enum.IsDefined(draft.Kind))missing.Add("Service or product offer");
        if(string.IsNullOrWhiteSpace(draft.Offer))missing.Add("Offer to review");
        if(string.IsNullOrWhiteSpace(draft.Audience))missing.Add("Intended customers");
        if(string.IsNullOrWhiteSpace(draft.SearchQuery))missing.Add("Public business search");
        if(draft.FitPhrases is not {Length:>0})missing.Add("Words to match on customer business pages");
        return missing.ToArray();
    }
    public static void ValidateDraft(BusinessCustomerDraft draft)
    {
        if(draft.Name is null||draft.Name.Length>200||draft.Offer is null||draft.Offer.Length>4000||draft.Audience is null||draft.Audience.Length>2000||Missing(draft).Length>0)
            throw new ArgumentException("Review the business name, offer, intended customers and public research criteria.");
        new Marketing.LeadDiscoveryRequest{Queries=[draft.SearchQuery],RequiredPhrases=draft.FitPhrases,MaxResultsPerQuery=MaximumProspects}.Validate();
    }
}
