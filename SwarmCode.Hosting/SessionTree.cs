namespace SwarmCode.Hosting;

/// <summary>One visible row of the session tree: the session, its nesting depth, and what is folded beneath it.</summary>
/// <param name="Descendants">Every session below this one, visible or not.</param>
/// <param name="Collapsed">The row has children and the person folded it; its descendants are not listed.</param>
public sealed record SessionTreeRow(WorkspaceSessionInfo Session, int Depth, int ChildCount, IReadOnlyList<string> Descendants, bool Collapsed)
{
    public bool HasChildren => ChildCount > 0;
}

/// <summary>
/// Orders a repository's sessions as a tree: agent sessions (<see cref="WorkspaceSessionInfo.ParentSessionId"/>) sit
/// under the session that started them. Top-level sessions are newest activity first, counting activity anywhere in
/// their subtree, so a parent whose agent is busy rises; children keep the order they were started in. A session whose
/// parent is not in the list (or that sits in a parent cycle) is shown at the top level.
/// </summary>
public static class SessionTree
{
    private const int MaxDepth = 16;

    public static IReadOnlyList<SessionTreeRow> Flatten(IReadOnlyList<WorkspaceSessionInfo> sessions, Func<string, bool> isCollapsed)
    {
        var ids = sessions.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var byParent = sessions
            .Where(s => s.ParentSessionId is { } p && p != s.Id && ids.Contains(p))
            .ToLookup(s => s.ParentSessionId!, StringComparer.Ordinal);
        var tops = sessions.Where(s => s.ParentSessionId is not { } p || p == s.Id || !ids.Contains(p)).ToList();

        var latest = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        DateTime Latest(WorkspaceSessionInfo s, int depth)
        {
            if (latest.TryGetValue(s.Id, out var known)) return known;
            var value = s.LastActivityUtc;
            if (depth < MaxDepth)
                foreach (var child in byParent[s.Id])
                {
                    var c = Latest(child, depth + 1);
                    if (c > value) value = c;
                }
            return latest[s.Id] = value;
        }

        var rows = new List<SessionTreeRow>(sessions.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // Returns the ids below s (walked even when folded, so a folded row can report its hidden work).
        List<string> Walk(WorkspaceSessionInfo s, int depth, bool visible)
        {
            var below = new List<string>();
            if (depth > MaxDepth || !seen.Add(s.Id)) return below;
            var children = byParent[s.Id].OrderBy(c => c.CreatedUtc).ToList();
            var collapsed = children.Count > 0 && isCollapsed(s.Id);
            var index = rows.Count;
            if (visible) rows.Add(new SessionTreeRow(s, depth, children.Count, [], collapsed));
            foreach (var child in children)
            {
                if (seen.Contains(child.Id)) continue;
                below.Add(child.Id);
                below.AddRange(Walk(child, depth + 1, visible && !collapsed));
            }
            if (visible && below.Count > 0) rows[index] = rows[index] with { Descendants = below };
            return below;
        }

        foreach (var top in tops.OrderByDescending(t => Latest(t, 0))) Walk(top, 0, visible: true);
        // A damaged index with a parent cycle has no top for those sessions; list them rather than lose them.
        foreach (var rest in sessions.Where(s => !seen.Contains(s.Id)).OrderByDescending(s => s.LastActivityUtc)) Walk(rest, 0, visible: true);
        return rows;
    }
}
