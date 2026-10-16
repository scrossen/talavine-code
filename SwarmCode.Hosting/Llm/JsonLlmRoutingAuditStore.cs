using System.Text.Json;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCode.Hosting.Llm;

/// <summary>
/// Durable append-only <see cref="ILlmRoutingAuditStore"/> over an app-data
/// JSON file, following SwarmCode' JSON storage idiom. Routing decisions and
/// control-plane changes now survive restarts, so "why did you use that
/// model?" stays answerable. Retention mirrors Talavine's ledger:
/// high-volume selection/execution rows are bounded; control-plane changes
/// are kept for years.
/// </summary>
public sealed class JsonLlmRoutingAuditStore : ILlmRoutingAuditStore
{
    private const int MaxHighVolumeRows = 25_000;
    private static readonly TimeSpan HighVolumeRetention = TimeSpan.FromDays(30);
    private static readonly TimeSpan ControlPlaneRetention = TimeSpan.FromDays(365 * 7);

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
    private readonly object _lock = new();
    private List<LlmRoutingAuditEntry>? _entries;

    public JsonLlmRoutingAuditStore(SwarmCodeDataPaths paths) => _path = paths.RoutingAuditPath;

    public Task AppendAsync(LlmRoutingAuditEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            var entries = LoadLocked();
            entries.Insert(0, entry);
            PruneLocked(entries);
            SaveLocked(entries);
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
        try
        {
            _entries = File.Exists(_path)
                ? JsonSerializer.Deserialize<List<LlmRoutingAuditEntry>>(File.ReadAllText(_path)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Quarantine an unreadable ledger instead of silently overwriting it.
            TryQuarantineCorruptFile();
            _entries = [];
        }
        return _entries;
    }

    private void TryQuarantineCorruptFile()
    {
        try
        {
            if (File.Exists(_path))
                File.Move(_path, _path + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void PruneLocked(List<LlmRoutingAuditEntry> entries)
    {
        var now = DateTime.UtcNow;
        entries.RemoveAll(entry => entry.OccurredAtUtc < now
            - (ControlPlaneKinds.Contains(entry.EventKind) ? ControlPlaneRetention : HighVolumeRetention));

        var highVolume = entries.Count(entry => !ControlPlaneKinds.Contains(entry.EventKind));
        if (highVolume <= MaxHighVolumeRows) return;
        var toDrop = highVolume - MaxHighVolumeRows;
        for (var index = entries.Count - 1; index >= 0 && toDrop > 0; index--)
        {
            if (ControlPlaneKinds.Contains(entries[index].EventKind)) continue;
            entries.RemoveAt(index);
            toDrop--;
        }
    }

    private void SaveLocked(List<LlmRoutingAuditEntry> entries)
    {
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(entries));
        File.Move(tempPath, _path, overwrite: true);
    }
}
