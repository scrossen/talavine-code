using System.Collections.Concurrent;
using SwarmCore.Coding.Interaction;

namespace SwarmCode.Hosting;

/// <summary>A question waiting for the person, published to the runner of its conversation.</summary>
public sealed record PendingUserQuestion(string RequestId, string ConversationId, UserQuestionRequest Request);

/// <summary>
/// Question broker for interactive hosts: a request is published to subscribers (the session renders a
/// question card) and the tool waits for <see cref="Answer"/> or <see cref="Decline"/>. The turn's token
/// cancels the wait and drops the pending entry, so a stopped turn leaves no question hanging.
/// <para>
/// Nobody watches an agent's session (<see cref="WorkspaceSessionInfo.ParentSessionId"/>), a conversation a
/// host marked <see cref="Unattended"/> (a scheduled run), or a broker with no subscriber: those get
/// <see cref="UserAnswer.NoOne"/> at once instead of a wait that only a cancel would end.
/// </para>
/// </summary>
public sealed class UiUserQuestionBroker : IUserQuestionBroker
{
    private readonly WorkspaceSessionCatalog? _sessions;
    private readonly ConcurrentDictionary<string, (string ConversationId, TaskCompletionSource<UserAnswer> Answer)> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _unattended = new(StringComparer.Ordinal);
    private readonly List<Action<PendingUserQuestion>> _subscribers = [];
    private readonly object _gate = new();

    public UiUserQuestionBroker(WorkspaceSessionCatalog? sessions = null) => _sessions = sessions;

    public IReadOnlyCollection<string> PendingRequestIds => _pending.Keys.ToList();

    public IDisposable Subscribe(Action<PendingUserQuestion> onQuestion)
    {
        lock (_gate) _subscribers.Add(onQuestion);
        return new Unsubscriber(this, onQuestion);
    }

    /// <summary>Resolves a pending question with what the person entered; false when it is no longer pending.</summary>
    public bool Answer(string requestId, UserAnswer answer)
    {
        if (!_pending.TryRemove(requestId, out var pending)) return false;
        return pending.Answer.TrySetResult(answer);
    }

    public bool Answer(string requestId, string? text, IReadOnlyList<string>? selected) => Answer(requestId, UserAnswer.From(text, selected));

    /// <summary>The person skipped the question; the model is told to decide itself.</summary>
    public bool Decline(string requestId) => Answer(requestId, UserAnswer.Declined);

    /// <summary>Declines every pending question of a conversation (transcript cleared, turn stopped, session deleted).</summary>
    public void ClearConversation(string conversationId)
    {
        foreach (var (id, pending) in _pending.ToArray())
            if (string.Equals(pending.ConversationId, conversationId, StringComparison.Ordinal)) Decline(id);
    }

    /// <summary>
    /// Marks a conversation as having no one to answer for the lifetime of the returned scope (a scheduled run's turn):
    /// questions asked meanwhile get <see cref="UserAnswer.NoOne"/> at once.
    /// </summary>
    public IDisposable Unattended(string conversationId)
    {
        _unattended.AddOrUpdate(conversationId, 1, (_, count) => count + 1);
        return new UnattendedScope(this, conversationId);
    }

    public bool IsUnattended(string conversationId) => _unattended.TryGetValue(conversationId, out var count) && count > 0;

    public async Task<UserAnswer> AskAsync(UserQuestionRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (IsUnattended(request.ConversationId)) return UserAnswer.NoOne;
        if (_sessions is not null)
        {
            WorkspaceSessionInfo? session = null;
            try { session = await _sessions.GetAsync(request.ConversationId, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* an unreadable catalog does not decide who is watching */ }
            if (session?.ParentSessionId is not null) return UserAnswer.NoOne;
        }

        Action<PendingUserQuestion>[] subscribers;
        lock (_gate) subscribers = _subscribers.ToArray();
        if (subscribers.Length == 0) return UserAnswer.NoOne;

        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<UserAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = (request.ConversationId, tcs);
        var pending = new PendingUserQuestion(id, request.ConversationId, request);
        var seen = 0;
        foreach (var subscriber in subscribers)
        {
            try { subscriber(pending); seen++; } catch { /* a broken subscriber must not block the turn */ }
        }
        if (seen == 0)
        {
            _pending.TryRemove(id, out _);
            return UserAnswer.NoOne;
        }

        using var registration = ct.Register(() =>
        {
            if (_pending.TryRemove(id, out var entry)) entry.Answer.TrySetCanceled(ct);
        });
        return await tcs.Task.ConfigureAwait(false);
    }

    private sealed class Unsubscriber(UiUserQuestionBroker owner, Action<PendingUserQuestion> handler) : IDisposable
    {
        public void Dispose() { lock (owner._gate) owner._subscribers.Remove(handler); }
    }

    private sealed class UnattendedScope(UiUserQuestionBroker owner, string conversationId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            while (owner._unattended.TryGetValue(conversationId, out var count))
            {
                if (count <= 1 ? owner._unattended.TryRemove(new KeyValuePair<string, int>(conversationId, count)) : owner._unattended.TryUpdate(conversationId, count - 1, count))
                    return;
            }
        }
    }
}
