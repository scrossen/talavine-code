using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SwarmCode.Hosting.Settings;

/// <summary>Fresh reads, an exclusive cross-process lease and atomic replacement. Invalid existing data fails closed.</summary>
internal static class SettingsJsonFile
{
    // A Windows reader that did not share DELETE (for example, a legacy host or a
    // scanner) can briefly deny replacement even though our readers allow it.
    // Keep this bounded: persistent denial must still fail closed.
    private static readonly int[] TransientSharingRetryDelaysMs = [10, 20, 40, 80, 160, 320];
    [ThreadStatic] private static HashSet<string>? _ownedWriterPaths;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, PropertyNameCaseInsensitive = true };
    internal static JsonObject Read(string path)
    {
        return ParseSnapshot(ReadSnapshot(path));
    }
    private static JsonObject ParseSnapshot(byte[]? bytes)
    {
        if (bytes is null) return new();
        using var stream = new MemoryStream(bytes);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return JsonNode.Parse(reader.ReadToEnd()) as JsonObject
            ?? throw new InvalidDataException("Settings must be a JSON object.");
    }
    internal static string Revision(string path)
        => Revision(ReadSnapshot(path));
    private static string Revision(byte[]? bytes)
        => bytes is null ? "missing" : Convert.ToHexString(SHA256.HashData(bytes));

    private static byte[]? ReadSnapshot(string path, bool leaseHeld = false)
    {
        if (!leaseHeld && _ownedWriterPaths is { Count: > 0 } owned)
            leaseHeld = owned.Contains(Path.GetFullPath(path));
        // Successful opens retain a complete old or new snapshot. Only a missing name
        // or recovery backup needs coordination with the replacement transition.
        var recovery = RecoveryFiles(path).Any();
        if (!recovery && ReadBytes(path) is { } bytes) return bytes;
        if (!leaseHeld && Directory.Exists(Path.GetDirectoryName(path)))
        {
            using var lease = ReadLease(path);
            return ReadSnapshot(path, leaseHeld: true);
        }
        if (recovery) throw new InvalidDataException($"Settings replacement needs recovery. Preserve '{path}' and its .replace-backup files before retrying.");
        return null;
    }

    private static IEnumerable<string> RecoveryFiles(string path)
        => Directory.Exists(Path.GetDirectoryName(path))
            ? Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.replace-backup")
            : [];

    private static byte[]? ReadBytes(string path)
    {
        // Writers replace a complete file atomically. A reader must allow that replacement,
        // and briefly retry a Windows sharing lock without converting it into empty settings.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var buffer = new MemoryStream();
                file.CopyTo(buffer);
                return buffer.ToArray();
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (Exception ex) when (CanRetryTransientSharing(ex, attempt))
            { Thread.Sleep(TransientSharingRetryDelaysMs[attempt]); }
        }
    }
    internal static T Mutate<T>(string path, Func<JsonObject, T> edit, string? revision = null,
        Action<string, string, string>? replaceExisting = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var lease = Lease(path);
        using var ownership = new WriterLeaseScope(path);
        var baseline = ReadSnapshot(path, leaseHeld: true);
        if (revision is not null && Revision(baseline) != revision) throw new InvalidOperationException("Settings changed or were deleted. Read them again before saving.");
        var root = ParseSnapshot(baseline);
        var result = edit(root);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var backup = tmp + ".replace-backup";
        var retainTemporary = false;
        try
        {
            File.WriteAllText(tmp, root.ToJsonString(Json), Encoding.UTF8);
            try { ReplaceSnapshot(tmp, path, backup, baseline is not null, replaceExisting); }
            catch (Exception error)
            {
                if (File.Exists(backup))
                {
                    try
                    {
                        // ReplaceFile can fail after moving the old name (Win32 1177).
                        // Restore only our checked baseline into an unoccupied name.
                        if (baseline is null || !baseline.AsSpan().SequenceEqual(ReadBytes(backup)))
                            throw new IOException("The recovery snapshot does not match the write baseline.");
                        var current = ReadBytes(path);
                        if (current is null) File.Move(backup, path);
                        else if (baseline.AsSpan().SequenceEqual(current)) File.Delete(backup);
                        else throw new IOException("Another snapshot occupies the settings name; it was preserved.");
                    }
                    catch (Exception recoveryError)
                    {
                        retainTemporary = true;
                        throw new IOException($"Settings replacement could not be recovered. Preserve '{path}', '{backup}' and '{tmp}'; reload before another edit.",
                            new AggregateException(error, recoveryError));
                    }
                }
                throw;
            }
            // A retained backup is a fail-closed recovery indicator for later processes.
            // Remove it only after a confirmed successful replacement or restoration.
            try { if (File.Exists(backup)) File.Delete(backup); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Settings were replaced but recovery cleanup failed. Preserve '{path}' and '{backup}' before retrying.", error);
            }
        }
        finally { if (!retainTemporary && File.Exists(tmp)) File.Delete(tmp); }
        return result;
    }
    // Mutate and its Action callback are synchronous. Thread-local ownership cannot
    // flow through ExecutionContext to a Task that outlives this actual file lease.
    private sealed class WriterLeaseScope : IDisposable
    {
        private readonly string _path;
        public WriterLeaseScope(string path)
        {
            _path = Path.GetFullPath(path);
            (_ownedWriterPaths ??= new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)).Add(_path);
        }
        public void Dispose() => _ownedWriterPaths!.Remove(_path);
    }
    private static bool IsTransientSharing(Exception error)
        => error is IOException io && SwarmCore.Abstractions.Storage.TransientFileLock.IsSharingViolation(io)
            || error is UnauthorizedAccessException && (error.HResult & 0xffff) == 5;
    private static bool CanRetryTransientSharing(Exception error, int attempt)
        => IsTransientSharing(error) && attempt < TransientSharingRetryDelaysMs.Length;

    private static void ReplaceSnapshot(string temporary, string path, string backup, bool existed,
        Action<string, string, string>? replaceExisting)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                // The baseline, not a late File.Exists check, chooses create or replace.
                // A deletion must fail, and first creation must not overwrite a competitor.
                if (!existed) File.Move(temporary, path);
                else if (replaceExisting is not null) replaceExisting(temporary, path, backup);
                else File.Replace(temporary, path, backup);
                return;
            }
            catch (Exception ex) when (CanRetryTransientSharing(ex, attempt)) { Thread.Sleep(TransientSharingRetryDelaysMs[attempt]); }
        }
    }

    internal static FileStream Lease(string path)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < until) { Thread.Sleep(25); }
        }
    }
    private static FileStream ReadLease(string path)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite); }
            catch (IOException) when (DateTime.UtcNow < until) { Thread.Sleep(25); }
        }
    }
    internal static JsonNode? Get(JsonObject root, string key) => root.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
    internal static void Set(JsonObject root, string key, JsonNode? value)
    {
        var actual = root.Select(p => p.Key).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
        root[actual] = value?.DeepClone();
    }
    internal static void Merge(JsonObject root, JsonObject changes)
    {
        foreach (var pair in changes)
        {
            if (pair.Value is JsonObject nested && Get(root, pair.Key) is JsonObject target) Merge(target, nested);
            else Set(root, pair.Key, pair.Value);
        }
    }
}
