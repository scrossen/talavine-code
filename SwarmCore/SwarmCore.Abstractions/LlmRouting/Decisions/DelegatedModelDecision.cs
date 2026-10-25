namespace SwarmCore.Abstractions.LlmRouting.Decisions;

public enum DelegationTaskClass { Routine, Difficult, Uncertain }

/// <summary>A proposal inside the host's admitted candidate set, never a quality or execution grant.</summary>
public sealed record DelegatedModelDecision(
    ModelDecisionCandidate Candidate,
    DelegationTaskClass TaskClass,
    string Reason,
    string DecisionProvider,
    string DecisionModel,
    bool RequiresCoordinatorReview);
