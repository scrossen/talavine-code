using SwarmCore.Agent.Catalog;

namespace SwarmCore.Agent.Abstractions;

/// <summary>
/// Process-wide registry of agent tools. Each <see cref="ICapabilityHandler"/>
/// contributes its tools at startup; the LLM-driven loop later asks the catalog
/// which tools are relevant to a given goal.
/// </summary>
public interface ICapabilityCatalog
{
    /// <summary>
    /// Register a tool. Idempotent on <see cref="ToolDescriptor.ToolId"/> —
    /// re-registering the same id replaces the prior entry.
    /// </summary>
    void Register(ToolDescriptor tool);

    /// <summary>
    /// Remove a tool by id. Used by the MCP bridge to drop tools that belong
    /// to a disconnected or deleted connector so agents don't keep seeing them.
    /// Returns true if a tool was removed.
    /// </summary>
    bool Unregister(string toolId);

    /// <summary>
    /// Look up a tool by its stable id.
    /// </summary>
    ToolDescriptor? Get(string toolId);

    /// <summary>
    /// All tools registered for a given agent.
    /// </summary>
    IReadOnlyList<ToolDescriptor> GetForAgent(string ownerAgentId);

    /// <summary>
    /// All tools across all agents.
    /// </summary>
    IReadOnlyList<ToolDescriptor> All();

    /// <summary>
    /// Lightweight semantic search over the catalog. The default implementation
    /// uses keyword + description matching; richer impls (embeddings, LLM rerank)
    /// can plug in later without touching call sites.
    /// </summary>
    /// <param name="goal">The user/agent goal in natural language.</param>
    /// <param name="ownerAgentId">If supplied, restrict to one agent's tools.</param>
    /// <param name="topK">How many tools to return.</param>
    IReadOnlyList<ToolDescriptor> Search(string goal, string? ownerAgentId = null, int topK = 10);
}
