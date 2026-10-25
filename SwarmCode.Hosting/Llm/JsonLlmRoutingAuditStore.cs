using System.Text;
using System.Text.Json;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCode.Hosting.Llm;

/// <summary>
/// Durable append-only <see cref="ILlmRoutingAuditStore"/> over an app-data JSON Lines file
/// (<c>llm-routing-audit.jsonl</c>), so routing decisions and control-plane changes survive restarts and
/// "why did you use that model?" stays answerable. Retention mirrors Talavine's ledger: high-volume
/// selection/execution rows are bounded; control-plane changes are kept for years.
/// <para>
/// The router appends several rows per model call, so an append writes one line; it never rewrites the
/// ledger. The file is compacted (pruned and rewritten) only when the high-volume rows exceed their bound
/// by a margin, when rows age out, or when an unreadable line is found. Queries use the in-memory copy.
/// </para>
/// </summary>
public sealed class JsonLlmRoutingAuditStore : ILlmRoutingAuditStore
{
    private const int MaxHighVolumeRows = 25_000;

    /// <summary>High-volume rows allowed past the bound before the file is compacted.</summary>
    private const int CompactionSlack = 2_500;

    private static readonly TimeSpan HighVolumeRetention = TimeSpan.FromDays(30);
    private static readonly TimeSpan ControlPlaneRetention = TimeSpan.FromDays(365 * 7);
    private static readonly TimeSpan AgeCheckInterval = TimeSpan.FromHours(1);
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static readonly HashSet<string> ControlPlaneKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        LlmRoutingAuditEventKinds.ControlRequested,
        LlmRoutingAuditEventKinds.PreferenceChanged,
        LlmRoutingAuditEventKinds.PreferenceCleared,
        LlmRoutingAuditEventKinds.AutomationChanged,
        LlmRoutingAuditEventKinds.PolicyChanged,
        LlmRoutingAuditEventKinds.OptimizationRun,
        LlmRoutingAuditEventKinds.BenchmarkRun,
        LlmRoutingAuditEventKinds.RecoveryRun,
    };

    private readonly string _path;
    private readonly string _legacyPath;
    private readonly object _lock = new();

    // Oldest first, in append order.
    private List<LlmRoutingAuditEntry>? _entries;
    private int _highVolumeRows;
    private DateTime _nextAgeCheckUtc;

    public JsonLlmRoutingAuditStore(SwarmCodeDataPaths paths)
    {
        _legacyPath = paths.RoutingAuditPath;
        _path = Path.ChangeExtension(paths.RoutingAuditPath, ".jsonl");
    }

    public Task AppendAsync(LlmRoutingAuditEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            var entries = LoadLocked();
            entries.Add(entry);
            if (!ControlPlaneKinds.Contains(entry.EventKind)) _highVolumeRows++;

            if (_highVolumeRows > MaxHighVolumeRows + CompactionSlack || DateTime.UtcNow >= _nextAgeCheckUtc)
            {
                if (PruneLocked(entries)) { CompactLocked(entries); return Task.CompletedTask; }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path, JsonSerializer.Serialize(entry) + "\n", Utf8NoBom);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LlmRoutingAuditEntry>> QueryAsync(
        LlmRoutingAuditQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (_lock)
        {
            var rows = LoadLocked()
                .Where(entry => Matches(entry, query))
                .OrderByDescending(entry => entry.OccurredAtUtc)
                .ToList();

            if (query.FunctionTypes is { Count: > 0 } && query.LimitPerFunction is > 0 and var perFunction)
            {
                rows = rows
                    .GroupBy(entry => entry.FunctionType ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .SelectMany(group => group.Take(perFunction))
                    .OrderByDescending(entry => entry.OccurredAtUtc)
                    .ToList();
            }

            return Task.FromResult<IReadOnlyList<LlmRoutingAuditEntry>>(
                rows.Take(Math.Max(1, query.Limit)).ToList());
        }
    }

    private static bool Matches(LlmRoutingAuditEntry entry, LlmRoutingAuditQuery query)
    {
        if (!MatchesText(query.AuditId, entry.AuditId)) return false;
        if (!MatchesText(query.CorrelationId, entry.CorrelationId)) return false;
        if (!MatchesText(query.SessionId, entry.SessionId)) return false;
        if (!MatchesText(query.TurnId, entry.TurnId)) return false;
        if (!MatchesText(query.Actor, entry.Actor)) return false;
        if (!MatchesText(query.Trigger, entry.Trigger)) return false;
        if (!MatchesText(query.FunctionType, entry.FunctionType)) return false;
        if (!MatchesText(query.EventKind, entry.EventKind)) return false;
        if (query.FunctionTypes is { Count: > 0 }
            && !query.FunctionTypes.Contains(entry.FunctionType ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            return false;
        if (query.EventKinds is { Count: > 0 }
            && !query.EventKinds.Contains(entry.EventKind, StringComparer.OrdinalIgnoreCase))
            return false;
        if (query.SinceUtc is { } since && entry.OccurredAtUtc < since) return false;
        return true;

        static bool MatchesText(string? filter, string? value)
            => string.IsNullOrWhiteSpace(filter)
               || string.Equals(filter, value, StringComparison.OrdinalIgnoreCase);
    }

    private List<LlmRoutingAuditEntry> LoadLocked()
    {
        if (_entries is not null) return _entries;

        var entries = new List<LlmRoutingAuditEntry>();
        var rewrite = false;
        try
        {
            if (File.Exists(_path))
            {
                var unreadable = false;
                foreach (var line in File.ReadLines(_path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        if (JsonSerializer.Deserialize<LlmRoutingAuditEntry>(line) is { } entry) entries.Add(entry);
                        else unreadable = true;
                    }
                    catch (JsonException) { unreadable = true; } // e.g. a line cut short by a crash
                }
                if (unreadable)
                {
                    // Keep the original bytes before the compaction below drops the unreadable lines.
                    TryCopyAside(_path);
                    rewrite = true;
                }
            }
            else if (File.Exists(_legacyPath))
            {
                // One-time migration from the whole-document ledger this store used to rewrite on every append.
                entries.AddRange((JsonSerializer.Deserialize<List<LlmRoutingAuditEntry>>(File.ReadAllText(_legacyPath)) ?? [])
                    .OrderBy(entry => entry.OccurredAtUtc));
                rewrite = true;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Quarantine an unreadable ledger instead of silently overwriting it.
            TryQuarantine(File.Exists(_path) ? _path : _legacyPath);
            entries.Clear();
        }

        _entries = entries;
        _highVolumeRows = entries.Count(entry => !ControlPlaneKinds.Contains(entry.EventKind));
        if (PruneLocked(entries) || rewrite)
        {
            try
            {
                CompactLocked(entries);
                if (File.Exists(_legacyPath) && File.Exists(_path)) File.Delete(_legacyPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The next append or compaction retries; queries still see the loaded rows.
            }
        }
        return _entries;
    }

    private static void TryCopyAside(string path)
    {
        try { File.Copy(path, path + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryQuarantine(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Move(path, path + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Drops aged-out rows and the oldest high-volume rows past the bound. True when anything was removed.</summary>
    private bool PruneLocked(List<LlmRoutingAuditEntry> entries)
    {
        var now = DateTime.UtcNow;
        _nextAgeCheckUtc = now + AgeCheckInterval;
        var removed = entries.RemoveAll(entry => entry.OccurredAtUtc < now
            - (ControlPlaneKinds.Contains(entry.EventKind) ? ControlPlaneRetention : HighVolumeRetention));

        var highVolume = entries.Count(entry => !ControlPlaneKinds.Contains(entry.EventKind));
        var toDrop = highVolume - MaxHighVolumeRows;
        if (toDrop > 0)
        {
            // Oldest first: drop from the start, keeping control-plane rows.
            var kept = new List<LlmRoutingAuditEntry>(entries.Count - toDrop);
            foreach (var entry in entries)
            {
                if (toDrop > 0 && !ControlPlaneKinds.Contains(entry.EventKind)) { toDrop--; removed++; continue; }
                kept.Add(entry);
            }
            entries.Clear();
            entries.AddRange(kept);
        }

        _highVolumeRows = entries.Count(entry => !ControlPlaneKinds.Contains(entry.EventKind));
        return removed > 0;
    }

    private void CompactLocked(List<LlmRoutingAuditEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tempPath = _path + ".tmp";
        using (var writer = new StreamWriter(tempPath, append: false, Utf8NoBom))
        {
            foreach (var entry in entries)
            {
                writer.Write(JsonSerializer.Serialize(entry));
                writer.Write('\n');
            }
        }
        File.Move(tempPath, _path, overwrite: true);
    }
}
