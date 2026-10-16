using SwarmCore.Skills.Abstractions;
using SwarmCore.Skills.Models;
using SwarmCore.Agent.Interactions;

namespace SwarmCore.Agent.Runtime;

/// <summary>
/// Thin wrapper around <see cref="ISkillPermissionConsentService"/> that
/// exposes a subscribe/dispose pattern matching our turn lifecycle.
/// </summary>
public sealed class PermissionBroker : IPermissionBroker
{
    private readonly ISkillPermissionConsentService _consent;
    private readonly IInteractionRequestService? _interactions;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, InteractionRequestCreate> _projectionOutbox = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, InteractionRequestCompletion> _localWinners = new(StringComparer.Ordinal);
    // Live winners whose durable interaction record has not been completed yet.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _unpersistedWinners = new(StringComparer.Ordinal);

    public PermissionBroker(
        ISkillPermissionConsentService consent,
        IInteractionRequestService? interactions = null)
    {
        _consent = consent;
        _interactions = interactions;
    }

    public IDisposable Subscribe(Action<PendingPermissionRequest> onRequest)
    {
        void Handler(object? sender, PermissionConsentRequest req)
        {
            onRequest(new PendingPermissionRequest(
                RequestId: req.RequestId,
                SkillId: req.SkillId,
                SkillName: req.SkillName,
                FunctionName: req.FunctionName,
                Permissions: req.MissingPermissions.Select(p => p.PermissionId).ToList(),
                PermissionLevels: req.MissingPermissions.Select(p => p.Level).ToList(),
                SessionId: req.SessionId));
        }
        _consent.ConsentRequested += Handler;
        return new Unsubscriber(() => _consent.ConsentRequested -= Handler);
    }

    /// <summary>
    /// Relays an answer to the live waiter without touching the durable interaction ledger.
    /// The exact in-memory winner still applies, and the answer is recorded for
    /// <see cref="ReconcileInteractionsAsync"/> to persist. Answer paths that can await use
    /// <see cref="RespondAsync"/>, which commits the durable result first.
    /// </summary>
    public void Respond(string requestId, bool approved, PermissionGrantScope scope)
    {
        if (_interactions is not null) ClaimLocalWinner(requestId, Completion(approved, scope));
        _consent.Respond(requestId, approved, scope);
    }

    public Task RespondAsync(string requestId, bool approved, PermissionGrantScope scope, CancellationToken ct = default)
        => RespondWithCompletionAsync(requestId, approved, scope, Completion(approved, scope), ct);

    public async Task RespondWithCompletionAsync(
        string requestId,
        bool approved,
        PermissionGrantScope scope,
        InteractionRequestCompletion completion,
        CancellationToken ct = default)
    {
        completion = InteractionCompletionCanonicalizer.Canonicalize(completion);
        // The durable result is authoritative when available: it is committed before
        // the live waiter is woken, so opposing desktop/mobile answers cannot split the
        // persisted and executing outcomes, and retrying the exact response after a
        // failed live relay replays it to the waiter. During a total interaction-store
        // outage an exact in-memory winner keeps the live waiter moving, and the
        // registered projection metadata repairs the durable record later.
        if (_interactions is not null)
        {
            InteractionRequestSnapshot? interaction = null;
            try
            {
                await TryProjectAsync(requestId, ct).ConfigureAwait(false);
                interaction = await _interactions.GetByCallerIdAsync($"permission:{requestId}", ct).ConfigureAwait(false);
                if (interaction is not null)
                {
                    await _interactions.CompleteAsync(interaction.InteractionId, completion, ct).ConfigureAwait(false);
                    _localWinners[requestId] = completion;
                    _projectionOutbox.TryRemove(requestId, out _);
                    _unpersistedWinners.TryRemove(requestId, out _);
                }
            }
            catch (InvalidOperationException)
            {
                // A durable opposing answer is authoritative, not an outage.
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                interaction = null;
            }

            if (interaction is null) ClaimLocalWinner(requestId, completion);
        }
        _consent.Respond(requestId, approved, scope);
    }

    public void RegisterInteractionProjection(InteractionRequestCreate request)
        => _projectionOutbox[request.OwnerId] = request;

    public async Task ReconcileInteractionsAsync(CancellationToken ct = default)
    {
        if (_interactions is null) return;
        foreach (var requestId in _projectionOutbox.Keys)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var interaction = await _interactions.CreateAsync(_projectionOutbox[requestId], ct);
                if (_localWinners.TryGetValue(requestId, out var winner))
                {
                    await _interactions.CompleteAsync(interaction.InteractionId, winner, ct);
                    _unpersistedWinners.TryRemove(requestId, out _);
                }
                _projectionOutbox.TryRemove(requestId, out _);
            }
            catch when (!ct.IsCancellationRequested)
            {
                // Durable stores may be temporarily unavailable. The metadata
                // remains an in-memory outbox and the live user-requested work
                // is not converted into another gate.
            }
        }
        foreach (var requestId in _unpersistedWinners.Keys)
        {
            ct.ThrowIfCancellationRequested();
            // A still-unprojected request is completed when its projection is created above.
            if (_projectionOutbox.ContainsKey(requestId) || !_localWinners.TryGetValue(requestId, out var winner)) continue;
            try
            {
                var interaction = await _interactions.GetByCallerIdAsync($"permission:{requestId}", ct);
                if (interaction is not null)
                    await _interactions.CompleteAsync(interaction.InteractionId, winner, ct);
                _unpersistedWinners.TryRemove(requestId, out _);
            }
            catch (InvalidOperationException)
            {
                // A durable opposing answer won; there is nothing left to repair.
                _unpersistedWinners.TryRemove(requestId, out _);
            }
            catch when (!ct.IsCancellationRequested)
            {
                // Still unavailable; the next reconciliation retries.
            }
        }
    }

    private async Task TryProjectAsync(string requestId, CancellationToken ct)
    {
        if (_interactions is null || !_projectionOutbox.TryGetValue(requestId, out var request)) return;
        try
        {
            var interaction = await _interactions.CreateAsync(request, ct).ConfigureAwait(false);
            if (_localWinners.TryGetValue(requestId, out var winner))
                await _interactions.CompleteAsync(interaction.InteractionId, winner, ct).ConfigureAwait(false);
        }
        catch when (!ct.IsCancellationRequested)
        {
            // RespondWithCompletionAsync falls back to the exact in-memory winner.
        }
    }

    /// <summary>
    /// Records the exact live winner when no durable result could be committed, and
    /// queues it for durable repair. A different answer for the same request is refused.
    /// </summary>
    private void ClaimLocalWinner(string requestId, InteractionRequestCompletion completion)
    {
        completion = InteractionCompletionCanonicalizer.Canonicalize(completion);
        var winner = _localWinners.GetOrAdd(requestId, completion);
        if (!InteractionCompletionCanonicalizer.Equivalent(winner, completion))
            throw new InvalidOperationException($"Permission '{requestId}' already has a different live winner.");
        if (ReferenceEquals(winner, completion)) _unpersistedWinners.TryAdd(requestId, 0);
    }

    private static InteractionRequestCompletion Completion(bool approved, PermissionGrantScope scope) => new()
    {
        Kind = approved ? InteractionResolutionKind.Approved : InteractionResolutionKind.Denied,
        Actor = "user",
        ChoiceId = approved ? $"approve:{scope}" : "deny",
    };

    public Task<PermissionConsentResult> RequestAsync(PermissionConsentRequest request, CancellationToken ct = default)
        => _consent.RequestConsentAsync(request, ct);

    private sealed class Unsubscriber : IDisposable
    {
        private Action? _dispose;
        public Unsubscriber(Action dispose) { _dispose = dispose; }
        public void Dispose()
        {
            var d = Interlocked.Exchange(ref _dispose, null);
            d?.Invoke();
        }
    }
}
