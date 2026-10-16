using System.Collections.Concurrent;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// Process-local catalog. Default registered by SwarmCore so the calibration service has
/// something to write to even on a fresh install. Apps that want results to survive
/// restarts can register a SQLite-backed implementation over this default.
/// </summary>
public sealed class InMemoryToolUseCalibrationCatalog : IToolUseCalibrationCatalog
{
    private readonly ConcurrentDictionary<string, ToolUseCalibrationResult> _store =
        new(StringComparer.OrdinalIgnoreCase);

    public Task<ToolUseCalibrationResult?> GetAsync(RoutingCalibrationKey key, CancellationToken ct = default)
    {
        _store.TryGetValue(key.ToStableString(), out var result);
        return Task.FromResult(result);
    }

    public Task SetAsync(RoutingCalibrationKey key, ToolUseCalibrationResult result, CancellationToken ct = default)
    {
        _store[key.ToStableString()] = result;
        return Task.CompletedTask;
    }

    public Task ClearAsync(string providerId, string modelId, CancellationToken ct = default)
    {
        var stale = _store.Keys
            .Where(k => KeyMatches(k, providerId, modelId))
            .ToList();
        foreach (var k in stale) _store.TryRemove(k, out _);
        return Task.CompletedTask;
    }

    public IReadOnlyList<(RoutingCalibrationKey Key, ToolUseCalibrationResult Result)> Snapshot()
    {
        return _store.Select(kvp => (Parse(kvp.Key), kvp.Value))
            .Where(x => x.Item1 is not null)
            .Select(x => (x.Item1!, x.Value))
            .ToList();
    }

    private static bool KeyMatches(string stable, string providerId, string modelId)
    {
        var parts = stable.Split('|');
        return parts.Length is 5 or 8
            && parts[2].Equals(providerId, StringComparison.OrdinalIgnoreCase)
            && parts[3].Equals(modelId, StringComparison.OrdinalIgnoreCase);
    }

    private static RoutingCalibrationKey? Parse(string stable)
    {
        var parts = stable.Split('|');
        return parts.Length switch
        {
            5 => new RoutingCalibrationKey(parts[0], parts[1], parts[2], parts[3], parts[4]),
            8 => new RoutingCalibrationKey(
                parts[0],
                parts[1],
                parts[2],
                parts[3],
                parts[4],
                DecodeIdentityPart(parts[5]),
                DecodeIdentityPart(parts[6]),
                DecodeIdentityPart(parts[7])),
            _ => null,
        };
    }

    private static string? DecodeIdentityPart(string value)
    {
        if (value == "-") return null;
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
        try
        {
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
