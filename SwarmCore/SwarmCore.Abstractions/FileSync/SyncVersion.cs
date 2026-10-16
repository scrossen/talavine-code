namespace SwarmCore.Abstractions.FileSync;

/// <summary>Causal versions distinguish delayed updates from independent edits without comparing device clocks.</summary>
public static class SyncVersion
{
    public static Dictionary<string, long> Merge(IReadOnlyDictionary<string, long> a, IReadOnlyDictionary<string, long> b)
    {
        var result = new Dictionary<string, long>(a, StringComparer.Ordinal);
        foreach (var (device, revision) in b) result[device] = Math.Max(result.GetValueOrDefault(device), revision);
        return result;
    }

    public static Dictionary<string, long> Next(IReadOnlyDictionary<string, long> previous, string device)
    {
        var next = new Dictionary<string, long>(previous, StringComparer.Ordinal);
        next[device] = checked(next.GetValueOrDefault(device) + 1);
        return next;
    }

    public static bool Dominates(IReadOnlyDictionary<string, long> a, IReadOnlyDictionary<string, long> b)
        => b.All(p => a.TryGetValue(p.Key, out var value) && value >= p.Value);
}
