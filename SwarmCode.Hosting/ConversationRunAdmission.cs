namespace SwarmCode.Hosting;

/// <summary>Process-local restart admission. Existing turns finish; an idle restart excludes new turns atomically.</summary>
public sealed class ConversationRunAdmission
{
    public static ConversationRunAdmission Shared { get; } = new();
    private readonly object _gate = new();
    private int _active;
    private long _generation;
    private bool _blocked;
    public event Action? Available;

    public IDisposable? TryEnterTurn()
    {
        lock (_gate)
        {
            if (_blocked) return null;
            _active++;
            return new Release(() => { lock (_gate) _active--; });
        }
    }

    public RestartAdmission? TryQuiesce()
    {
        lock (_gate)
        {
            if (_active != 0 || _blocked) return null;
            var generation = ++_generation;
            // The owner releases admission after the detached helper actually exits. A wall
            // clock deadline could reopen admission while that helper is still waiting.
            _blocked = true;
            return new RestartAdmission(() =>
            {
                lock (_gate) if (_generation == generation) _blocked = false;
                // Never call runner code while holding the admission lock.
                foreach (var listener in Available?.GetInvocationList() ?? [])
                    try { ((Action)listener)(); } catch { /* One disposed host must not strand another runner. */ }
            });
        }
    }

    public sealed class RestartAdmission(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
    private sealed class Release(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
