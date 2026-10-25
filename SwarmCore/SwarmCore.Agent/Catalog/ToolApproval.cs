using SwarmCore.Skills.Models;

namespace SwarmCore.Agent.Catalog;

/// <summary>
/// How the owning host presents a tool in an approval request. When a tool has
/// none, the engine derives the permission and wording from the tool's effective
/// <see cref="ToolSideEffect"/>, so a host never needs to register a tool by name
/// for it to be gated.
/// </summary>
public sealed record ToolApproval
{
    /// <summary>The permission the user grants for this tool.</summary>
    public required SkillPermission Permission { get; init; }

    /// <summary>Short user-facing name of the tool kind ("Shell command").</summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Optional one-line summary of a specific invocation (for example the
    /// command a shell call would run). Receives the typed tool input.
    /// </summary>
    public Func<object, string?>? Summarize { get; init; }
}

/// <summary>
/// Behaviours the shared tool loop gives specific kinds of tools. The owning
/// host declares them on the descriptor instead of the engine matching tool ids.
/// </summary>
[Flags]
public enum ToolEngineRoles
{
    None = 0,

    /// <summary>
    /// A general-purpose tool (code execution, tool discovery) whose presence
    /// lets the loop ask the model to change strategy once when a user-authorized
    /// turn ends in an actionable inability.
    /// </summary>
    StrategyRecovery = 1 << 0,

    /// <summary>A successful call may expose new tools; the loop refreshes the discoverable set.</summary>
    ToolDiscovery = 1 << 1,

    /// <summary>
    /// A gateway that invokes another tool by the name in its <c>name</c> input; when a
    /// policy refuses the gateway call the loop points the model at the native tool.
    /// </summary>
    ServiceGateway = 1 << 2,

    /// <summary>
    /// Builds a source pack for the answer. A result without typed context-pack
    /// evidence marks the turn's source proof incomplete.
    /// </summary>
    SourcePackBuilder = 1 << 3,
}
