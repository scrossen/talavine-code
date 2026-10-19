using System.Text.Json;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Learning;

namespace SwarmCode.Hosting.Observability;

/// <summary>Fresh, read-only ledger view across workspace instances; never uses a writer's cached snapshot.</summary>
public static class SwarmCodeUsageOverview
{
    public static async Task<AiUsageOverview> ReadAsync(SwarmCodeDataPaths paths, int days, CancellationToken ct = default)
    {
        if (days is not (7 or 30)) throw new ArgumentOutOfRangeException(nameof(days));
        var now = DateTime.UtcNow;
        var info = new FileInfo(paths.UsagePath);
        if (info.Exists && (info.Length > 32_000_000 || (info.Attributes & FileAttributes.ReparsePoint) != 0))
            throw new InvalidDataException("Usage history cannot be read safely.");
        var records = info.Exists
            ? JsonSerializer.Deserialize<List<LlmUsageRecord>>(await File.ReadAllBytesAsync(paths.UsagePath, ct))
                ?? throw new InvalidDataException("Usage history is empty.")
            : [];
        return AiUsageOverview.From(records, now.Date.AddDays(1 - days), now);
    }
}
