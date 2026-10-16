using System.Threading.Channels;

namespace SwarmCore.Abstractions.LlmRouting;

public sealed record ChatSteeringUpdate(string Id, string Message);
public enum ChatSteeringDelivery { ProviderQueued, Applied, Deferred }
public sealed record ChatSteeringDeliveryEvent(string Id, ChatSteeringDelivery Delivery,
    string? ProviderSteeringId = null, string? ResponseId = null, string? Reason = null, string? Message = null) : ToolUseStreamEvent;

/// <summary>Only the host's authenticated durable steering coordinator supplies this channel.</summary>
public interface IChatSteeringSource
{
    bool TryTake(out ChatSteeringUpdate update);
    ValueTask WaitForUpdateAsync(CancellationToken ct);
}

/// <summary>One turn owns one native transport, including all client-tool continuations.
/// Disposal terminates connection-local work. Provider acknowledgment is not durable host application.</summary>
public interface IChatContextBoundaryTransport : IAsyncDisposable
{
    /// <summary>Atomically stop taking new steering only after the current response ended
    /// and all sent steering has been reconciled. False preserves the live connection.</summary>
    ValueTask<bool> TryDetachAsync(CancellationToken ct);
}

public sealed class ChatSteeringSession(IChatSteeringSource source, CancellationToken lifetimeToken = default) : IAsyncDisposable
{
    public CancellationToken LifetimeToken { get; } = lifetimeToken;
    public IChatSteeringSource Source { get; } = source;
    public IAsyncDisposable? Transport { get; set; }
    public async ValueTask DisposeAsync()
    {
        if (Transport is { } transport) await transport.DisposeAsync().ConfigureAwait(false);
        Transport = null;
    }
}
