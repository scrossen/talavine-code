namespace SwarmCore.Agent.Runtime;

/// <summary>
/// Owns a conversation's read/execute/append lifecycle within one host service
/// provider (the persistence partition). Hosts serving independent partitions
/// supply independent instances. This is in-process execution ownership, not a
/// replacement for durable objective leases or tool operation journals.
/// </summary>
public sealed class AgentConversationExecutionGate
{
    /// <summary>
    /// Fallback for manually constructed runtimes and host recovery services.
    /// Dependency-injection hosts should share their registered partition instance.
    /// </summary>
    public static AgentConversationExecutionGate Shared { get; } = new();
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public async ValueTask<IAsyncDisposable> AcquireAsync(string conversationId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        Entry entry;
        lock (_sync)
        {
            if (!_entries.TryGetValue(conversationId, out entry!))
                _entries.Add(conversationId, entry = new());
            entry.Users++;
        }
        try { await entry.Gate.WaitAsync(ct).ConfigureAwait(false); }
        catch { Release(conversationId, entry, acquired: false); throw; }
        return new Lease(this, conversationId, entry);
    }

    private void Release(string conversationId, Entry entry, bool acquired)
    {
        lock (_sync)
        {
            if (acquired) entry.Gate.Release();
            if (--entry.Users != 0) return;
            _entries.Remove(conversationId);
            entry.Gate.Dispose();
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int Users;
    }

    private sealed class Lease(AgentConversationExecutionGate owner, string conversationId, Entry entry) : IAsyncDisposable
    {
        private int _released;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release(conversationId, entry, acquired: true);
            return ValueTask.CompletedTask;
        }
    }
}
