using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.ToolUseEngine;

namespace SwarmCore.Agent.Runtime;

/// <summary>
/// Pre-turn tool selector. Hosts can replace the default no-op implementation
/// to shrink broad profile tool catalogs before provider tool schemas are sent.
/// The router receives only tools already allowed by the resolved profile; it
/// must not introduce tools outside that candidate set.
/// </summary>
public interface IAgentToolRouter
{
    AgentToolRoutingResult Route(AgentToolRoutingRequest request);
}

public sealed record AgentToolRoutingRequest
{
    public required string ConversationId { get; init; }
    public required string UserMessage { get; init; }
    /// <summary>Host-authenticated authored original; absent values use legacy wrapper extraction.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? AuthenticatedOriginalUserMessage { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool? ToolsWithheldByUser { get; init; }
    public required AgentProfileSnapshot Profile { get; init; }
    public required IReadOnlyList<ToolDescriptor> CandidateTools { get; init; }
    public CurrentViewSnapshot? CurrentView { get; init; }

    /// <summary>Host-issued repair authority; never inferred from prompt text or a conversation name.</summary>
    public AgentRepairAuthority? RepairAuthority { get; init; }

    /// <summary>Communication authority for this turn.</summary>
    public AgentCommunicationScopeKind CommunicationScopeKind { get; init; }

    /// <summary>Whether the user requested or approved durable continuation.</summary>
    public bool CommunicationScopeUserInitiated { get; init; }
}

public sealed record AgentToolRoutingResult
{
    public required IReadOnlyList<ToolDescriptor> Tools { get; init; }
    public required ToolRoutingMetadata Metadata { get; init; }

    public static AgentToolRoutingResult Unchanged(IReadOnlyList<ToolDescriptor> tools, string reason) =>
        new()
        {
            Tools = tools,
            Metadata = new ToolRoutingMetadata
            {
                Applied = false,
                Reason = reason,
                CandidateCount = tools.Count,
                SelectedCount = tools.Count,
                SelectedToolIds = tools.Select(t => t.ToolId).ToList()
            }
        };

    public static AgentToolRoutingResult Empty(int candidateCount, string reason) =>
        new()
        {
            Tools = [],
            Metadata = new ToolRoutingMetadata
            {
                Applied = true,
                Reason = reason,
                CandidateCount = candidateCount,
                SelectedCount = 0,
                SelectedToolIds = []
            }
        };
}

public sealed record ToolRoutingMetadata
{
    /// <summary>
    /// Whether the sole initially selected tool's first formatted result may
    /// finish the whole turn. Hosts with
    /// a multi-step completion contract disable this so intermediate status
    /// reads return to the model for remaining work and a final deliverable.
    /// </summary>
    public bool AllowToolResultFinalAnswer { get; init; } = true;

    public required bool Applied { get; init; }
    public required string Reason { get; init; }
    public required int CandidateCount { get; init; }
    public required int SelectedCount { get; init; }
    public IReadOnlyList<string> SelectedToolIds { get; init; } = [];

    /// <summary>
    /// The user explicitly requested evidence that can only be obtained from
    /// the routed tool set, so the provider must issue a tool call on the
    /// first model step. Later steps return to automatic tool choice so the
    /// model can synthesize a normal answer from the result.
    /// </summary>
    public bool RequireFirstToolCall { get; init; }

    /// <summary>
    /// If the provider ignores <see cref="RequireFirstToolCall"/>, the engine
    /// may invoke the sole routed tool with an empty object after verifying it
    /// is read-only and declares no required input fields. This is reserved
    /// for evidence readers whose empty request means "current status".
    /// </summary>
    public bool InvokeSoleReadOnlyToolOnFirstMiss { get; init; }
}

public sealed class NoopAgentToolRouter : IAgentToolRouter
{
    public AgentToolRoutingResult Route(AgentToolRoutingRequest request) =>
        AgentToolRoutingResult.Unchanged(request.CandidateTools, "no-op router");
}
