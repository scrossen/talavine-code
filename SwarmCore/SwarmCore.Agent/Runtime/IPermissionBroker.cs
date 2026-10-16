using SwarmCore.Skills.Abstractions;
using SwarmCore.Skills.Models;
using SwarmCore.Agent.Interactions;

namespace SwarmCore.Agent.Runtime;

/// <summary>
/// Routes skill-permission requests between the sandboxed skill executor
/// (which raises consent events) and the chat UI (which collects the user's
/// answer). Wraps <see cref="ISkillPermissionConsentService"/> with a
/// turn-aware facade so the runner can yield
/// <see cref="PermissionRequested"/> events and forward responses without
/// needing to know the consent-service internals.
/// </summary>
public interface IPermissionBroker
{
    /// <summary>
    /// Subscribe to pending permission requests raised during a turn. The
    /// returned <see cref="IDisposable"/> unsubscribes when disposed —
    /// always dispose at turn end.
    /// </summary>
    IDisposable Subscribe(Action<PendingPermissionRequest> onRequest);

    /// <summary>Forward the user's answer back to the waiting skill executor (live relay only).</summary>
    void Respond(string requestId, bool approved, PermissionGrantScope scope);

    /// <summary>
    /// Record the user's answer in the host's durable interaction ledger, when it has one,
    /// and then wake the waiting executor. UI, mobile and remote answer paths await this
    /// rather than blocking on persistence.
    /// </summary>
    Task RespondAsync(string requestId, bool approved, PermissionGrantScope scope, CancellationToken ct = default)
    {
        Respond(requestId, approved, scope);
        return Task.CompletedTask;
    }

    /// <summary>Registers repair metadata before a best-effort durable projection attempt.</summary>
    void RegisterInteractionProjection(InteractionRequestCreate request) { }

    /// <summary>Retries any missing permission projections and terminal receipts.</summary>
    Task ReconcileInteractionsAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Request user approval and wait for the response. Used by the tool-use
    /// engine for non-skill tool calls that still need an explicit human gate
    /// (for example shell commands).
    /// </summary>
    Task<PermissionConsentResult> RequestAsync(PermissionConsentRequest request, CancellationToken ct = default);
}

/// <param name="SessionId">
/// Conversation the request belongs to (the engine stamps
/// <see cref="PermissionConsentRequest.SessionId"/> with the session's
/// conversation id). A turn runner delivers only requests for its own
/// conversation; a null session id is delivered to every subscriber so an
/// unattributed request is never dropped.
/// </param>
public sealed record PendingPermissionRequest(
    string RequestId,
    string SkillId,
    string SkillName,
    string FunctionName,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<PermissionLevel> PermissionLevels,
    string? SessionId = null);
