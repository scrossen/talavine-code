using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;

namespace SwarmCore.Coding.Agents;

public sealed record AgentsSpawnInput(string Task, string? Title = null, string? Lane = null, string? Model = null, string? Isolation = null, string? Agent = null, bool Independent = false);
public sealed record AgentsIdInput(string AgentId);
public sealed record AgentsListInput(bool IncludeReported = true);
public sealed record AgentsWaitInput(IReadOnlyList<string>? AgentIds = null, bool All = true, int? TimeoutSeconds = null);
public sealed record AgentsSendInput(string AgentId, string Message);
public sealed record AgentsCancelInput(string AgentId, string? Reason = null);

/// <summary>What the model sees of an agent. The result appears only once the agent has settled.</summary>
public sealed record AgentView(
    string AgentId, string Title, string State, int Depth, string? Lane, int Turns, int ToolCalls,
    string? Activity, int? ElapsedSeconds, string? Result, string? Error,
    string? Branch = null, string? WorktreePath = null, bool? UncommittedChanges = null, int? BranchCommits = null, string? Agent = null,
    bool Independent = false)
{
    public static AgentView From(AgentRecord r, DateTime now)
    {
        var end = r.FinishedUtc ?? now;
        return new(r.Id, r.Title, r.State.ToString().ToLowerInvariant(), r.Depth, r.Lane, r.Turns, r.ToolCalls,
            r.Activity, r.StartedUtc is { } s ? (int)Math.Max(0, (end - s).TotalSeconds) : null,
            r.IsTerminal ? r.Result : null, r.Error,
            r.Worktree?.Branch, r.Worktree?.Path, r.Worktree?.HasUncommittedChanges, r.Worktree?.CommitsAhead, r.Agent, r.Independent);
    }
}

public sealed record AgentsResult(bool Success, string? Message, IReadOnlyList<AgentView> Agents)
{
    public static AgentsResult Ok(string? message, params AgentRecord[] agents) => Ok(message, (IEnumerable<AgentRecord>)agents);
    public static AgentsResult Ok(string? message, IEnumerable<AgentRecord> agents)
        => new(true, message, agents.Select(a => AgentView.From(a, DateTime.UtcNow)).ToList());
    public static AgentsResult Fail(string message) => new(false, message, []);
}

/// <summary>
/// The <c>agents.*</c> tools. <paramref name="owner"/> resolves the trusted calling conversation (the current turn for
/// engine lanes, the bridged native turn for CLI lanes); a model can never name another conversation as owner.
/// </summary>
public static class AgentTeamTools
{
    public const string OwnerAgentId = "coding.agents";
    public const string SpawnToolId = "agents.spawn";
    public const string WaitToolId = "agents.wait";
    public const string StatusToolId = "agents.status";
    public const string ListToolId = "agents.list";
    public const string SendToolId = "agents.send";
    public const string CancelToolId = "agents.cancel";
    public static readonly IReadOnlyList<string> AllToolIds = [SpawnToolId, WaitToolId, StatusToolId, ListToolId, SendToolId, CancelToolId];

    public static IReadOnlyList<ToolDescriptor> Create(AgentTeam team, Func<string?> owner) =>
    [
        ToolDescriptor.Create<AgentsSpawnInput, AgentsResult>(SpawnToolId, OwnerAgentId,
            "Start an agent: a full coding agent with your tools, in its own session, working in the background. Returns at once with "
            + "agentId. task must be complete and self-contained (it cannot see this conversation): goal, files/URLs, constraints, "
            + "the result to report. Optional title (short), lane and model (defaults: the session's). isolation: \"worktree\" gives it its own "
            + "git worktree on a new branch from your HEAD (use when agents edit files in parallel; review and merge the branch with git). "
            + "agent: the name of a role from the Agent roles index (its instructions, lane/model and tool limits apply). Collect with agents.wait. "
            + "independent: true starts separate follow-up work you noticed that is outside the current task (a bug spotted in passing, a "
            + "next step worth its own session): it runs now in its own session under this one, its result is not handed back to you, and "
            + "agents.wait does not wait for it; the person reviews it directly.",
            ToolCostClass.Expensive, ToolSideEffect.Exec,
            (input, ct) => Run(owner, async id => AgentsResult.Ok(input.Independent
                    ? "Started as independent work in its own session; the person reviews it. Continue your task."
                    : "Started. Continue other work; collect the result with agents.wait.",
                await team.SpawnAsync(id, new AgentSpawnRequest(input.Task, input.Title, input.Lane, input.Model, input.Isolation, input.Agent, input.Independent), ct).ConfigureAwait(false))),
            keywords: ["agent", "subagent", "spawn", "delegate", "parallel", "background", "team", "follow up", "independent"]),
        ToolDescriptor.Create<AgentsWaitInput, AgentsResult>(WaitToolId, OwnerAgentId,
            "Wait for agents you started to finish and return their reports. agentIds defaults to every agent whose report you have "
            + "not received; all=false returns when the first finishes. timeoutSeconds is capped (default the cap); on timeout it "
            + "returns current progress, and you may wait again.",
            ToolCostClass.Cheap, ToolSideEffect.ReadOnly,
            (input, ct) => Run(owner, async id =>
            {
                var agents = await team.WaitAsync(id, input.AgentIds, input.All,
                    input.TimeoutSeconds is > 0 ? TimeSpan.FromSeconds(input.TimeoutSeconds.Value) : TimeSpan.Zero, ct).ConfigureAwait(false);
                return AgentsResult.Ok(agents.Count == 0 ? "No agents to wait for." : agents.Any(a => !a.IsTerminal) ? "Some agents are still working." : null, agents);
            }),
            keywords: ["agent", "wait", "join", "collect", "result"]),
        ToolDescriptor.Create<AgentsIdInput, AgentsResult>(StatusToolId, OwnerAgentId,
            "Check one agent you started: state (queued, running, idle = finished, failed, cancelled, interrupted), what it is doing, and its report once finished.",
            ToolCostClass.Trivial, ToolSideEffect.ReadOnly,
            (input, ct) => Run(owner, id => Task.FromResult(AgentsResult.Ok(null, team.Status(id, input.AgentId)))),
            keywords: ["agent", "status", "progress"]),
        ToolDescriptor.Create<AgentsListInput, AgentsResult>(ListToolId, OwnerAgentId,
            "List the agents you started, newest first (use after a restart instead of starting duplicates). includeReported=false hides finished agents whose report you already received.",
            ToolCostClass.Trivial, ToolSideEffect.ReadOnly,
            (input, ct) => Run(owner, id => Task.FromResult(AgentsResult.Ok(null, team.Children(id, input.IncludeReported)))),
            keywords: ["agent", "list", "team"]),
        ToolDescriptor.Create<AgentsSendInput, AgentsResult>(SendToolId, OwnerAgentId,
            "Send a follow-up to an agent you started (more detail, a correction, a next step). A finished agent resumes with its full session; a running one gets it after its current step.",
            ToolCostClass.Expensive, ToolSideEffect.Exec,
            (input, ct) => Run(owner, async id => AgentsResult.Ok("Sent.", await team.SendAsync(id, input.AgentId, input.Message, ct).ConfigureAwait(false))),
            keywords: ["agent", "message", "follow up", "continue"]),
        ToolDescriptor.Create<AgentsCancelInput, AgentsResult>(CancelToolId, OwnerAgentId,
            "Stop an agent you started (and any agents it started) when its work is no longer needed.",
            ToolCostClass.Cheap, ToolSideEffect.WritesLocal,
            (input, ct) => Run(owner, async id => AgentsResult.Ok("Stopped.", await team.CancelAsync(id, input.AgentId, ct).ConfigureAwait(false))),
            keywords: ["agent", "cancel", "stop"]),
    ];

    private static async Task<AgentsResult> Run(Func<string?> owner, Func<string, Task<AgentsResult>> call)
    {
        if (owner() is not { Length: > 0 } id) return AgentsResult.Fail("Agents can be used only inside a conversation turn.");
        try { return await call(id).ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { return AgentsResult.Fail(ex.Message); }
    }
}

/// <summary>Publishes the <c>agents.*</c> tools for engine lanes; the owner is the current turn's conversation.</summary>
public sealed class AgentTeamToolRegistration(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var team = services.GetRequiredService<AgentTeam>();
        if (!team.Options.Enabled) return Task.CompletedTask;
        var turns = services.GetRequiredService<ICurrentTurnContextAccessor>();
        var catalog = services.GetRequiredService<ICapabilityCatalog>();
        foreach (var tool in AgentTeamTools.Create(team, () => turns.Current?.ConversationId)) catalog.Register(tool);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
