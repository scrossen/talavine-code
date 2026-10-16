using System.Collections.Concurrent;
using SwarmCore.Agent.Abstractions;

namespace SwarmCore.Agent.Catalog;

/// <summary>
/// Thread-safe in-process capability catalog. Singleton-friendly.
///
/// Search uses a deliberately simple lexical scoring pass over the descriptor's
/// id + description + keyword bag. The point isn't ranking quality — it's to
/// shrink the candidate set the LLM planner sees from "everything" to "the 5–10
/// tools most likely relevant" so the prompt stays short. The LLM still does
/// the real selection.
/// </summary>
public sealed class InMemoryCapabilityCatalog : ICapabilityCatalog
{
    private readonly ConcurrentDictionary<string, ToolDescriptor> _tools = new();

    public void Register(ToolDescriptor tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _tools[tool.ToolId] = tool;
    }

    public bool Unregister(string toolId) =>
        !string.IsNullOrEmpty(toolId) && _tools.TryRemove(toolId, out _);

    public ToolDescriptor? Get(string toolId) =>
        _tools.TryGetValue(toolId, out var tool) ? tool : null;

    public IReadOnlyList<ToolDescriptor> GetForAgent(string ownerAgentId) =>
        _tools.Values
            .Where(t => string.Equals(t.OwnerAgentId, ownerAgentId, StringComparison.OrdinalIgnoreCase))
            .ToList();

    public IReadOnlyList<ToolDescriptor> All() => _tools.Values.ToList();

    public IReadOnlyList<ToolDescriptor> Search(string goal, string? ownerAgentId = null, int topK = 10)
    {
        var pool = ownerAgentId is null
            ? (IEnumerable<ToolDescriptor>)_tools.Values
            : _tools.Values.Where(t =>
                string.Equals(t.OwnerAgentId, ownerAgentId, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(goal))
        {
            return pool.Take(topK).ToList();
        }

        var goalTokens = Tokenize(goal);
        if (goalTokens.Count == 0)
        {
            return pool.Take(topK).ToList();
        }

        // Score every candidate; ties resolved by cost ascending so cheap tools
        // surface first. The LLM still chooses — this is just shortlisting.
        return pool
            .Select(t => new { Tool = t, Score = Score(t, goalTokens) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => (int)x.Tool.Cost)
            .Take(topK)
            .Select(x => x.Tool)
            .ToList();
    }

    private static int Score(ToolDescriptor tool, IReadOnlyCollection<string> goalTokens)
    {
        var score = 0;

        // Keyword bag — explicit hints from the tool author count strongest.
        foreach (var keyword in tool.Keywords)
        {
            if (goalTokens.Contains(keyword.ToLowerInvariant()))
                score += 5;
        }

        // Tool id tokens — names like "spam.list-classified-spam" are signal.
        foreach (var token in Tokenize(tool.ToolId))
        {
            if (goalTokens.Contains(token))
                score += 3;
        }

        // Description tokens — broader, weaker.
        var descriptionTokens = Tokenize(tool.Description);
        foreach (var token in descriptionTokens)
        {
            if (goalTokens.Contains(token))
                score += 1;
        }

        return score;
    }

    private static HashSet<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new HashSet<string>();

        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = new System.Text.StringBuilder();

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
            }
            else if (current.Length > 0)
            {
                if (current.Length >= 2)
                    tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length >= 2)
            tokens.Add(current.ToString());

        return tokens;
    }
}
