using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Catalog;

namespace SwarmCore.Agent.ToolUseEngine;

/// <summary>Queued calls remain individually cancellable without starting a silence watchdog.</summary>
internal sealed class QueuedToolProgress(IAgentToolProgressBroker? broker, CancellationToken parent) : IDisposable
{
    private readonly Dictionary<string, (CancellationTokenSource Stop, IDisposable? Registration)> _calls = new(StringComparer.Ordinal);
    public CancellationToken Register(ToolUsePart call)
    {
        if (_calls.TryGetValue(call.Id, out var existing)) return existing.Stop.Token;
        var stop = CancellationTokenSource.CreateLinkedTokenSource(parent);
        var registration = broker?.Track(call.Id, call.Name, stop, Timeout.InfiniteTimeSpan);
        _calls.Add(call.Id, (stop, registration));
        broker?.Report(call.Id, "Queued — waiting for its execution slot or evidence.");
        return stop.Token;
    }
    public CancellationToken Token(string id) => _calls[id].Stop.Token;
    public void Dispose()
    {
        foreach (var call in _calls.Values) { call.Registration?.Dispose(); call.Stop.Dispose(); }
    }
}
