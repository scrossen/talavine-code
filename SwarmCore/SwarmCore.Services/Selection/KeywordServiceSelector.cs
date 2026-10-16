using System.Text.RegularExpressions;
using SwarmCore.Services.Abstractions;

namespace SwarmCore.Services.Selection;

/// <summary>
/// Ranks services by keyword overlap between the query and each service's
/// name/description/when-to-use/tags. A pragmatic default until embedding-based
/// semantic selection is wired in (the interface is the same, so it swaps cleanly).
/// </summary>
public sealed class KeywordServiceSelector : IServiceSelector
{
    public Task<IReadOnlyList<ServiceDefinition>> SelectAsync(
        string query, IReadOnlyList<ServiceDefinition> candidates, ServiceKind? kind, int limit, CancellationToken ct = default)
    {
        var pool = kind is { } k ? candidates.Where(c => c.Kind == k && c.Enabled) : candidates.Where(c => c.Enabled);
        var terms = Tokens(query);

        IReadOnlyList<ServiceDefinition> result;
        if (terms.Count == 0)
        {
            result = pool.OrderByDescending(c => c.UpdatedUtc).Take(limit).ToList();
        }
        else
        {
            result = pool
                .Select(c => (svc: c, score: Score(c, terms)))
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score)
                .ThenByDescending(x => x.svc.UpdatedUtc)
                .Take(limit)
                .Select(x => x.svc)
                .ToList();
            // Fall back to recent services if nothing matched, so a vague need still gets options.
            if (result.Count == 0)
                result = pool.OrderByDescending(c => c.UpdatedUtc).Take(limit).ToList();
        }
        return Task.FromResult(result);
    }

    private static int Score(ServiceDefinition s, HashSet<string> terms)
    {
        var haystack = Tokens($"{s.Name} {s.Name} {s.Description} {s.WhenToUse} {string.Join(' ', s.Tags)}");
        return terms.Count(t => haystack.Contains(t));
    }

    private static HashSet<string> Tokens(string text) =>
        Regex.Split(text.ToLowerInvariant(), @"[^a-z0-9]+")
            .Where(t => t.Length > 2)
            .ToHashSet();
}
