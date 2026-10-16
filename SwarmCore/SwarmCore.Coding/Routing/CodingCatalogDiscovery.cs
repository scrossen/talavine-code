namespace SwarmCore.Coding.Routing;

/// <summary>Builds selectable routes from live catalogs without changing configured routes.</summary>
public static class CodingCatalogDiscovery
{
    public static async Task<IReadOnlyList<CodingLane>> DiscoverAsync(
        IEnumerable<CodingLane> candidates, IReadOnlyList<CodingLane> existing,
        Func<CodingLane, CancellationToken, Task<IReadOnlyList<string>>> readCatalog,
        CancellationToken ct = default)
    {
        var results = await Task.WhenAll(candidates.Where(candidate =>
            !existing.Any(l => l.Provider.Equals(candidate.Provider, StringComparison.OrdinalIgnoreCase)
                || l.Name.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase))).Select(async candidate =>
        {
            try
            {
                var models = await readCatalog(candidate, ct).ConfigureAwait(false);
                var model = models.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
                return model is null ? null : candidate with { Model = model };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { return null; } // An offline optional server must not prevent opening a workspace.
        })).ConfigureAwait(false);
        return results.OfType<CodingLane>().ToArray();
    }
}
