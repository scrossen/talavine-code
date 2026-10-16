using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Missions;

namespace SwarmCore.Coding.Extensions;

/// <summary>
/// Runs the hook events for a host: tool hooks (through <see cref="HookedToolInvocationGuard"/> and
/// <see cref="HookedProfileResolver"/>), turn hooks (<see cref="UserPromptSubmitAsync"/>, <see cref="StopAsync"/>,
/// called by the host's turn runner) and lifecycle hooks (<see cref="SessionStartAsync"/>, <see cref="SessionEndAsync"/>,
/// <see cref="SubagentStopAsync"/>, <see cref="PreCompactAsync"/>, <see cref="NotificationAsync"/>, called by the host
/// when those things happen). Hooks apply to SwarmCore's own tool loop; native CLI lanes run their own tools and their
/// own hook configuration.
/// </summary>
public sealed class CodingHooks(
    ICodingHookSource source,
    CodingHookRunner runner,
    ISandboxPathResolver? sandbox = null,
    ActiveCodingWorkspaces? activeWorkspaces = null)
{
    public const int MaxPayloadCharacters = 32_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public ICodingHookSource Source => source;

    /// <summary>The repository a tool call in <paramref name="conversationId"/> runs in.</summary>
    public string? WorkspaceRoot(string? conversationId)
        => (conversationId is not null ? activeWorkspaces?.Get(conversationId)?.FullRoot : null) ?? sandbox?.Root;

    public async Task<bool> HasToolHooksAsync(string? repositoryRoot, CancellationToken ct = default)
        => (await source.GetHooksAsync(repositoryRoot, ct).ConfigureAwait(false))
            .Any(h => h.Event is CodingHookEvent.PreToolUse or CodingHookEvent.PostToolUse);

    public Task<CodingHookOutcome> PreToolUseAsync(string toolId, object? input, string? conversationId, CancellationToken ct = default)
        => RunAsync(CodingHookEvent.PreToolUse, toolId, conversationId, WorkspaceRoot(conversationId), new JsonObject
        {
            ["conversationId"] = conversationId,
            ["tool"] = toolId,
            ["input"] = ToNode(input),
        }, ct);

    public Task<CodingHookOutcome> PostToolUseAsync(string toolId, object? input, object? result, string? conversationId, CancellationToken ct = default)
        => RunAsync(CodingHookEvent.PostToolUse, toolId, conversationId, WorkspaceRoot(conversationId), new JsonObject
        {
            ["conversationId"] = conversationId,
            ["tool"] = toolId,
            ["input"] = ToNode(input),
            ["result"] = ToNode(result),
        }, ct);

    public Task<CodingHookOutcome> UserPromptSubmitAsync(string conversationId, string prompt, string? repositoryRoot, CancellationToken ct = default)
        => RunAsync(CodingHookEvent.UserPromptSubmit, null, conversationId, repositoryRoot, new JsonObject
        {
            ["conversationId"] = conversationId,
            ["prompt"] = prompt,
        }, ct);

    public Task<CodingHookOutcome> StopAsync(string conversationId, string? repositoryRoot, bool completed, bool cancelled, string? error, CancellationToken ct = default)
        => RunAsync(CodingHookEvent.Stop, null, conversationId, repositoryRoot, new JsonObject
        {
            ["conversationId"] = conversationId,
            ["completed"] = completed,
            ["cancelled"] = cancelled,
            ["error"] = error,
        }, ct);

    /// <summary>
    /// SessionStart: <paramref name="source"/> is <c>startup</c> (the host process started with this session), <c>resume</c>
    /// (an existing session was opened) or <c>new</c> (a session was created). Stdout is context for the session's next prompt.
    /// </summary>
    public Task<CodingHookOutcome> SessionStartAsync(string conversationId, string source, string? repositoryRoot, CancellationToken ct = default)
        => RunAsync(CodingHookEvent.SessionStart, null, conversationId, repositoryRoot, new JsonObject
        {
            ["conversationId"] = conversationId,
            ["source"] = source,
        }, ct);

    /// <summary>SessionEnd: <paramref name="reason"/> is <c>exit</c>, <c>new</c> or <c>closed</c>. Informational.</summary>
    public Task<CodingHookOutcome> SessionEndAsync(string conversationId, string reason, string? repositoryRoot, CancellationToken ct = default)
        => RunAsync(CodingHookEvent.SessionEnd, null, conversationId, repositoryRoot, new JsonObject
        {
            ["conversationId"] = conversationId,
            ["reason"] = reason,
        }, ct);

    /// <summary>SubagentStop: an agent reached a terminal state. <c>conversationId</c> is the conversation that started it.</summary>
    public Task<CodingHookOutcome> SubagentStopAsync(string agentId, string? parentConversationId, string title, string state, string? result, string? error,
        string? repositoryRoot, CancellationToken ct = default)
        => RunAsync(CodingHookEvent.SubagentStop, null, parentConversationId, repositoryRoot, new JsonObject
        {
            ["conversationId"] = parentConversationId,
            ["agentId"] = agentId,
            ["title"] = title,
            ["state"] = state,
            ["result"] = result is null ? null : ExtensionFiles.Clip(result, MaxSubagentResultCharacters),
            ["error"] = error,
        }, ct);

    /// <summary>PreCompact: working-context compaction began for <paramref name="reason"/>. Informational; it cannot hold compaction.</summary>
    public Task<CodingHookOutcome> PreCompactAsync(string conversationId, string reason, string? repositoryRoot, CancellationToken ct = default)
        => RunAsync(CodingHookEvent.PreCompact, null, conversationId, repositoryRoot, new JsonObject
        {
            ["conversationId"] = conversationId,
            ["reason"] = reason,
        }, ct);

    /// <summary>Notification: the host raised something to the person (<paramref name="kind"/>: permission, question or idle). Informational.</summary>
    public Task<CodingHookOutcome> NotificationAsync(string conversationId, string kind, string message, string? repositoryRoot, CancellationToken ct = default)
        => RunAsync(CodingHookEvent.Notification, null, conversationId, repositoryRoot, new JsonObject
        {
            ["conversationId"] = conversationId,
            ["kind"] = kind,
            ["message"] = ExtensionFiles.Clip(message, MaxNotificationCharacters),
        }, ct);

    public const int MaxSubagentResultCharacters = 8_000;
    public const int MaxNotificationCharacters = 4_000;

    /// <summary>The text a lane receives: the person's message plus any prompt-hook context, clearly labelled.</summary>
    public static string WithContext(string message, CodingHookOutcome outcome)
        => outcome.Context.Count == 0 ? message
            : message + "\n\n[Context added by a UserPromptSubmit hook]\n" + string.Join("\n\n", outcome.Context);

    /// <summary>The context SessionStart hooks produced, joined, or null when they produced none.</summary>
    public static string? SessionContext(CodingHookOutcome outcome)
        => outcome.Context.Count == 0 ? null : string.Join("\n\n", outcome.Context);

    /// <summary>A lane message with session-start context in front, clearly labelled; the transcript keeps the person's text.</summary>
    public static string WithSessionContext(string message, string? sessionContext)
        => string.IsNullOrWhiteSpace(sessionContext) ? message
            : "[Context added by a SessionStart hook]\n" + sessionContext.Trim() + "\n\n" + message;

    private async Task<CodingHookOutcome> RunAsync(CodingHookEvent evt, string? toolId, string? conversationId, string? root, JsonObject payload, CancellationToken ct)
    {
        var hooks = await source.GetHooksAsync(root, ct).ConfigureAwait(false);
        if (!hooks.Any(h => h.Event == evt && h.Matches(toolId))) return CodingHookOutcome.None;
        return await runner.RunAsync(hooks, evt, toolId, payload, root ?? Environment.CurrentDirectory, ct).ConfigureAwait(false);
    }

    internal static JsonNode? ToNode(object? value)
    {
        if (value is null) return null;
        try
        {
            var text = value is string s ? JsonSerializer.Serialize(s, Json) : JsonSerializer.Serialize(value, value.GetType(), Json);
            if (text.Length > MaxPayloadCharacters) return JsonValue.Create(text[..MaxPayloadCharacters] + "…[truncated]");
            return JsonNode.Parse(text);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
        {
            return JsonValue.Create(ExtensionFiles.Clip(value.ToString() ?? "", MaxPayloadCharacters));
        }
    }
}

/// <summary>
/// The engine's guard seam with PreToolUse hooks after the host's own guard. The engine consults guards for tools
/// that are not read-only, before any approval prompt; read-only tools get PreToolUse through
/// <see cref="HookedProfileResolver"/>. A blocking hook's reason goes back to the model as the refusal.
/// </summary>
public sealed class HookedToolInvocationGuard(IToolInvocationGuard? inner, CodingHooks hooks) : IToolInvocationGuard
{
    public IToolInvocationGuard? Inner => inner;

    public async Task<ToolGuardDecision> InspectAsync(ToolInvocationRequest request, CancellationToken ct = default)
    {
        var decision = inner is null ? ToolGuardDecision.Allow() : await inner.InspectAsync(request, ct).ConfigureAwait(false);
        if (!decision.Allowed) return decision;
        var outcome = await hooks.PreToolUseAsync(request.ToolId, request.Input, request.ConversationId, ct).ConfigureAwait(false);
        return outcome.Blocked ? ToolGuardDecision.Block("PreToolUse hook: " + outcome.Reason) : decision;
    }
}

/// <summary>
/// A tool result with notes added by PostToolUse hooks (serialized as <c>{ result, hookNotes }</c>). The engine and the
/// host tool bridge unwrap it (<see cref="ToolResultAnnotations.Unwrap"/>) before inspecting evidence, images or failures.
/// </summary>
public sealed record HookAnnotatedToolResult(object? Result, IReadOnlyList<string> HookNotes) : IAnnotatedToolResult;

/// <summary>
/// Decorates a profile resolver so the turn's tools run PostToolUse hooks (and PreToolUse for read-only calls, which
/// the engine never shows a guard). Tools are wrapped only when the workspace has tool hooks, so a host without hooks
/// sees its descriptors unchanged. A blocked read-only call is reported as not dispatched.
/// </summary>
public sealed class HookedProfileResolver(IProfileResolver inner, CodingHooks hooks) : IProfileResolver
{
    public IProfileResolver Inner => inner;

    public async Task<ResolvedProfile> ResolveAsync(string conversationId, Guid? explicitProfileId = null, CancellationToken ct = default)
    {
        var resolved = await inner.ResolveAsync(conversationId, explicitProfileId, ct).ConfigureAwait(false);
        if (!await hooks.HasToolHooksAsync(hooks.WorkspaceRoot(conversationId), ct).ConfigureAwait(false)) return resolved;
        return resolved with { Tools = resolved.Tools.Select(t => Wrap(t, conversationId)).ToList() };
    }

    internal ToolDescriptor Wrap(ToolDescriptor descriptor, string conversationId)
    {
        var invoke = descriptor.Invoke;
        return descriptor with
        {
            Invoke = async (input, ct) =>
            {
                if (descriptor.EffectiveSideEffect(input) == ToolSideEffect.ReadOnly)
                {
                    var pre = await hooks.PreToolUseAsync(descriptor.ToolId, input, conversationId, ct).ConfigureAwait(false);
                    if (pre.Blocked)
                        throw new ToolCallNotDispatchedException("Blocked by a PreToolUse hook: " + pre.Reason + " The tool did not run.");
                }
                var result = await invoke(input, ct).ConfigureAwait(false);
                var post = await hooks.PostToolUseAsync(descriptor.ToolId, input, result, conversationId, ct).ConfigureAwait(false);
                // The tool already ran, so a PostToolUse "block" cannot undo it: as in Claude Code, its reason reaches the model as feedback.
                var notes = post.Blocked && !string.IsNullOrWhiteSpace(post.Reason)
                    ? [.. post.Context, "PostToolUse hook objected to this result: " + post.Reason]
                    : post.Context;
                return notes.Count == 0 ? result : new HookAnnotatedToolResult(result, notes);
            },
        };
    }
}

public static class CodingHooksServiceCollectionExtensions
{
    /// <summary>
    /// Turns hooks on for the engine: decorates the currently registered <see cref="IToolInvocationGuard"/> and
    /// <see cref="IProfileResolver"/> (call after every other registration of those seams). The hook source, runner
    /// and default approval store come from the harness; a host registers a durable
    /// <see cref="ICodingHookApprovalStore"/> before calling <c>AddSwarmCoreCodingHarness</c>.
    /// </summary>
    public static IServiceCollection AddCodingHooks(this IServiceCollection services)
    {
        services.TryAddSingleton<CodingHookRunner>();
        services.TryAddSingleton<ICodingHookApprovalStore, NoCodingHookApprovals>();
        services.TryAddSingleton<ICodingHookSource, CodingHookSource>();
        services.TryAddSingleton<CodingHooks>();
        if (services.Any(d => d.ServiceType == typeof(CodingHooksMarker))) return services;
        services.AddSingleton<CodingHooksMarker>();

        Decorate<IToolInvocationGuard>(services, (sp, inner) => new HookedToolInvocationGuard(inner, sp.GetRequiredService<CodingHooks>()),
            sp => new HookedToolInvocationGuard(null, sp.GetRequiredService<CodingHooks>()));
        Decorate<IProfileResolver>(services, (sp, inner) => new HookedProfileResolver(inner, sp.GetRequiredService<CodingHooks>()), null);
        return services;
    }

    private static void Decorate<T>(IServiceCollection services, Func<IServiceProvider, T, T> wrap, Func<IServiceProvider, T>? whenMissing) where T : class
    {
        var existing = services.LastOrDefault(d => d.ServiceType == typeof(T));
        if (existing is null)
        {
            if (whenMissing is not null) services.AddSingleton(whenMissing);
            return;
        }
        services.Remove(existing);
        services.Add(ServiceDescriptor.Describe(typeof(T), sp => wrap(sp, (T)(existing.ImplementationInstance
            ?? existing.ImplementationFactory?.Invoke(sp)
            ?? ActivatorUtilities.CreateInstance(sp, existing.ImplementationType!))), existing.Lifetime));
    }

    private sealed class CodingHooksMarker;
}
