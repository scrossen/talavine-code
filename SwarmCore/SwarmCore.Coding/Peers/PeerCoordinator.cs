namespace SwarmCore.Coding.Peers;

/// <summary>No work replay, prompt-specific routing, or extra model calls. SwarmSpan owns the durable work ledger.</summary>
public sealed class PeerCoordinator(IPeerWorkBridge bridge)
{
    public async Task<IReadOnlyList<PeerCandidate>> DiscoverAsync(PeerRequirements requirements, CancellationToken ct = default)
    {
        PeerRouting.Validate(requirements);
        var peers = await bridge.ListPeersAsync(ct).ConfigureAwait(false);
        using var limit = new SemaphoreSlim(4);
        return await Task.WhenAll(peers.Select(async peer =>
        {
            await limit.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // A stale discovery beacon is not evidence that an authenticated capability call will fail.
                var caps = await bridge.CapabilitiesAsync(peer.DeviceId, ct).ConfigureAwait(false);
                return PeerRouting.Match(peer, caps, requirements, DateTimeOffset.UtcNow);
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException)
            { return new PeerCandidate(peer, null, false, "Capability query failed; check SwarmSpan connectivity and diagnostics."); }
            finally { limit.Release(); }
        })).ConfigureAwait(false);
    }

    public async Task<PeerWorkHandle> DelegateAsync(string conversationId, PeerDelegateInput input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.Goal) || string.IsNullOrWhiteSpace(input.Title)) throw new ArgumentException("Title and complete goal are required.");
        if (input.SuccessCriteria is not { Count: > 0 } || input.SuccessCriteria.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Give observable success criteria for the peer to verify.");
        var requirements = input.Requirements ?? new();
        var candidates = await DiscoverAsync(requirements, ct).ConfigureAwait(false);
        var eligible = candidates.Where(p => p.Matches && (string.IsNullOrWhiteSpace(input.Peer) || p.Peer.DeviceId == input.Peer
            || string.Equals(p.Peer.DisplayName, input.Peer, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (!string.IsNullOrWhiteSpace(input.Peer) && eligible.Length > 1) throw new InvalidOperationException("Peer name is ambiguous; use the device id.");
        if (eligible.Length == 0) throw new InvalidOperationException("No peer meets the requirements. Use peers.list to inspect each device's reason; nothing was assigned.");
        var work = await bridge.ListWorkAsync(ct).ConfigureAwait(false);
        var selected = eligible.OrderBy(p => work.Count(w => w.PeerDeviceId == p.Peer.DeviceId && !w.IsTerminal))
            .ThenBy(p => p.Peer.DeviceId, StringComparer.Ordinal).First();
        var fresh = await bridge.CapabilitiesAsync(selected.Peer.DeviceId, ct).ConfigureAwait(false);
        selected = PeerRouting.Match(selected.Peer, fresh, requirements, DateTimeOffset.UtcNow);
        if (!selected.Matches) throw new InvalidOperationException("The selected peer's capabilities changed: " + selected.Reason);
        // Mutating dispatch is attempted exactly once. A lost response must be reconciled through peers.work.
        return await bridge.AssignAsync(new(selected.Peer.DeviceId, input.Title, input.Goal, input.Context,
            input.SuccessCriteria, input.ExpectedArtifacts ?? [], conversationId, selected.Provider, selected.Model,
            requirements.ExecutionMode, requirements.Job, input.JobArguments ?? []), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PeerWork>> WorkAsync(string conversationId, CancellationToken ct = default)
        => (await bridge.ListWorkAsync(ct).ConfigureAwait(false)).Where(w => w.ConversationId == conversationId).ToArray();

    public async Task<PeerWork> ObserveAsync(string conversationId, PeerWorkInput input, bool wait, bool cancel, CancellationToken ct = default)
    {
        var existing = await bridge.WorkAsync(input.WorkId, ct).ConfigureAwait(false);
        if (existing.ConversationId != conversationId) throw new InvalidOperationException("This work belongs to another conversation.");
        if (cancel) return await bridge.CancelAsync(input.WorkId, ct).ConfigureAwait(false);
        return wait && !existing.IsTerminal
            ? await bridge.WaitAsync(input.WorkId, input.Since ?? existing.UpdatedUtcMs, Math.Clamp(input.TimeoutSeconds, 1, 50), ct).ConfigureAwait(false)
            : existing;
    }
}
