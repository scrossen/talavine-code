using System.Text.Json.Nodes;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Agent.ToolUseEngine;

namespace SwarmCore.Agent.Procedures;

/// <summary>Host-authored recurring job identity and current parameters, separate from model/source prose.</summary>
public sealed record AgentProcedureJob(string AgentId, string TaskId, string Definition, JsonObject Inputs);

/// <summary>Host owns the profile partition and chooses the task completion contract.</summary>
public interface IAgentProcedureTurnContextProvider
{
    AgentProcedureContext? Create(AgentTurnRequest request, AgentProfileSnapshot profile, IReadOnlyList<ToolDescriptor> tools);
}
