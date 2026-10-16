namespace SwarmCore.Abstractions.Scheduling;

public sealed record DurableRecipientWork<T>(Guid Id, string Recipient, T Payload);

public interface IDurableRecipientWorkStore<T>
{
    Task<DurableRecipientWork<T>?> ReadAsync(Guid id, CancellationToken ct);
    Task AcknowledgeAsync(DurableRecipientWork<T> work, CancellationToken ct);
}

/// <summary>
/// Delivers persisted work to its exact recipient before acknowledging it.
/// Delivery is at least once: handlers must tolerate replay after a crash or
/// failed acknowledgement. Scheduling and persistence belong to the host.
/// </summary>
public static class DurableRecipientDelivery
{
    public static async Task DeliverAsync<T>(Guid id, IDurableRecipientWorkStore<T> store,
        IReadOnlyDictionary<string, Func<T, CancellationToken, Task>> recipients, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var work = await store.ReadAsync(id, ct).ConfigureAwait(false);
        if (work is null) return;
        if (!recipients.TryGetValue(work.Recipient, out var execute))
            throw new InvalidOperationException($"Cleanup recipient '{work.Recipient}' is unavailable.");
        await execute(work.Payload, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        await store.AcknowledgeAsync(work, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Allows a recipient to defer work whose prerequisites are unavailable without
    /// throwing or acknowledging it. A later delivery rereads the durable payload.
    /// </summary>
    public static async Task<bool> TryDeliverAsync<T>(Guid id, IDurableRecipientWorkStore<T> store,
        IReadOnlyDictionary<string, Func<T, CancellationToken, Task<bool>>> recipients, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var work = await store.ReadAsync(id, ct).ConfigureAwait(false);
        if (work is null) return false;
        if (!recipients.TryGetValue(work.Recipient, out var execute))
            throw new InvalidOperationException($"Cleanup recipient '{work.Recipient}' is unavailable.");
        var delivered = await execute(work.Payload, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!delivered) return false;
        await store.AcknowledgeAsync(work, ct).ConfigureAwait(false);
        return true;
    }
}
