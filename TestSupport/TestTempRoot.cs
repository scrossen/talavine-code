using System.Runtime.CompilerServices;
#if TEST_SCRATCH_HARNESS
namespace SwarmCore.Testing.Harness;
#else
using SwarmCore.Testing;
namespace Talavine.TestSupport;
#endif

/// <summary>Compatibility facade for the shared process-owned test scratch lifecycle.</summary>
public static class TestTempRoot
{
    public const string BaseDirectoryName = "talavine-tests";
    public static string BaseDirectory => OwnedTestScratch.BaseDirectory;
    public static string RunRoot => OwnedTestScratch.RunRoot;
    public static bool RunRootCreated => OwnedTestScratch.IsCreated;
    public static void Initialize() => OwnedTestScratch.Initialize();
    public static string NewPath(string prefix) => Path.Combine(RunRoot, $"{Sanitize(prefix)}-{Guid.NewGuid():N}");
    public static string CreateDirectory(string prefix)
    {
        var path = NewPath(prefix);
        Directory.CreateDirectory(path);
        return path;
    }
    public static bool DeleteDirectory(string path) => OwnedTestScratch.DeleteDirectory(path);
    public static void RetainDirectoryForProcess(string path, int processId) => OwnedTestScratch.RetainDirectoryForProcess(path, processId);
    public static event Action<string>? BeforeDeleteFile;
    public static bool DeleteFile(string path)
    {
        OwnedTestScratch.ValidateOwnedPath(path);
        BeforeDeleteFile?.Invoke(path);
        try
        {
            foreach (var candidate in new[] { path, path + "-wal", path + "-shm", path + "-journal" }) File.Delete(candidate);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Test file cleanup remains pending: {path} ({ex.GetType().Name}: {ex.Message})");
            return false;
        }
    }
    private static string Sanitize(string prefix)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.IsNullOrWhiteSpace(prefix) ? "fixture" : new string(prefix.Trim().Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '-' : c).ToArray());
    }
}

public sealed class TestTempDirectory : IDisposable, IAsyncDisposable
{
    private bool _disposed;
    private TestTempDirectory(string path) => Path = path;
    public string Path { get; }
    public bool Exists => Directory.Exists(Path);
    public static TestTempDirectory Reserve(string prefix) => new(TestTempRoot.NewPath(prefix));
    public static TestTempDirectory Create(string prefix) => new(TestTempRoot.CreateDirectory(prefix));
    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);
    public string EnsureCreated() { Directory.CreateDirectory(Path); return Path; }
    public void Dispose()
    {
        if (_disposed) return;
        if (!TestTempRoot.DeleteDirectory(Path)) throw new IOException($"Test fixture did not release its scratch files: {Path}");
        _disposed = true;
    }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    public override string ToString() => Path;
}

internal static class TestTempRootModuleInitializer
{
    [ModuleInitializer]
    internal static void Initialize() => TestTempRoot.Initialize();
}
