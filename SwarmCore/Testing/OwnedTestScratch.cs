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
    private const string ProcessTempName = "process-temp";
    private const string IsolationOptOutVariable = "TALAVINE_TEST_TEMP_ISOLATION";
    private static readonly string[] TempVariables = OperatingSystem.IsWindows() ? ["TMP", "TEMP"] : ["TMPDIR"];
    private static Dictionary<string, string?>? _originalTemp;
    public static string BaseDirectory { get; } = Path.Combine(Environment.GetEnvironmentVariable("SWARMCORE_TEST_ORIGINAL_TEMP") ?? Path.GetTempPath(), "talavine-tests");
    public static string RunRoot => Root.Value;
    public static bool IsCreated => Root.IsValueCreated;
    public static event Action<string>? BeforeDelete;
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Contain fixtures that still use raw temporary-file APIs, including inherited child environments.</summary>
    public static IDisposable IsolateTemporaryEnvironment()
    {
        Initialize();
        var directory = Path.Combine(RunRoot, "tmp");
        Directory.CreateDirectory(directory);
        return new TemporaryEnvironment(directory);
    }

    private sealed class TemporaryEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = new();
        private readonly Dictionary<string, string> _assigned = new();
        private readonly string _directory;
        private bool _disposed;
        public TemporaryEnvironment(string directory)
        {
            _directory = directory;
            foreach (var key in new[] { "TMP", "TEMP", "TMPDIR" })
            {
                _previous[key] = Environment.GetEnvironmentVariable(key);
                _assigned[key] = directory;
                Environment.SetEnvironmentVariable(key, directory);
            }
            // Another source-linked helper (for example a harness assembly)
            // must create a sibling run, not a leased root inside this run.
            const string original = "SWARMCORE_TEST_ORIGINAL_TEMP";
            _previous[original] = Environment.GetEnvironmentVariable(original);
            _assigned[original] = _previous[original] ?? Path.GetDirectoryName(BaseDirectory)!;
            Environment.SetEnvironmentVariable(original, _assigned[original]);
        }
        public void Dispose()
        {
            if (_disposed) return;
            foreach (var pair in _previous)
                if (Environment.GetEnvironmentVariable(pair.Key) == _assigned[pair.Key])
                    Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            _disposed = true;
        }
    }

    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
        RecoverAbandonedRuns();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupCurrentRun();
    }

    /// <summary>
    /// Points this process's temp location (and its children's) at a directory inside the owned run
    /// root, so scratch that fixtures create through <see cref="Path.GetTempPath"/> or
    /// <see cref="Path.GetTempFileName"/> is reclaimed with the run instead of accumulating in the
    /// user's temp folder. Windows only: Unix socket paths are length-limited and the shorter system
    /// temp path must stay in effect there. Set <c>TALAVINE_TEST_TEMP_ISOLATION=0</c> to opt out.
    /// </summary>
    public static void IsolateProcessTemp()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Environment.GetEnvironmentVariable(IsolationOptOutVariable) is "0" or "false") return;
        Initialize();
        lock (RetainedChildren)
        {
            if (_originalTemp is not null) return;
            _ = BaseDirectory; // Resolve the real temp location before it is redirected.
            var target = Path.Combine(RunRoot, ProcessTempName);
            Directory.CreateDirectory(target);
            _originalTemp = TempVariables.ToDictionary(v => v, Environment.GetEnvironmentVariable);
            foreach (var variable in TempVariables) Environment.SetEnvironmentVariable(variable, target);
        }
    }

    private static void RestoreProcessTemp()
    {
        lock (RetainedChildren)
        {
            if (_originalTemp is null) return;
            foreach (var (variable, value) in _originalTemp) Environment.SetEnvironmentVariable(variable, value);
            _originalTemp = null;
        }
    }

    private static string CreateRoot()
    {
        Directory.CreateDirectory(BaseDirectory);
        RejectLink(BaseDirectory);
        // Keep all 128 random bits without the long timestamp/hex spelling: raw
        // temp consumers include Git worktrees and Windows Unix-domain sockets.
        var token = Convert.ToBase64String(Guid.NewGuid().ToByteArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var root = Path.Combine(BaseDirectory, $"r{Environment.ProcessId}-{token}");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(root);
        else Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _lease = new FileStream(Path.Combine(root, ".owner-lease"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var process = Process.GetCurrentProcess();
        WriteOwner(root, new Owner(process.Id, process.StartTime.ToUniversalTime().Ticks));
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
            var inspectionOwner = new Owner(process.Id, process.StartTime.ToUniversalTime().Ticks);
            RetainedChildren[full] = inspectionOwner;
            // A retained inspection app inherits the run's temporary environment.
            // Preserve that child until every retained inspection process exits.
            var temporaryFiles = Path.Combine(RunRoot, "tmp");
            if (Directory.Exists(temporaryFiles))
            {
                if (RetainedChildren.TryGetValue(temporaryFiles, out var previous))
                    inspectionOwner = inspectionOwner with { Children = [.. previous.Children ?? [], previous with { Children = null }] };
                RetainedChildren[temporaryFiles] = inspectionOwner;
            }
            using var parent = Process.GetCurrentProcess();
            WriteOwner(RunRoot, new Owner(parent.Id, parent.StartTime.ToUniversalTime().Ticks, RetainedChildren.Values.ToArray()));
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
        RestoreProcessTemp();
        // A straggler (for example a still-exiting child process) can hold a file in the redirected
        // temp folder. That must not fail the assembly; the owner marker keeps the run recoverable.
        var processTemp = Path.Combine(root, ProcessTempName);
        var processTempRemains = Directory.Exists(processTemp) && !TryDeleteTree(processTemp);
        Dictionary<string, Owner> retained;
        lock (RetainedChildren)
            retained = RetainedChildren.Where(p => OwnerMayBeAlive(p.Value)).ToDictionary(p => p.Key, p => p.Value);
        DeletePayload(root, retained.Keys.ToHashSet(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal));
        if (retained.Count > 0 || processTempRemains)
        {
            using var process = Process.GetCurrentProcess();
            WriteOwner(root, new Owner(process.Id, process.StartTime.ToUniversalTime().Ticks, retained.Values.ToArray()));
        }
        _lease?.Dispose();
        if (retained.Count == 0 && !processTempRemains)
        {
            File.Delete(Path.Combine(root, ".owner-lease"));
            File.Delete(Path.Combine(root, ".owner.json"));
            Directory.Delete(root);
        }
        lock (RetainedChildren) RetainedChildren.Clear();
        _lease = null;
        Root = new Lazy<string>(CreateRoot);
    }

    private static bool TryDeleteTree(string directory)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try { DeleteTree(directory); return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (Stopwatch.GetElapsedTime(started) >= DeleteRetryWindow)
                {
                    Console.Error.WriteLine($"Test temp cleanup deferred to recovery: {directory} ({ex.GetType().Name}: {ex.Message})");
                    return false;
                }
                Thread.Sleep(DeleteRetryDelay);
            }
        }
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
            if (retained is not null && entry.Name == ProcessTempName && Directory.Exists(entry.FullName)) continue;
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
                    // A run can die before writing its marker. Preserve every extant
                    // PID conservatively, including reused PIDs, for both name formats.
                    var compact = Regex.Match(Path.GetFileName(directory), @"^r(\d+)-[A-Za-z0-9_-]{22}$");
                    if (compact.Success && int.TryParse(compact.Groups[1].Value, out var compactPid))
                    {
                        if (OwnerMayBeAlive(new Owner(compactPid, 0))) continue;
                        DeleteTree(directory);
                        removed++;
                        continue;
                    }
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

    private static void WriteOwner(string root, Owner owner)
    {
        RejectLink(root);
        var marker = Path.Combine(root, ".owner.json");
        RejectLink(marker);
        var temporary = Path.Combine(root, ".owner-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(owner));
            File.Move(temporary, marker, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
