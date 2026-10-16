using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SwarmCore.Coding;
using SwarmCore.Coding.Agents;
using SwarmCore.Coding.Extensions;

namespace SwarmCode.Hosting.Extensions;

/// <summary>What the UserPromptSubmit hooks decided for one message.</summary>
/// <param name="Blocked">True: the message must not reach any lane.</param>
/// <param name="LaneMessage">The text the lane receives (the message plus labelled hook context).</param>
/// <param name="Reason">Why it was blocked.</param>
/// <param name="Warnings">Non-blocking hook problems to show the person.</param>
public sealed record PromptHookResult(bool Blocked, string LaneMessage, string? Reason, IReadOnlyList<string> Warnings);

/// <summary>
/// Turn-level hooks (UserPromptSubmit, Stop) and lifecycle hooks (SessionStart, SessionEnd, SubagentStop, PreCompact,
/// Notification) for SwarmCode's console and desktop hosts. Every method is safe to call from any host path: a missing
/// hooks service means nothing runs, and failures come back as warnings.
/// </summary>
public static class SwarmCodeTurnHooks
{
    /// <summary>Runs UserPromptSubmit hooks on the protected (credential-scrubbed) message.</summary>
    public static async Task<PromptHookResult> BeforePromptAsync(IServiceProvider services, string conversationId, string message, CancellationToken ct)
    {
        if (services.GetService<CodingHooks>() is not { } hooks) return new(false, message, null, []);
        CodingHookOutcome outcome;
        try { outcome = await hooks.UserPromptSubmitAsync(conversationId, message, RepositoryRoot(services), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(false, message, null, ["UserPromptSubmit hooks could not be loaded: " + ex.Message]); }
        return outcome.Blocked
            ? new(true, message, outcome.Reason, outcome.Warnings)
            : new(false, CodingHooks.WithContext(message, outcome), null, outcome.Warnings);
    }

    /// <summary>Runs Stop hooks after a turn; returns warnings to show. Never throws.</summary>
    public static async Task<IReadOnlyList<string>> AfterTurnAsync(IServiceProvider services, string conversationId, bool completed, bool cancelled, string? error)
    {
        if (services.GetService<CodingHooks>() is not { } hooks) return [];
        try
        {
            var outcome = await hooks.StopAsync(conversationId, RepositoryRoot(services), completed, cancelled, error, CancellationToken.None).ConfigureAwait(false);
            return outcome.Warnings;
        }
        catch (Exception ex) { return ["Stop hooks could not run: " + ex.Message]; }
    }

    /// <summary>
    /// Runs SessionStart hooks when a session is opened (<paramref name="source"/>: startup, resume or new). The context
    /// the hooks produce goes in front of the session's next lane message. Never throws.
    /// </summary>
    public static async Task<SessionStartHookResult> SessionStartAsync(IServiceProvider services, string conversationId, string source, CancellationToken ct = default)
    {
        try
        {
            if (services.GetService<CodingHooks>() is not { } hooks) return SessionStartHookResult.None;
            var outcome = await hooks.SessionStartAsync(conversationId, source, RepositoryRoot(services), ct).ConfigureAwait(false);
            return new(CodingHooks.SessionContext(outcome), outcome.Warnings);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return SessionStartHookResult.None; }
        catch (Exception ex) { return new(null, ["SessionStart hooks could not run: " + ex.Message]); }
    }

    /// <summary>Runs SessionEnd hooks (<paramref name="reason"/>: exit, new or closed); returns warnings. Never throws.</summary>
    public static Task<IReadOnlyList<string>> SessionEndAsync(IServiceProvider services, string conversationId, string reason, CancellationToken ct = default)
        => Informational(services, "SessionEnd", hooks => hooks.SessionEndAsync(conversationId, reason, RepositoryRoot(services), ct), ct);

    /// <summary>Runs SubagentStop hooks for an agent that reached a terminal state; returns warnings. Never throws.</summary>
    public static Task<IReadOnlyList<string>> SubagentStopAsync(IServiceProvider services, AgentRecord record, CancellationToken ct = default)
        => Informational(services, "SubagentStop", hooks => hooks.SubagentStopAsync(record.Id, record.ParentId, record.Title, record.State.ToString(),
            record.Result, record.Error, RepositoryRoot(services), ct), ct);

    /// <summary>Runs PreCompact hooks when working-context compaction begins; returns warnings. Never throws.</summary>
    public static Task<IReadOnlyList<string>> PreCompactAsync(IServiceProvider services, string conversationId, string reason, CancellationToken ct = default)
        => Informational(services, "PreCompact", hooks => hooks.PreCompactAsync(conversationId, reason, RepositoryRoot(services), ct), ct);

    /// <summary>Runs Notification hooks (<paramref name="kind"/>: permission, question or idle); returns warnings. Never throws.</summary>
    public static Task<IReadOnlyList<string>> NotificationAsync(IServiceProvider services, string conversationId, string kind, string message, CancellationToken ct = default)
        => Informational(services, "Notification", hooks => hooks.NotificationAsync(conversationId, kind, message, RepositoryRoot(services), ct), ct);

    private static async Task<IReadOnlyList<string>> Informational(IServiceProvider services, string name, Func<CodingHooks, Task<CodingHookOutcome>> run, CancellationToken ct)
    {
        try
        {
            if (services.GetService<CodingHooks>() is not { } hooks) return [];
            return (await run(hooks).ConfigureAwait(false)).Warnings;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return []; }
        catch (Exception ex) { return [$"{name} hooks could not run: " + ex.Message]; }
    }

    private static string? RepositoryRoot(IServiceProvider services) => services.GetService<IOptions<CodingOptions>>()?.Value.RepositoryRoot;
}

/// <summary>What SessionStart hooks produced: context for the session's next prompt (null: none) and warnings to show.</summary>
public sealed record SessionStartHookResult(string? Context, IReadOnlyList<string> Warnings)
{
    public static SessionStartHookResult None { get; } = new(null, []);
}
