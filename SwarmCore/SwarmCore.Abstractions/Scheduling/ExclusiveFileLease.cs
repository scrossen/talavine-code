using SwarmCore.Abstractions.Storage;

namespace SwarmCore.Abstractions.Scheduling;

/// <summary>Nonblocking process exclusion. Contention defers durable work; it never expires an owner.</summary>
public static class ExclusiveFileLease
{
    public static IDisposable? TryAcquire(string path)
    {
        try
        {
            return new FileStream(Path.GetFullPath(path), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.None);
        }
        catch (IOException error) when (TransientFileLock.IsSharingViolation(error))
        {
            return null;
        }
    }
}
