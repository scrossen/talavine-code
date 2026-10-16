using System.Text.RegularExpressions;

namespace SwarmCore.Agent.Catalog;

/// <summary>Exact, unique command selection from an already authorized catalog.</summary>
public static class ToolInvocationPhrasePolicy
{
    /// <summary>
    /// Retains named calls in direct user-authored command clauses. Selection is
    /// restricted to the caller's authorized candidates; a mention, explanation,
    /// quoted instruction, or negated command does not select a tool.
    /// </summary>
    public static IReadOnlyList<ToolDescriptor> SelectExplicitCalls(
        string userMessage, IReadOnlyList<ToolDescriptor> candidates)
    {
        var byId = candidates.GroupBy(t => t.ToolId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var selected = new Dictionary<string, ToolDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(userMessage,
            "(?:^|[.!?;:\\n]\\s*|\\bthen\\s+)(?:please\\s+)?(?:call|use|invoke|run)\\s+(?:the\\s+)?[`\"]?(?<id>[a-z][a-z0-9_-]*(?:\\.[a-z0-9_-]+)+)[`\"]?(?![\\w-]|\\.[a-z0-9_-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            if (byId.TryGetValue(match.Groups["id"].Value, out var tool)) selected.TryAdd(tool.ToolId, tool);
        }
        return selected.Values.ToArray();
    }

    public static ToolDescriptor? Select(string userMessage, IReadOnlyList<ToolDescriptor> candidates)
    {
        var command = Normalize(userMessage);
        if (command.Length == 0) return null;
        ToolDescriptor? match = null;
        foreach (var candidate in candidates)
        {
            if (!candidate.UserInvocationPhrases.Any(phrase => Normalize(phrase) == command)) continue;
            if (match is not null && match.ToolId != candidate.ToolId) return null;
            match = candidate;
        }
        return match;
    }

    private static string Normalize(string value)
    {
        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .TrimEnd('.', '!', '?').ToLowerInvariant();
        if (normalized.StartsWith("please ", StringComparison.Ordinal)) normalized = normalized[7..];
        if (normalized.EndsWith(" please", StringComparison.Ordinal)) normalized = normalized[..^7];
        return normalized.Trim();
    }
}
