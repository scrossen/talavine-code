namespace SwarmCore.Agent.Runtime;

/// <summary>
/// Deterministic final-answer policy applied after the agent finishes but
/// before the completed answer is persisted or sealed in the UI. Hosts use
/// this for evidence contracts that cannot safely depend on prompt compliance.
/// </summary>
public interface IAgentTurnOutputGuard
{
    AgentTurnOutputGuardResult Apply(AgentTurnOutputGuardContext context);
    Task<AgentTurnOutputGuardResult> ApplyAsync(AgentTurnOutputGuardContext context, CancellationToken ct = default)
        => Task.FromResult(Apply(context));
}

public sealed record AgentTurnOutputGuardContext(
    AgentTurnRequest Request,
    string ProposedFinalText,
    IReadOnlyList<ToolCallCompleted> ToolCalls);

public sealed record AgentTurnOutputGuardResult(
    string FinalText,
    bool Replaced,
    string? Reason = null)
{
    public static AgentTurnOutputGuardResult Unchanged(string finalText) =>
        new(finalText, Replaced: false);

    public static AgentTurnOutputGuardResult Replace(string finalText, string reason) =>
        new(finalText, Replaced: true, reason);
}
