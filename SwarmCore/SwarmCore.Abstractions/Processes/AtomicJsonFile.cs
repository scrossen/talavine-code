using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SwarmCore.Abstractions.Processes;

/// <summary>
/// Shared same-directory atomic JSON primitive for process-ownership markers
/// and heartbeats. Readers see either the previous complete document or the new
/// complete document; never an in-place partial write.
/// </summary>
public static class AtomicJsonFile
{
    public static void Write<T>(
        string path,
        T value,
        JsonSerializerOptions? options = null,
        int maximumAttempts = 8)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        maximumAttempts = Math.Clamp(maximumAttempts, 1, 32);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var tempPath = fullPath + $".tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(value, options));
            using var access = AcquireAccess(fullPath);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (!OperatingSystem.IsWindows())
                    {
                        // Same-directory rename atomically publishes both new
                        // and replacement documents on Unix. An open reader
                        // retains its old inode; new readers open the new one.
                        File.Move(tempPath, fullPath, overwrite: true);
                    }
                    else if (File.Exists(fullPath))
                    {
                        File.Replace(tempPath, fullPath, destinationBackupFileName: null);
                    }
                    else
                    {
                        File.Move(tempPath, fullPath);
                    }
                    return;
                }
                catch (Exception ex) when (
                    attempt < maximumAttempts
                    && ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(attempt * 5));
                }
            }
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>
    /// Reads a complete committed document, preserving file/JSON errors for
    /// callers that must distinguish an absent registry from a corrupt one.
    /// </summary>
    public static T? Read<T>(string path, JsonSerializerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var access = AcquireAccess(path);
        return ReadDocument<T>(path, options);
    }

    public static bool TryRead<T>(
        string path,
        out T? value,
        JsonSerializerOptions? options = null,
        int maximumAttempts = 8)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        maximumAttempts = Math.Clamp(maximumAttempts, 1, 32);
        value = default;
        IDisposable? access;
        try { access = AcquireAccess(path); }
        catch { return false; }
        using var lease = access;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                value = ReadDocument<T>(path, options);
                return value is not null;
            }
            catch (FileNotFoundException) when (attempt < maximumAttempts)
            {
                // Retain compatibility with an older external writer that does
                // not yet participate in the shared access boundary.
                Thread.Sleep(TimeSpan.FromMilliseconds(attempt));
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
            catch (Exception ex) when (
                attempt < maximumAttempts
                && ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(attempt));
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private static T? ReadDocument<T>(string path, JsonSerializerOptions? options)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<T>(stream, options);
    }

    private static IDisposable? AcquireAccess(string path)
    {
        // Unix rename already provides the complete-document boundary. Named
        // mutexes use a global /tmp/.dotnet/shm directory that is unavailable
        // inside the macOS App Sandbox, including inherited helper sandboxes.
        if (!OperatingSystem.IsWindows()) return null;

        // File.Replace may briefly remove the lookup name on Windows. Readers
        // must wait for the actual replacement, not infer absence after racing
        // it a fixed number of times. A named mutex also coordinates helpers in
        // other processes and is released by the OS if an owner exits/crashes.
        // No lock files or permanent per-path in-memory entries accumulate.
        var fullPath = Path.GetFullPath(path);
        // Resolve the parent, not the replaceable document: its identity must
        // remain stable before creation and throughout an atomic rename.
        fullPath = Path.Combine(
            PhysicalPathIdentity.Resolve(Path.GetDirectoryName(fullPath)!),
            Path.GetFileName(fullPath));
        fullPath = fullPath.ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath)));
        var mutex = new Mutex(false, "SwarmCore.AtomicJson." + key);
        try
        {
            try { mutex.WaitOne(); }
            catch (AbandonedMutexException)
            {
                // Acquired ownership. The atomic document is still either the
                // old or new version; validation remains the reader's job.
            }
            return new AccessLease(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private sealed class AccessLease(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A subsequent ownership sweep may clean a stranded temp file.
        }
    }
}
