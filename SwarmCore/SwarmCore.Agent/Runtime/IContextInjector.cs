namespace SwarmCore.Agent.Runtime;

/// <summary>
/// Produces a named context block appended to the system prompt for a turn.
/// Each implementation is a focused, testable unit (context facts, artifact
/// summary, conversation state, etc.). Profiles declare which injectors
/// they want by name.
/// </summary>
public interface IContextInjector
{
    /// <summary>
    /// Stable identifier used by profiles to declare which injectors to run.
    /// Case-insensitive; lowercase by convention.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Build the context text for the current turn, or null to contribute
    /// nothing. Called on every turn; implementations should be cheap or
    /// cache aggressively.
    /// </summary>
    Task<string?> BuildAsync(ContextInjectionRequest request, CancellationToken ct = default);
}

/// <summary>
/// Inputs available to a context injector. Kept intentionally narrow;
/// anything wider should be exposed through DI instead.
/// </summary>
public sealed record ContextInjectionRequest(
    string ConversationId,
    string UserMessage,
    Guid ProfileId,
    IReadOnlySet<Guid>? AllowedSourceAccountIds = null,
    CurrentViewSnapshot? CurrentView = null,
    bool CommunicationScopeExplicitlyWidened = false,
    Guid? TurnId = null,
    AgentCommunicationScopeKind CommunicationScopeKind = AgentCommunicationScopeKind.None,
    string CommunicationScopeAuthority = "legacy-unspecified",
    bool CommunicationScopeUserInitiated = false,
    CurrentTurnToolAccessState? ToolAccessState = null,
    Action<SwarmCore.Abstractions.Context.ContextPack>? CaptureWorkingContextSource = null,
    Action? MarkWorkingContextSourceIncomplete = null,
    Action<string>? CaptureWorkingContextAuthority = null,
    Action<Func<CancellationToken, Task<string>>>? RegisterWorkingContextAuthorityRefresh = null,
    string? WorkspaceRoot = null)
{
    /// <summary>Host-compiled original-request restriction; never supplied by context/tool content.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool? ToolsWithheldByUser { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? AuthenticatedOriginalUserMessage { get; init; }

    /// <summary>
    /// Host-set: no person is attending this turn (worker cycle, scheduled task, follow-up). Injectors that
    /// carry the person's private context (personal memory) contribute nothing for such turns, even when
    /// the communication scope was restored from an earlier user request.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UnattendedAgentTurn { get; init; }

    /// <summary>Host-set: the turn continues a durable mission objective (unattended by definition).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Guid? MissionObjectiveId { get; init; }
}

/// <summary>
/// Looks up injectors by name and assembles the final context block.
/// </summary>
public interface IContextInjectorRegistry
{
    /// <summary>
    /// Build the combined context block for a profile's declared injector
    /// list. Missing injector names are skipped with a warning; null
    /// contributions are omitted.
    /// </summary>
    Task<string?> BuildCombinedAsync(
        IReadOnlyList<string> injectorNames,
        ContextInjectionRequest request,
        CancellationToken ct = default);
}
