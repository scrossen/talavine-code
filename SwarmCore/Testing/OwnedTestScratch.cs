using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

#if TEST_SCRATCH_HARNESS
namespace SwarmCore.Testing.Harness;
#else
namespace SwarmCore.Testing;
#endif

/// <summary>Test-only source link: process identity and an OS lease own scratch, never its age.</summary>
public static class OwnedTestScratch
{
    private static readonly TimeSpan DeleteRetryWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(50);
    private sealed record Owner(int ProcessId, long StartedUtcTicks, Owner[]? Children = null);
    private static readonly Dictionary<string, Owner> RetainedChildren = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static Lazy<string> Root = new(CreateRoot);
    private static FileStream? _lease;
    private static int _initialized;
    public static string BaseDirectory { get; } = Path.Combine(Path.GetTempPath(), "talavine-tests");
    public static string RunRoot => Root.Value;
    public static bool IsCreated => Root.IsValueCreated;
    public static event Action<string>? BeforeDelete;
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
        RecoverAbandonedRuns();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupCurrentRun();
    }

    private static string CreateRoot()
    {
        Directory.CreateDirectory(BaseDirectory);
        RejectLink(BaseDirectory);
        var root = Path.Combine(BaseDirectory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}-{Environment.ProcessId}");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(root);
        else Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _lease = new FileStream(Path.Combine(root, ".owner-lease"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var process = Process.GetCurrentProcess();
        File.WriteAllText(Path.Combine(root, ".owner.json"), JsonSerializer.Serialize(new Owner(process.Id, process.StartTime.ToUniversalTime().Ticks)));
        return root;
    }

    public static bool DeleteDirectory(string path)
    {
        ValidateOwnedPath(path);
        var full = Path.GetFullPath(path);
        lock (RetainedChildren)
            if (RetainedChildren.Any(p => (string.Equals(full, p.Key, PathComparison)
                    || full.StartsWith(p.Key + Path.DirectorySeparatorChar, PathComparison)) && OwnerMayBeAlive(p.Value)))
                throw new InvalidOperationException("Stop the owned test app before deleting its profile.");
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                if (!Directory.Exists(path)) return true;
                BeforeDelete?.Invoke(path);
                DeleteTree(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (Stopwatch.GetElapsedTime(started) >= DeleteRetryWindow)
                {
                    Console.Error.WriteLine($"Test scratch cleanup remains pending: {path} ({ex.GetType().Name}: {ex.Message})");
                    return false;
                }

                // SQLite pool draining and Windows file scanners can release a
                // just-closed database handle slightly after fixture teardown.
                // Retry only this process-owned child; a persistent lock still
                // fails visibly once the small bounded window expires.
                Thread.Sleep(DeleteRetryDelay);
            }
        }
    }

    public static void ValidateOwnedPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.TrimEndingDirectorySeparator(RunRoot);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
            throw new InvalidOperationException("Test cleanup may only remove a child of its own run root.");
        RejectLink(BaseDirectory);
        RejectLink(root);
        for (var parent = Path.GetDirectoryName(full); parent is not null && !string.Equals(parent, root, PathComparison); parent = Path.GetDirectoryName(parent))
            RejectLink(parent);
    }

    /// <summary>Keep an explicitly requested inspection app's data until that exact process exits.</summary>
    public static void RetainDirectoryForProcess(string path, int processId)
    {
        ValidateOwnedPath(path);
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), RunRoot, PathComparison))
            throw new InvalidOperationException("Retained inspection data must be a direct owned child.");
        RejectLink(full);
        using var process = Process.GetProcessById(processId);
        if (process.HasExited) throw new InvalidOperationException("Inspection process has already exited.");
        lock (RetainedChildren)
        {
            RetainedChildren[full] = new Owner(process.Id, process.StartTime.ToUniversalTime().Ticks);
            using var parent = Process.GetCurrentProcess();
            File.WriteAllText(Path.Combine(RunRoot, ".owner.json"), JsonSerializer.Serialize(
                new Owner(parent.Id, parent.StartTime.ToUniversalTime().Ticks, RetainedChildren.Values.ToArray())));
        }
    }

    public static IEnumerable<string> EnumerateFilesWithoutLinks(string directory)
    {
        RejectLink(directory);
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            if (entry is DirectoryInfo)
            {
                foreach (var file in EnumerateFilesWithoutLinks(entry.FullName)) yield return file;
            }
            else yield return entry.FullName;
        }
    }

    private static void DeleteTree(string directory)
    {
        var info = new DirectoryInfo(directory);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(directory); // Delete the link itself, never enumerate its target.
            return;
        }
        foreach (var entry in info.EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo) DeleteTree(entry.FullName);
            else
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) == 0) entry.Attributes &= ~FileAttributes.ReadOnly;
                File.Delete(entry.FullName);
            }
        }
        Directory.Delete(directory);
    }

    /// <summary>Called by the test framework after every collection has completed and before it reports success.</summary>
    public static void FinishRun()
    {
        if (!Root.IsValueCreated) return;
        var root = RunRoot;
        RejectLink(BaseDirectory);
        RejectLink(root);
        BeforeDelete?.Invoke(root);
        Dictionary<string, Owner> retained;
        lock (RetainedChildren)
            retained = RetainedChildren.Where(p => OwnerMayBeAlive(p.Value)).ToDictionary();
        DeletePayload(root, retained.Keys.ToHashSet(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal));
        if (retained.Count > 0)
        {
            using var process = Process.GetCurrentProcess();
            File.WriteAllText(Path.Combine(root, ".owner.json"), JsonSerializer.Serialize(
                new Owner(process.Id, process.StartTime.ToUniversalTime().Ticks, retained.Values.ToArray())));
        }
        _lease?.Dispose();
        if (retained.Count == 0)
        {
            File.Delete(Path.Combine(root, ".owner-lease"));
            File.Delete(Path.Combine(root, ".owner.json"));
            Directory.Delete(root);
        }
        lock (RetainedChildren) RetainedChildren.Clear();
        _lease = null;
        Root = new Lazy<string>(CreateRoot);
    }

    private static void CleanupCurrentRun()
    {
        try { FinishRun(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Test run cleanup remains pending for recovery after process exit: {RunRoot} ({ex.GetType().Name}: {ex.Message})");
            _lease?.Dispose();
        }
    }

    private static void DeletePayload(string directory, HashSet<string>? retained = null)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if (entry.Name is ".owner.json" or ".owner-lease") continue;
            if (retained?.Contains(entry.FullName) == true) continue;
            if (entry is DirectoryInfo) DeleteTree(entry.FullName);
            else File.Delete(entry.FullName);
        }
    }

    public static int RecoverAbandonedRuns() => RecoverAbandonedRuns(BaseDirectory);

    // A separate base tests ownership without sweeping another test's artifacts.
    public static int RecoverAbandonedRuns(string baseDirectory)
    {
        if (!Directory.Exists(baseDirectory)) return 0;
        RejectLink(baseDirectory);
        var removed = 0;
        foreach (var directory in Directory.EnumerateDirectories(baseDirectory))
        {
            try
            {
                RejectLink(directory);
                var marker = Path.Combine(directory, ".owner.json");
                var leasePath = Path.Combine(directory, ".owner-lease");
                if (File.Exists(marker))
                {
                    RejectLink(marker);
                    var owner = JsonSerializer.Deserialize<Owner>(File.ReadAllText(marker));
                    if (owner is null || OwnerMayBeAlive(owner)) continue;
                    RejectLink(leasePath);
                    using (var lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                        DeletePayload(directory);
                    File.Delete(marker);
                    File.Delete(leasePath);
                    Directory.Delete(directory);
                    removed++;
                }
                else
                {
                    // Legacy roots, and current roots whose run died before writing its marker
                    // ({stamp}-{guid}-{pid}): preserve every extant PID conservatively, including a reused PID.
                    var match = Regex.Match(Path.GetFileName(directory), @"^(\d{8}-\d{6})(?:-[0-9a-f]{32})?-(\d+)$");
                    if (!match.Success || !DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                        || !int.TryParse(match.Groups[2].Value, out var pid) || OwnerMayBeAlive(new Owner(pid, 0))) continue;
                    DeleteTree(directory);
                    removed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Console.Error.WriteLine($"Test scratch recovery deferred: {directory} ({ex.GetType().Name})");
            }
        }
        return removed;
    }

    private static bool OwnerMayBeAlive(Owner owner)
    {
        if (owner.Children?.Any(OwnerMayBeAlive) == true) return true;
        if (owner.ProcessId <= 0) return true;
        try
        {
            using var process = Process.GetProcessById(owner.ProcessId);
            return !process.HasExited && (owner.StartedUtcTicks == 0 || process.StartTime.ToUniversalTime().Ticks == owner.StartedUtcTicks);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }

    private static void RejectLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Test scratch ownership cannot traverse a symbolic link or junction.");
    }
}
