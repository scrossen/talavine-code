using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.Agent.Runtime;

/// <summary>
/// Optional async hook invoked by <see cref="AgentTurnRunner"/> after a turn's
/// results (transcript + assistant turn row + extracted metadata) have been
/// persisted. Unlike <see cref="IAgentTurnMetadataExtractor"/> — which is a
/// pure, synchronous projection onto the turn row's metadata — completion
/// handlers may perform side effects (e.g. enriching rows the turn's tools
/// created with provenance gathered across the whole turn).
///
/// Handlers are awaited sequentially after persistence, before the terminal
/// event. Preserve the turn's interactivity for required completion work;
/// optional enrichment failures are logged. Required validation failures keep
/// the saved result pending and prevent a successful terminal event.
/// </summary>
public interface IAgentTurnCompletionHandler
{
    /// <summary>
    /// True for acceptance/validation required before reporting terminal success.
    /// Optional enrichment remains best effort. Required handlers must be safe
    /// to repeat when a saved result is verified after an interrupted check.
    /// </summary>
    bool RequiresSuccessfulCompletion => false;

    Task OnTurnCompletedAsync(AgentTurnCompletedContext context, CancellationToken ct = default);
}

/// <summary>What the runner knows about a just-persisted turn.</summary>
/// <param name="ConversationId">The conversation the turn belongs to.</param>
/// <param name="TurnRowId">The UI turn row id.</param>
/// <param name="ProfileId">The resolved profile that drove the turn.</param>
/// <param name="Provider">Effective LLM provider (profile + per-request override), if known.</param>
/// <param name="Model">Effective model id (profile + per-request override), if known.</param>
/// <param name="FinalText">The assistant's final visible answer.</param>
/// <param name="ToolCalls">Every completed tool call from the turn.</param>
/// <param name="SystemPrompt">The combined system prompt used for the turn.</param>
/// <param name="MetadataJson">The merged metadata-extractor output persisted on the turn row, if any.</param>
/// <param name="UserMessage">The direct goal that initiated the completed turn.</param>
/// <param name="UserInterventionObserved">Whether host input changed while the agent was working.</param>
/// <param name="Interactivity">Whether the host still has a user waiting for terminal completion.</param>
public sealed record AgentTurnCompletedContext(
    string ConversationId,
    Guid TurnRowId,
    Guid ProfileId,
    string? Provider,
    string? Model,
    string FinalText,
    IReadOnlyList<ToolCallCompleted> ToolCalls,
    string? SystemPrompt,
    string? MetadataJson,
    string? UserMessage = null,
    bool UserInterventionObserved = false,
    LlmInteractivity Interactivity = LlmInteractivity.Background);
