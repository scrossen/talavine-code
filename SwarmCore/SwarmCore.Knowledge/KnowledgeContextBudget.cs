using SwarmCore.Abstractions.Context;
namespace SwarmCore.Knowledge;

/// <summary>Enforces a measured final payload cap; adapters supply their actual wire serializer.</summary>
public static class KnowledgeContextBudget
{
    public static ContextPack Fit(ContextPack pack, Func<ContextPack, int> measureCharacters)
    {
        if (pack.Policy.MaxWireTokens is < 128 or > 32000)
            throw new ArgumentOutOfRangeException(nameof(pack), "Wire budget must be between 128 and 32000 estimated tokens.");
        var maximum = checked(pack.Policy.MaxWireTokens * 4);
        var candidate = pack;
        // Each iteration removes a whole claim and its orphan sources; aliases never change.
        for (var remaining = pack.Items.Count; remaining >= 0; remaining--)
        {
            var measured = measureCharacters(candidate);
            if (measured <= maximum) return candidate with { WireCharacters = measured };
            var items = candidate.Items.Take(Math.Max(0, candidate.Items.Count - 1)).ToArray();
            var used = items.SelectMany(i => i.SourceIds).ToHashSet(StringComparer.Ordinal);
            candidate = candidate with { Items = items, Sources = candidate.Sources.Where(s => used.Contains(s.Id)).ToArray(),
                EstimatedTokens = (int)Math.Ceiling(items.Sum(i => i.Text.Length) / 4d),
                Coverage = candidate.Coverage with { Status = "partial", StopReason = "wire-budget-exhausted", SupportsAbsence = false,
                    Gaps = candidate.Coverage.Gaps.Append("wire-budget-exhausted").Distinct(StringComparer.Ordinal).ToArray() } };
        }
        candidate = candidate with { Plan = null, Items = [], Sources = [], EstimatedTokens = 0,
            Coverage = new() { Status = "insufficient-budget", StopReason = "wire-budget-exhausted", Gaps = ["wire-budget-exhausted"] } };
        var minimal = measureCharacters(candidate);
        if (minimal > maximum) throw new InvalidOperationException("wire-budget-too-small");
        return candidate with { WireCharacters = minimal };
    }
}
