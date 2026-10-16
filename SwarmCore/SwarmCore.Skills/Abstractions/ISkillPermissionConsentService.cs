using SwarmCore.Skills.Models;

namespace SwarmCore.Skills.Abstractions;

/// <summary>
/// Service that mediates permission consent between the skill executor and the UI.
/// When a skill requires elevated permissions that were not pre-granted, the executor
/// calls <see cref="RequestConsentAsync"/> which blocks until the user responds or the owning operation is cancelled.
/// Supports three grant scopes: once, conversation/session, and always (persistent).
/// </summary>
public interface ISkillPermissionConsentService
{
    /// <summary>
    /// Raised when a skill execution needs permissions that weren't provided.
    /// UI layers subscribe to this to present consent prompts to the user.
    /// </summary>
    event EventHandler<PermissionConsentRequest>? ConsentRequested;

    /// <summary>
    /// Request consent for missing permissions. Blocks until the user responds or caller cancellation.
    /// Called by the skill executor when elevated permissions are missing.
    /// Checks session and persistent grants before prompting the user.
    /// </summary>
    Task<PermissionConsentResult> RequestConsentAsync(
        PermissionConsentRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Respond to a pending consent request. Called by the UI layer.
    /// </summary>
    /// <param name="requestId">The request ID from <see cref="PermissionConsentRequest"/>.</param>
    /// <param name="approved">Whether the user approved the permissions.</param>
    /// <param name="scope">How long the grant should remain active.</param>
    void Respond(string requestId, bool approved, PermissionGrantScope scope = PermissionGrantScope.Once);

    /// <summary>
    /// Forget every <see cref="PermissionGrantScope.Conversation"/> grant recorded for a
    /// session. Hosts call this when a conversation is closed or deleted so its grants do
    /// not outlive it. Persistent (<see cref="PermissionGrantScope.Always"/>) grants are
    /// untouched. Default implementation is a no-op for services that keep no session state.
    /// </summary>
    /// <param name="sessionId">
    /// The session/conversation id. Pass null to clear the process-wide fallback bucket
    /// used when a conversation grant was made without a session id.
    /// </param>
    void ClearSession(string? sessionId) { }

    /// <summary>
    /// Revoke a single <see cref="PermissionGrantScope.Conversation"/> grant within a
    /// session. Returns true if a grant was removed. Default implementation revokes nothing.
    /// </summary>
    /// <param name="sessionId">The session/conversation id, or null for the process-wide fallback bucket.</param>
    /// <param name="permissionId">The permission to revoke.</param>
    bool RevokeSessionGrant(string? sessionId, string permissionId) => false;
}
