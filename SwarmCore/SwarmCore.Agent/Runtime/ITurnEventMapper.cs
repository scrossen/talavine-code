using SwarmCore.Agent.ToolUseEngine;

namespace SwarmCore.Agent.Runtime;

/// <summary>
/// Pure translator from the engine's low-level
/// <see cref="AgentChatEvent"/> vocabulary to the UI-facing
/// <see cref="TurnEvent"/> vocabulary. Keeping this a one-shot mapper (not
/// an async enumerable wrapper) makes it trivially unit-testable.
/// </summary>
public interface ITurnEventMapper
{
    /// <summary>
    /// Map one engine event to zero or more turn events. Some engine events
    /// collapse (e.g. we don't surface <see cref="UserMessageAppendedEvent"/>
    /// through the UI stream — the UI already knows what the user typed).
    /// </summary>
    IEnumerable<TurnEvent> Map(AgentChatEvent engineEvent, TurnMappingContext context);
}

/// <summary>
/// Contextual state the mapper needs but can't derive from the event alone.
/// </summary>
public sealed record TurnMappingContext(
    string TurnId,
    Guid ProfileId,
    string ProfileName,
    DateTime TurnStartUtc);
