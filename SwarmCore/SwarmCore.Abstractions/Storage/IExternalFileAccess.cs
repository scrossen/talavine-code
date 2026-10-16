namespace SwarmCore.Abstractions.Storage;

/// <summary>Host-authorized access to a named external source. No grant means no lease.</summary>
public interface IExternalFileAccess
{
    ExternalFileLease? AcquireReadOnly(string sourceId);
}

/// <summary>Keeps a host's file authorization alive for the complete read operation.</summary>
public sealed class ExternalFileLease : IDisposable
{
    private Action? _release;

    public ExternalFileLease(string path, Action? release = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
        _release = release;
    }

    public string Path { get; }

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
