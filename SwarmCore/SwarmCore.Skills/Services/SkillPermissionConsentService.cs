using System.Collections.Concurrent;
using SwarmCore.Skills.Abstractions;
using SwarmCore.Skills.Models;
using Microsoft.Extensions.Logging;

namespace SwarmCore.Skills.Services;

/// <summary>
/// Mediates permission consent between the skill executor and the UI layer.
/// Maintains session-scoped grants in memory and delegates persistent grants to
/// <see cref="IPermissionGrantStore"/>. Only prompts the user when the permission
/// has not already been granted at a broader scope.
/// </summary>
public class SkillPermissionConsentService : ISkillPermissionConsentService
{
    /// <summary>
    /// Bucket used for <see cref="PermissionGrantScope.Conversation"/> grants made by a
    /// caller that supplied no session id. The user answered "this conversation", so the
    /// grant must be remembered somewhere; with no conversation to pin it to, the closest
    /// honest reading is "for the rest of this process". Previously such grants were
    /// silently dropped and the user was re-prompted on every call.
    /// </summary>
    private const string ProcessWideSessionKey = "\0process";

    private readonly ConcurrentDictionary<string, TaskCompletionSource<PermissionConsentResult>> _pending = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _sessionGrants = new(StringComparer.Ordinal);
    private readonly IPermissionGrantStore _grantStore;
    private readonly ILogger<SkillPermissionConsentService> _logger;
    private readonly IPermissionConsentPolicy[] _policies;

    public SkillPermissionConsentService(
        IPermissionGrantStore grantStore,
        ILogger<SkillPermissionConsentService> logger,
        IEnumerable<IPermissionConsentPolicy>? policies = null)
    {
        _grantStore = grantStore;
        _logger = logger;
        _policies = policies?.ToArray() ?? [];
    }

    public event EventHandler<PermissionConsentRequest>? ConsentRequested;

    public async Task<PermissionConsentResult> RequestConsentAsync(
        PermissionConsentRequest request,
        CancellationToken ct = default)
    {
        foreach (var policy in _policies)
        {
            var decision = await policy.EvaluateAsync(request, ct);
            if (decision is not null) return decision;
        }
        // Check if all missing permissions are already covered by session or persistent grants
        var stillMissing = await FilterAlreadyGrantedAsync(request.SessionId, request.MissingPermissions, ct);
        if (stillMissing.Count == 0)
        {
            _logger.LogDebug(
                "All permissions for {SkillId}.{Function} already granted (session or persistent)",
                request.SkillId, request.FunctionName);
            return new PermissionConsentResult { Approved = true, Scope = PermissionGrantScope.Once, AlreadyGranted = true };
        }

        // Narrow the request to only the truly missing permissions
        request = request with { MissingPermissions = stillMissing };

        var tcs = new TaskCompletionSource<PermissionConsentResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.RequestId] = tcs;

        try
        {
            _logger.LogInformation(
                "Requesting user consent for {SkillId}.{Function} — missing: {Permissions}",
                request.SkillId,
                request.FunctionName,
                string.Join(", ", request.MissingPermissions.Select(p => p.Name)));

            ConsentRequested?.Invoke(this, request);

            // The request now has a durable reconnectable identity in the
            // agent interaction ledger. Do not turn a client detachment or an
            // arbitrary wall-clock delay into a denial; only owner/caller
            // cancellation ends the wait.
            var result = await tcs.Task.WaitAsync(ct);

            // Store the scope the user selected. Existing durable authority is
            // authority; do not silently convert it into another prompt later.
            if (result.Approved)
            {
                var permissionIds = request.MissingPermissions
                    .Select(p => p.PermissionId)
                    .ToList();
                if (permissionIds.Count > 0)
                {
                    await StoreGrantsAsync(request.SessionId, permissionIds, result.Scope, ct);
                }
            }

            return result;
        }
        finally
        {
            _pending.TryRemove(request.RequestId, out _);
        }
    }

    public void Respond(string requestId, bool approved, PermissionGrantScope scope = PermissionGrantScope.Once)
    {
        if (_pending.TryGetValue(requestId, out var tcs))
        {
            _logger.LogInformation(
                "Permission consent {Result} (scope: {Scope}) for request {RequestId}",
                approved ? "approved" : "denied", scope, requestId);
            tcs.TrySetResult(new PermissionConsentResult { Approved = approved, Scope = scope });
        }
        else
        {
            _logger.LogWarning(
                "No pending consent request found for {RequestId} — may have timed out",
                requestId);
        }
    }

    public void ClearSession(string? sessionId)
    {
        var key = SessionKey(sessionId);
        if (_sessionGrants.TryRemove(key, out var removed))
        {
            _logger.LogInformation(
                "Cleared {Count} conversation-scoped grant(s) for session {SessionId}",
                removed.Count, sessionId ?? "(process-wide)");
        }
    }

    public bool RevokeSessionGrant(string? sessionId, string permissionId)
    {
        if (string.IsNullOrWhiteSpace(permissionId)) return false;
        if (!_sessionGrants.TryGetValue(SessionKey(sessionId), out var sessionSet)) return false;

        bool removed;
        lock (sessionSet)
        {
            removed = sessionSet.Remove(permissionId);
        }

        if (removed)
        {
            _logger.LogInformation(
                "Revoked conversation-scoped grant {Permission} for session {SessionId}",
                permissionId, sessionId ?? "(process-wide)");
        }

        return removed;
    }

    private static string SessionKey(string? sessionId) =>
        string.IsNullOrWhiteSpace(sessionId) ? ProcessWideSessionKey : sessionId;

    private async Task<IReadOnlyList<SkillPermission>> FilterAlreadyGrantedAsync(
        string? sessionId,
        IReadOnlyList<SkillPermission> permissions,
        CancellationToken ct)
    {
        // Check persistent grants
        var persistent = await GetPersistentGrantsCachedAsync(ct);

        // Check session grants. A caller with a session id sees its own grants plus
        // anything granted process-wide; a caller with none sees the process-wide bucket.
        _sessionGrants.TryGetValue(SessionKey(sessionId), out var sessionSet);
        _sessionGrants.TryGetValue(ProcessWideSessionKey, out var processSet);

        return permissions
            .Where(p =>
                !persistent.Contains(p.PermissionId) &&
                !ContainsLocked(sessionSet, p.PermissionId) &&
                !ContainsLocked(processSet, p.PermissionId))
            .ToList();
    }

    private static bool ContainsLocked(HashSet<string>? set, string permissionId)
    {
        if (set is null) return false;
        lock (set)
        {
            return set.Contains(permissionId);
        }
    }

    private async Task StoreGrantsAsync(
        string? sessionId,
        List<string> permissionIds,
        PermissionGrantScope scope,
        CancellationToken ct)
    {
        switch (scope)
        {
            case PermissionGrantScope.Conversation:
                var key = SessionKey(sessionId);
                var sessionSet = _sessionGrants.GetOrAdd(key, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                lock (sessionSet)
                {
                    foreach (var id in permissionIds)
                        sessionSet.Add(id);
                }
                _logger.LogInformation(
                    "Stored conversation-scoped grants for session {SessionId}: {Permissions}",
                    sessionId ?? "(process-wide — no session id supplied)", string.Join(", ", permissionIds));
                break;

            case PermissionGrantScope.Always:
                await _grantStore.GrantAsync(permissionIds, ct);
                _logger.LogInformation(
                    "Stored persistent grants: {Permissions}",
                    string.Join(", ", permissionIds));
                break;

            // Once — nothing to store
        }
    }

    private async Task<IReadOnlySet<string>> GetPersistentGrantsCachedAsync(CancellationToken ct)
    {
        // This service is shared by hosts with different scopes. Keeping this cache here would
        // let one tenant's grants satisfy another tenant's request. A single store read per skill
        // invocation is cheap; a host with a single profile may cache safely in its own store.
        return await _grantStore.GetGrantedPermissionsAsync(ct);
    }
}
