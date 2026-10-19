namespace SwarmCore.Abstractions.Collaboration;

/// <summary>An exact host-offered route. RouteName identifies a host agent lane; it is never model-authored.</summary>
public sealed record AssistanceModelChoice(string Key, string Provider, string Model, string? RouteName = null);

/// <summary>Explicit user choices, off by default. Each request captures its own immutable snapshot.</summary>
public sealed record TurnAssistanceOptions
{
    public bool RecommendOpinionModels { get; init; } = true;
    public int OpinionCount { get; init; } = 1;
    public bool RecommendReviewer { get; init; } = true;
    public bool GetOpinionsFirst { get; init; }
    public IReadOnlyList<AssistanceModelChoice> OpinionModels { get; init; } = [];
    public bool ReviewBeforeFinishing { get; init; }
    public AssistanceModelChoice? Reviewer { get; init; }
    public bool Enabled => GetOpinionsFirst || ReviewBeforeFinishing;
    public string Summary => string.Join(" / ", new[] {
        GetOpinionsFirst ? $"{(RecommendOpinionModels ? OpinionCount : OpinionModels.Count)} opinion{((RecommendOpinionModels ? OpinionCount : OpinionModels.Count) == 1 ? "" : "s")}" : null,
        ReviewBeforeFinishing ? "Independent review" : null }.Where(s => s is not null));
    public string? Validate()
    {
        if (GetOpinionsFirst && (RecommendOpinionModels ? OpinionCount : OpinionModels.Count) is < 1 or > 4) return "Choose one to four models for opinions.";
        if (GetOpinionsFirst && OpinionModels.Select(m => (m.Provider, m.Model)).Distinct().Count() != OpinionModels.Count)
            return "Choose each opinion model only once.";
        if (ReviewBeforeFinishing && !RecommendReviewer && Reviewer is null) return "Choose an agent for the review.";
        if ((GetOpinionsFirst ? OpinionModels : []).Concat(ReviewBeforeFinishing && Reviewer is not null ? [Reviewer] : [])
            .Any(m => string.IsNullOrWhiteSpace(m.Key) || string.IsNullOrWhiteSpace(m.Provider) || string.IsNullOrWhiteSpace(m.Model)))
            return "A selected model is no longer available. Choose it again.";
        return null;
    }
}
