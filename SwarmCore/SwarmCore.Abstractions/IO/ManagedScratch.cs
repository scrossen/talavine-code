using System.Diagnostics;
using System.Text.Json;

namespace SwarmCore.Abstractions.IO;

/// <summary>
/// Policy for scratch that must outlive a single call (a handoff, a staged update, a restart) but
/// never forever. Hosts pass their own root; this type has no product or customer knowledge.
/// </summary>
/// <param name="OrphanMaxAge">Age after which a directory with no readable owner marker is reclaimed.</param>
/// <param name="DefaultRetention">How long a lease that asked to be kept (<see cref="ScratchLease.Retain()"/>) survives its owner.</param>
public sealed record ScratchRetentionPolicy(TimeSpan OrphanMaxAge, TimeSpan DefaultRetention)
{
    public static ScratchRetentionPolicy Default { get; } = new(TimeSpan.FromDays(3), TimeSpan.FromDays(2));
}

public sealed record ScratchSweepResult(int Removed, long BytesFreed, int Skipped)
{
    public static ScratchSweepResult Empty { get; } = new(0, 0, 0);
}

/// <summary>
/// A directory owned by the creating process. Disposing removes it unless <see cref="Retain()"/> was
/// called, in which case the sweeper keeps it until <c>RetainUntil</c> and then reclaims it, even
/// after the owner exited or the machine rebooted.
/// </summary>
public sealed class ScratchLease : IDisposable
{
    private readonly string _markerPath;
    private readonly ManagedScratch.Marker _marker;
    private bool _retained;
    private bool _disposed;

    internal ScratchLease(string path, ManagedScratch.Marker marker)
    {
        Path = path;
        _marker = marker;
        _markerPath = System.IO.Path.Combine(path, ManagedScratch.MarkerFileName);
    }

    public string Path { get; }

    /// <summary>Survive disposal and process exit for <see cref="ScratchRetentionPolicy.DefaultRetention"/>.</summary>
    public void Retain() => Retain(ScratchRetentionPolicy.Default.DefaultRetention);

    /// <summary>Survive disposal and process exit until <paramref name="duration"/> elapses (handoff, restart, review).</summary>
    public void Retain(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        _retained = true;
        ManagedScratch.WriteMarker(_markerPath, _marker with { RetainUntilUtc = DateTime.UtcNow + duration });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_retained) return;
        ManagedScratch.TryDeleteTree(Path);
    }
}

/// <summary>
/// Process-owned scratch with crash, handoff and reboot recovery. Every directory records its owner
/// process identity (id plus start time, so a reused id is not mistaken for the owner). The sweep
/// reclaims directories whose owner is gone, honoring an explicit retain-until deadline, and
/// reclaims unmarked directories only by age. Reparse points are never traversed.
/// </summary>
public static class ManagedScratch
{
    internal const string MarkerFileName = ".scratch-owner.json";
    private static readonly TimeSpan DeleteRetryWindow = TimeSpan.FromSeconds(2);

    internal sealed record Marker(int ProcessId, long StartedUtcTicks, DateTime CreatedUtc, DateTime? RetainUntilUtc = null);

    /// <summary>Create <c>root/prefix-guid</c> owned by this process.</summary>
    public static ScratchLease Create(string root, string prefix)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("A scratch root is required.", nameof(root));
        Directory.CreateDirectory(root);
        RejectLink(root);
        var path = System.IO.Path.Combine(root, $"{Sanitize(prefix)}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        using var self = Process.GetCurrentProcess();
        var marker = new Marker(self.Id, self.StartTime.ToUniversalTime().Ticks, DateTime.UtcNow);
        WriteMarker(System.IO.Path.Combine(path, MarkerFileName), marker);
        return new ScratchLease(path, marker);
    }

    /// <summary>Remove abandoned children of <paramref name="root"/>. Safe to call on every startup.</summary>
    public static ScratchSweepResult Sweep(string root, ScratchRetentionPolicy? policy = null, DateTime? nowUtc = null)
    {
        policy ??= ScratchRetentionPolicy.Default;
        var now = nowUtc ?? DateTime.UtcNow;
        if (!Directory.Exists(root) || IsLink(root)) return ScratchSweepResult.Empty;
        int removed = 0, skipped = 0;
        long freed = 0;
        foreach (var directory in SafeEnumerateDirectories(root))
        {
            try
            {
                if (IsLink(directory)) { skipped++; continue; }
                if (!IsReclaimable(directory, policy, now)) { skipped++; continue; }
                var size = MeasureTree(directory);
                if (TryDeleteTree(directory)) { removed++; freed += size; }
                else skipped++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
        }
        return new ScratchSweepResult(removed, freed, skipped);
    }

    /// <summary>
    /// Prune accumulating plain files (logs, downloaded installers, receipts): keep the newest
    /// <paramref name="keepNewest"/> and anything younger than <paramref name="maxAge"/>; delete the rest.
    /// <paramref name="isProtected"/> shields files a caller still needs (the version being installed).
    /// </summary>
    public static ScratchSweepResult PruneFiles(string directory, string searchPattern, int keepNewest, TimeSpan maxAge,
        Func<string, bool>? isProtected = null, DateTime? nowUtc = null)
    {
        if (keepNewest < 0) throw new ArgumentOutOfRangeException(nameof(keepNewest));
        if (!Directory.Exists(directory) || IsLink(directory)) return ScratchSweepResult.Empty;
        var cutoff = (nowUtc ?? DateTime.UtcNow) - maxAge;
        var files = new DirectoryInfo(directory).EnumerateFiles(searchPattern, SearchOption.TopDirectoryOnly)
            .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();
        int removed = 0, skipped = 0;
        long freed = 0;
        foreach (var file in files.Skip(keepNewest))
        {
            if (file.LastWriteTimeUtc >= cutoff || isProtected?.Invoke(file.FullName) == true) { skipped++; continue; }
            try
            {
                var length = file.Length;
                file.Attributes &= ~FileAttributes.ReadOnly;
                file.Delete();
                removed++;
                freed += length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        }
        return new ScratchSweepResult(removed, freed, skipped);
    }

    /// <summary>
    /// Prune accumulating child directories (per-run workspaces, build outputs) that carry no owner
    /// marker: keep the newest <paramref name="keepNewest"/> and anything younger than
    /// <paramref name="maxAge"/>. <paramref name="isProtected"/> shields a run that is still referenced.
    /// </summary>
    public static ScratchSweepResult PruneDirectories(string root, string searchPattern, int keepNewest, TimeSpan maxAge,
        Func<string, bool>? isProtected = null, DateTime? nowUtc = null)
    {
        if (keepNewest < 0) throw new ArgumentOutOfRangeException(nameof(keepNewest));
        if (!Directory.Exists(root) || IsLink(root)) return ScratchSweepResult.Empty;
        var cutoff = (nowUtc ?? DateTime.UtcNow) - maxAge;
        var directories = new DirectoryInfo(root).EnumerateDirectories(searchPattern, SearchOption.TopDirectoryOnly)
            .Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderByDescending(d => LastActivityUtc(d.FullName))
            .ToList();
        int removed = 0, skipped = 0;
        long freed = 0;
        foreach (var directory in directories.Skip(keepNewest))
        {
            if (LastActivityUtc(directory.FullName) >= cutoff || isProtected?.Invoke(directory.FullName) == true) { skipped++; continue; }
            var size = MeasureTree(directory.FullName);
            if (TryDeleteTree(directory.FullName)) { removed++; freed += size; }
            else skipped++;
        }
        return new ScratchSweepResult(removed, freed, skipped);
    }

    private static bool IsReclaimable(string directory, ScratchRetentionPolicy policy, DateTime now)
    {
        var markerPath = System.IO.Path.Combine(directory, MarkerFileName);
        Marker? marker = null;
        try
        {
            if (File.Exists(markerPath) && !IsLink(markerPath))
                marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(markerPath));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }

        if (marker is null)
            return now - LastActivityUtc(directory) >= policy.OrphanMaxAge;
        if (marker.RetainUntilUtc is { } until && until > now) return false;
        if (marker.RetainUntilUtc is null && OwnerMayBeAlive(marker)) return false;
        // A retained lease whose deadline passed is reclaimable even when its owner is still running:
        // retention is an explicit promise to keep it only until then.
        return true;
    }

    private static bool OwnerMayBeAlive(Marker owner)
    {
        if (owner.ProcessId <= 0) return true;
        try
        {
            using var process = Process.GetProcessById(owner.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == owner.StartedUtcTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }

    private static DateTime LastActivityUtc(string directory)
    {
        var newest = Directory.GetLastWriteTimeUtc(directory);
        try
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
                if (entry.LastWriteTimeUtc > newest) newest = entry.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return newest;
    }

    internal static void WriteMarker(string path, Marker marker)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(marker));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Retrying recursive delete that never follows a link; false when something stays locked.</summary>
    public static bool TryDeleteTree(string directory)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                if (Directory.Exists(directory)) DeleteTree(directory);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (Stopwatch.GetElapsedTime(started) >= DeleteRetryWindow) return false;
                Thread.Sleep(50);
            }
        }
    }

    private static void DeleteTree(string directory)
    {
        var info = new DirectoryInfo(directory);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(directory); // Remove the link itself, never its target.
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

    private static long MeasureTree(string directory)
    {
        long total = 0;
        try
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                total += entry is DirectoryInfo ? MeasureTree(entry.FullName) : ((FileInfo)entry).Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return total;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        try { return Directory.EnumerateDirectories(root).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static bool IsLink(string path)
        => (File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static void RejectLink(string path)
    {
        if (IsLink(path)) throw new IOException("Managed scratch cannot live under a symbolic link or junction.");
    }

    private static string Sanitize(string prefix)
    {
        var chars = (string.IsNullOrWhiteSpace(prefix) ? "scratch" : prefix).Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        return new string(chars);
    }
}
