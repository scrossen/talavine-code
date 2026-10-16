namespace SwarmCore.Agent.Business;

/// <summary>Public-source attribution is evidence for review, never buying intent, consent or authority.</summary>
public sealed record BusinessProspectingEvidence(string SourceId,string Url,string Quote,DateTimeOffset ObservedAtUtc,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    bool IsCompletePassage=false);
public sealed record BusinessObservedSummary(string Text,BusinessProspectingEvidence[] Evidence);
public sealed record BusinessAudienceHypothesis(string Id,string Label,string UseCase,string PainHypothesis,
    string SearchQuery,string[] FitPhrases,BusinessProspectingEvidence[] Evidence,bool IsHypothesis=true);
public enum BusinessProspectRouteKind { PublishedEmail, PublishedPage, PublishedProfile, PublishedPhone, InferredEmail, ProviderVerifiedEmail }
public sealed record BusinessProspectContactRoute(string Value,BusinessProspectRouteKind Kind,
    BusinessProspectingEvidence Evidence,bool DeliveryVerified=false);
public sealed record BusinessProspectDecisionMaker(string Name,string Role,string? ProfileUrl,
    BusinessProspectingEvidence[] Evidence);
public sealed record BusinessProspectResearch(Guid ProspectId,string Name,string Domain,string Description,
    BusinessProspectingEvidence[] Facts,BusinessProspectDecisionMaker[] DecisionMakers,
    BusinessProspectContactRoute[] ContactRoutes,string MissingData,bool ExistingRecord=false);
public sealed record BusinessProspectDraft(Guid ProspectId,int Revision,string Subject,string Body,
    BusinessProspectingEvidence[] Evidence,DateTimeOffset CreatedAtUtc,bool EditedByOwner=false,
    string? SelectedEmail=null,Guid? CaseId=null,long? ContactId=null,long? TaskId=null);
public sealed record BusinessProspectingWorkspace(BusinessObservedSummary Summary,
    BusinessAudienceHypothesis[] Audiences,string? SelectedAudienceId=null,
    BusinessProspectResearch[]? Companies=null,BusinessProspectDraft[]? Drafts=null,string? StatusMessage=null);

/// <summary>Optional typed extraction. Summary and support quotes must occur in the supplied sources.</summary>
public sealed record BusinessProspectingSuggestion(string SummarySourceId,string SummaryQuote,
    BusinessAudienceSuggestion[] Audiences);
public sealed record BusinessAudienceSuggestion(string Label,string UseCase,string PainHypothesis,
    string SearchQuery,string[] FitPhrases,string SourceId,string Quote);
public sealed record BusinessProspectingDraftSuggestion(string Subject,string FactSourceId,string FactQuote,
    string OfferQuote);
