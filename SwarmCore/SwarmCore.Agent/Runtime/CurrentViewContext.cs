namespace SwarmCore.Agent.Runtime;

/// <summary>
/// Thread-safe in-memory implementation of <see cref="ICurrentViewContext"/>.
/// Registered as a singleton; state lives for the app session.
/// </summary>
public sealed class CurrentViewContext : ICurrentViewContext
{
    private readonly object _gate = new();
    private CurrentViewSnapshot? _current;

    public CurrentViewSnapshot? Current
    {
        get { lock (_gate) return _current; }
    }

    public event Action<CurrentViewSnapshot?>? Changed;

    public void Set(CurrentViewSnapshot snapshot)
    {
        lock (_gate) _current = snapshot;
        Changed?.Invoke(snapshot);
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_current is null) return;
            _current = null;
        }
        Changed?.Invoke(null);
    }

    public bool ClearIf(CurrentViewSnapshot expected)
    {
        ArgumentNullException.ThrowIfNull(expected);

        lock (_gate)
        {
            if (!ReferenceEquals(_current, expected))
            {
                return false;
            }

            _current = null;
        }

        Changed?.Invoke(null);
        return true;
    }
}
