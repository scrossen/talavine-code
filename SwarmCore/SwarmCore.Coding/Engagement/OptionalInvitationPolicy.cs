namespace SwarmCore.Coding.Engagement;

/// <summary>
/// Immutable completion facts supplied by the host, never inferred from an answer's wording.
/// SuccessfulWorkTool requires a successful mutating ToolCallCompleted receipt for this attempt;
/// a workspace diff is insufficient. JournalSaved requires the completed job journal to be saved.
/// AcceptanceFailed overrides both positive proof sources.
/// </summary>
public sealed record CodingCompletionEvidence(
    bool Completed,
    bool Cancelled,
    bool HasError,
    bool AcceptancePassed,
    bool DirectUserRequest,
    bool PlanOnly,
    bool Temporary,
    bool Continuation,
    bool JournalSaved,
    bool SuccessfulWorkTool,
    bool AcceptanceFailed);

/// <summary>Eligibility for an optional invitation; hosts own saved preferences and presentation.</summary>
public static class OptionalInvitationPolicy
{
    public static bool Qualifies(CodingCompletionEvidence? evidence)
        => evidence is
        {
            Completed: true,
            Cancelled: false,
            HasError: false,
            DirectUserRequest: true,
            PlanOnly: false,
            Temporary: false,
            Continuation: false,
            JournalSaved: true,
            AcceptanceFailed: false,
        } && (evidence.AcceptancePassed || evidence.SuccessfulWorkTool);

    public static bool ShouldShow(bool hasUsefulSuccess, bool dismissed, bool busy)
        => hasUsefulSuccess && !dismissed && !busy;
}
