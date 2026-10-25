namespace SwarmCore.Abstractions.LlmRouting.Decisions;

/// <summary>A host-configured connection. Credentials are references, never serialized values.</summary>
public sealed record DecisionConnection
{
    public string Provider { get; init; } = "ollama";
    public string Endpoint { get; init; } = "http://127.0.0.1:11434/v1/systemone";
    public string Model { get; init; } = "nimble";
    public string? CredentialName { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
}

public sealed record DecisionOption(string Id, string Description);
public sealed record ChoiceDecisionRequest(string State, string Instructions, IReadOnlyList<DecisionOption> Options);
public sealed record ChoiceDecision(string Choice, double? Confidence);

/// <summary>Implement for a decision API or CLI. Caller supplies the complete finite option set.</summary>
public interface IChoiceDecisionProvider
{
    string Name { get; }
    Task<ChoiceDecision> ChooseAsync(DecisionConnection connection, ChoiceDecisionRequest request, CancellationToken ct = default);
}

public interface IDecisionCredentials
{
    Task<string?> ResolveAsync(string name, CancellationToken ct);
}

/// <summary>An executable model/thinking combination offered by the host. Route is opaque to the selector.</summary>
public sealed record ModelDecisionCandidate(string Id, string Route, string Model, string Description);
public enum ModelSelectionOrigin { Inherited, User, Decision, Handoff }
public enum ModelReassessment { None, FirstPrompt, Failure, ScopeChanged, UserRequested }

/// <summary>Persist with the session. Revision protects against delayed decisions and user changes.</summary>
public sealed record SessionModelSelection
{
    public long Revision { get; init; }
    public ModelSelectionOrigin Origin { get; init; } = ModelSelectionOrigin.Inherited;
    public bool UserSelected { get; init; }
    public string? Route { get; init; }
    public string? Model { get; init; }
    public bool InitialAssessmentComplete { get; init; }
    public ModelReassessment Reassessment { get; init; }
    /// <summary>Trusted delegated-task assessment; never inferred from a route or model name.</summary>
    public DelegationTaskClass? DelegationClass { get; init; }
}

public static class ModelSelectionPolicy
{
    public static bool ShouldAssess(SessionModelSelection state) => !state.UserSelected && state.Origin != ModelSelectionOrigin.User
        && (!state.InitialAssessmentComplete || state.Reassessment != ModelReassessment.None);

    public static SessionModelSelection UserChoice(SessionModelSelection state, string route, string model)
        => state with { Revision = state.Revision + 1, Origin = ModelSelectionOrigin.User, UserSelected = true, Route = route, Model = model,
            InitialAssessmentComplete = true, Reassessment = ModelReassessment.None, DelegationClass = null };

    public static SessionModelSelection Assessed(SessionModelSelection state, ModelDecisionCandidate? choice)
        => state with { Revision = state.Revision + 1, InitialAssessmentComplete = true, Reassessment = ModelReassessment.None,
            Origin = choice is null ? state.Origin : ModelSelectionOrigin.Decision,
            Route = choice?.Route ?? state.Route, Model = choice?.Model ?? state.Model };
}
