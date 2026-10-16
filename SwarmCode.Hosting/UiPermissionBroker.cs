using System.Collections.Concurrent;
using SwarmCore.Agent.Runtime;
using SwarmCore.Skills.Models;

namespace SwarmCode.Hosting;

/// <summary>
/// Permission broker for interactive hosts: a request is published to subscribers
/// (the chat surface renders an approval card) and the engine waits for a decision.
/// With <see cref="AutoApprove"/> set, every request is approved immediately, which is
/// the default for user-requested coding turns.
/// <para>
/// A <see cref="PermissionGrantScope.Conversation"/> answer is remembered here, in memory,
/// for that conversation (the request's session id) and that skill: a later request from
/// the same conversation whose missing permissions are all granted is approved without a
/// prompt. Grants are dropped by <see cref="RevokeGrant"/> (the receipt's Change),
/// <see cref="ClearConversation"/> (/clear, session delete) and process exit. A request
/// with no session id is never covered by, and never creates, a conversation grant.
/// </para>
/// </summary>
public sealed class UiPermissionBroker : IPermissionBroker
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<(bool Approved, PermissionGrantScope Scope)>> _pending = new();
    private readonly ConcurrentDictionary<string, PermissionConsentRequest> _pendingRequests = new();
    private readonly List<Action<PendingPermissionRequest>> _subscribers = [];
    private readonly object _gate = new();

    // conversation id -> grant keys ("skill\u001fpermission"); request id -> the grant it created
    private readonly Dictionary<string, HashSet<string>> _grants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Conversation, string[] Keys)> _grantsByRequest = new(StringComparer.Ordinal);

    public bool AutoApprove { get; set; }

    public IReadOnlyCollection<string> PendingRequestIds => _pending.Keys.ToList();

    public IDisposable Subscribe(Action<PendingPermissionRequest> onRequest)
    {
        lock (_gate) _subscribers.Add(onRequest);
        return new Unsubscriber(this, onRequest);
    }

    public void Respond(string requestId, bool approved, PermissionGrantScope scope)
    {
        if (!_pending.TryRemove(requestId, out var tcs)) return;
        if (_pendingRequests.TryRemove(requestId, out var request)
            && approved && scope == PermissionGrantScope.Conversation
            && !string.IsNullOrWhiteSpace(request.SessionId))
        {
            var keys = GrantKeys(request);
            lock (_gate)
            {
                if (!_grants.TryGetValue(request.SessionId, out var set))
                    _grants[request.SessionId] = set = new HashSet<string>(StringComparer.Ordinal);
                set.UnionWith(keys);
                _grantsByRequest[requestId] = (request.SessionId, keys);
            }
        }
        tcs.TrySetResult((approved, scope));
    }

    /// <summary>True when the conversation already holds a grant covering every permission the request names.</summary>
    public bool IsGranted(PermissionConsentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SessionId) || request.MissingPermissions.Count == 0) return false;
        lock (_gate)
        {
            return _grants.TryGetValue(request.SessionId, out var set) && GrantKeys(request).All(set.Contains);
        }
    }

    /// <summary>Withdraws the conversation grant a request created. Returns false when it created none (or it is already gone).</summary>
    public bool RevokeGrant(string requestId)
    {
        lock (_gate)
        {
            if (!_grantsByRequest.Remove(requestId, out var grant)) return false;
            if (_grants.TryGetValue(grant.Conversation, out var set))
            {
                // Another request may have granted the same key; keep keys still claimed by one.
                var stillClaimed = _grantsByRequest.Values
                    .Where(g => g.Conversation == grant.Conversation)
                    .SelectMany(g => g.Keys)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var key in grant.Keys)
                    if (!stillClaimed.Contains(key)) set.Remove(key);
                if (set.Count == 0) _grants.Remove(grant.Conversation);
            }
            return true;
        }
    }

    /// <summary>Forgets every grant of a conversation (transcript cleared or session deleted).</summary>
    public void ClearConversation(string conversationId)
    {
        lock (_gate)
        {
            _grants.Remove(conversationId);
            foreach (var id in _grantsByRequest.Where(g => g.Value.Conversation == conversationId).Select(g => g.Key).ToList())
                _grantsByRequest.Remove(id);
        }
    }

    public async Task<PermissionConsentResult> RequestAsync(PermissionConsentRequest request, CancellationToken ct = default)
    {
        if (AutoApprove)
            return new PermissionConsentResult { Approved = true, Scope = PermissionGrantScope.Once };
        if (IsGranted(request))
            return new PermissionConsentResult { AlreadyGranted = true, Approved = true, Scope = PermissionGrantScope.Conversation };

        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<(bool, PermissionGrantScope)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        _pendingRequests[id] = request;
        var pending = new PendingPermissionRequest(
            id,
            request.SkillId,
            request.SkillName,
            request.FunctionName,
            request.MissingPermissions.Select(p => p.Name).ToList(),
            request.MissingPermissions.Select(p => p.Level).ToList(),
            request.SessionId);

        Action<PendingPermissionRequest>[] subscribers;
        lock (_gate) subscribers = _subscribers.ToArray();
        if (subscribers.Length == 0)
        {
            _pending.TryRemove(id, out _);
            _pendingRequests.TryRemove(id, out _);
            return new PermissionConsentResult { Approved = false, Scope = PermissionGrantScope.Once };
        }
        foreach (var s in subscribers)
        {
            try { s(pending); } catch { /* a broken subscriber must not block the turn */ }
        }

        using var registration = ct.Register(() =>
        {
            _pendingRequests.TryRemove(id, out _);
            if (_pending.TryRemove(id, out var t)) t.TrySetCanceled(ct);
        });
        var (approved, scope) = await tcs.Task.ConfigureAwait(false);
        return new PermissionConsentResult { Approved = approved, Scope = scope };
    }

    private static string[] GrantKeys(PermissionConsentRequest request)
        => request.MissingPermissions
            .Select(p => request.SkillId + "\u001f" + (string.IsNullOrWhiteSpace(p.PermissionId) ? p.Name : p.PermissionId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private sealed class Unsubscriber(UiPermissionBroker owner, Action<PendingPermissionRequest> handler) : IDisposable
    {
        public void Dispose() { lock (owner._gate) owner._subscribers.Remove(handler); }
    }
}
