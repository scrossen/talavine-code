using System.Text.Json.Nodes;

namespace SwarmCore.Agent.Runtime;

/// <summary>
/// Optional hook for product-specific turn metadata, such as citation sources
/// discovered through tools during an agent turn.
/// </summary>
public interface IAgentTurnMetadataExtractor
{
    JsonObject? Extract(AgentTurnMetadataContext context);
    Task<JsonObject?> ExtractAsync(AgentTurnMetadataContext context, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Extract(context));
    }
}

public sealed record AgentTurnMetadataContext(
    string ConversationId,
    Guid TurnRowId,
    string FinalText,
    IReadOnlyList<ToolCallCompleted> ToolCalls,
    string? SystemPrompt = null,
    IReadOnlySet<Guid>? AllowedSourceAccountIds = null);
