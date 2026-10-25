using System.Text.Json;
using Microsoft.Extensions.Logging;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCode.Hosting.Llm;

/// <summary>
/// Durable <see cref="ILlmUsageTracker"/> over the app-data usage file
/// (<see cref="SwarmCodeDataPaths.UsagePath"/>), following SwarmCode' JSON storage
/// idiom. SwarmCode makes very few LLM calls, so a bounded load-modify-replace
/// file is plenty — but unlike the old in-memory ring, usage and cost history
/// now survive restarts. Prompt/response bodies are never persisted.
/// </summary>
public sealed class JsonLlmUsageTracker : ILlmUsageTracker
{
    private const int MaxRecords = 5_000;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    private readonly SwarmCodeDataPaths _paths;
    private readonly ILogger<JsonLlmUsageTracker>? _logger;
    private readonly object _lock = new();
    private List<LlmUsageRecord>? _records;

    public JsonLlmUsageTracker(SwarmCodeDataPaths paths, ILogger<JsonLlmUsageTracker>? logger = null)
    {
        _paths = paths;
        _logger = logger;
    }

    public Task TrackUsageAsync(LlmUsageRecord record)
    {
        lock (_lock)
        {
            var records = LoadLocked();
            records.Insert(0, Sanitize(record));
            PruneLocked(records);
            SaveLocked(records);
        }
        return Task.CompletedTask;
    }

    public Task<List<LlmUsageRecord>> GetRecentUsageAsync(string? functionType = null, int limit = 100)
    {
        lock (_lock)
        {
            var query = LoadLocked().AsEnumerable();
            if (!string.IsNullOrWhiteSpace(functionType))
                query = query.Where(r => string.Equals(r.FunctionType, functionType, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(query.Take(limit).ToList());
        }
    }

    public Task<LlmCostSummary> GetCostSummaryAsync(string? functionType = null, DateTime? startDate = null, DateTime? endDate = null)
    {
        lock (_lock)
        {
            var query = LoadLocked().AsEnumerable();
            if (!string.IsNullOrWhiteSpace(functionType))
                query = query.Where(r => string.Equals(r.FunctionType, functionType, StringComparison.OrdinalIgnoreCase));
            if (startDate is { } s) query = query.Where(r => r.RequestTimestamp >= s);
            if (endDate is { } e) query = query.Where(r => r.RequestTimestamp <= e);
            var list = query.ToList();
            return Task.FromResult(new LlmCostSummary
            {
                TotalCost = list.Sum(r => r.EstimatedCost),
                TotalRequests = list.Count,
                TotalTokens = list.Sum(r => (long)r.TotalTokens),
                StartDate = startDate ?? DateTime.MinValue,
                EndDate = endDate ?? DateTime.UtcNow,
                CostByFunction = list.GroupBy(r => r.FunctionType ?? "").ToDictionary(g => g.Key, g => g.Sum(r => r.EstimatedCost)),
                RequestsByFunction = list.GroupBy(r => r.FunctionType ?? "").ToDictionary(g => g.Key, g => g.Count()),
                CostByModel = list.GroupBy(r => r.Model ?? "").ToDictionary(g => g.Key, g => g.Sum(r => r.EstimatedCost)),
                TokensByModel = list.GroupBy(r => r.Model ?? "").ToDictionary(g => g.Key, g => g.Sum(r => (long)r.TotalTokens)),
            });
        }
    }

    public Task<List<LlmCostBreakdown>> GetCostBreakdownAsync(TimeSpan period, string groupBy = "function")
        => GetCostBreakdownAsync(DateTime.UtcNow - period, DateTime.UtcNow, groupBy);

    public Task<List<LlmCostBreakdown>> GetCostBreakdownAsync(DateTime startDate, DateTime endDate, string groupBy = "function")
    {
        lock (_lock)
        {
            var list = LoadLocked()
                .Where(r => r.RequestTimestamp >= startDate && r.RequestTimestamp <= endDate)
                .ToList();
            var grouped = list
                .GroupBy(r => groupBy == "model" ? r.Model : r.FunctionType)
                .Select(g => new LlmCostBreakdown
                {
                    Category = g.Key,
                    TotalCost = g.Sum(r => r.EstimatedCost),
                    RequestCount = g.Count(),
                    TotalTokens = g.Sum(r => (long)r.TotalTokens),
                    AverageCostPerRequest = g.Any() ? g.Average(r => r.EstimatedCost) : 0,
                    AverageTokensPerRequest = g.Any() ? (decimal)g.Average(r => r.TotalTokens) : 0,
                    PeriodStart = startDate,
                    PeriodEnd = endDate,
                })
                .ToList();
            return Task.FromResult(grouped);
        }
    }

    private static LlmUsageRecord Sanitize(LlmUsageRecord record)
    {
        if (record.PromptText is null && record.ResponseText is null) return record;
        record.PromptText = null;
        record.ResponseText = null;
        return record;
    }

    private List<LlmUsageRecord> LoadLocked()
    {
        if (_records is not null) return _records;
        try
        {
            _records = File.Exists(_paths.UsagePath)
                ? JsonSerializer.Deserialize<List<LlmUsageRecord>>(File.ReadAllText(_paths.UsagePath)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable usage file must not take LLM calls down, but its history must not
            // vanish either: move it aside (like the routing audit ledger) and start a fresh ledger.
            Quarantine(ex);
            _records = [];
        }
        return _records;
    }

    private void Quarantine(Exception reason)
    {
        var aside = _paths.UsagePath + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
        try
        {
            if (File.Exists(_paths.UsagePath)) File.Move(_paths.UsagePath, aside, overwrite: false);
            _logger?.LogWarning(reason, "The LLM usage file was unreadable; moved it to {Quarantine} and started a new one.", aside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "The LLM usage file was unreadable and could not be moved aside.");
        }
    }

    private static void PruneLocked(List<LlmUsageRecord> records)
    {
        var cutoff = DateTime.UtcNow - Retention;
        records.RemoveAll(r => r.RequestTimestamp < cutoff);
        if (records.Count > MaxRecords)
            records.RemoveRange(MaxRecords, records.Count - MaxRecords);
    }

    private void SaveLocked(List<LlmUsageRecord> records)
    {
        var tempPath = _paths.UsagePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(records));
        File.Move(tempPath, _paths.UsagePath, overwrite: true);
    }
}
