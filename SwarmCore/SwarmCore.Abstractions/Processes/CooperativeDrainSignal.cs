namespace SwarmCore.Abstractions.Processes;

/// <summary>
/// Stops admission of new work at a durable boundary without cancelling work
/// already in flight. Once requested, a drain cannot be withdrawn.
/// </summary>
public sealed class CooperativeDrainSignal(Func<bool>? readRequest = null)
{
    private static readonly AsyncLocal<CooperativeDrainSignal?> Ambient = new();
    private int _requested;

    /// <summary>
    /// The drain governing the current asynchronous flow, entered by the
    /// flow's owner for nested work whose contract cannot carry one. Work
    /// made of several independently durable steps checks it before
    /// admitting each further step; it never cancels the running step.
    /// </summary>
    public static CooperativeDrainSignal? Current => Ambient.Value;

    public bool IsRequested
    {
        get
        {
            if (Volatile.Read(ref _requested) == 0 && readRequest?.Invoke() == true)
                Request();
            return Volatile.Read(ref _requested) != 0;
        }
    }

    public void Request() => Interlocked.Exchange(ref _requested, 1);

    /// <summary>
    /// Makes <paramref name="signal"/> (no drain when null) the
    /// <see cref="Current"/> drain until the returned scope is disposed.
    /// </summary>
    public static IDisposable Enter(CooperativeDrainSignal? signal)
    {
        var previous = Ambient.Value;
        Ambient.Value = signal;
        return new Scope(previous);
    }

    private sealed class Scope(CooperativeDrainSignal? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Ambient.Value = previous;
        }
    }
}
