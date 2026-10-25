using SwarmCode.Hosting;

namespace SwarmCode.App.Components.Layout;

/// <summary>History starts folded; expansion is window-local. The legacy collapsed preference is never rewritten or inverted.</summary>
public sealed class SidebarSessionTree
{
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _foldedByUser = new(StringComparer.Ordinal);
    private string? _selection;

    public IReadOnlyList<SessionTreeRow> Rows(IReadOnlyList<WorkspaceSessionInfo> sessions, string? selectedId)
    {
        var byId = sessions.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var selectedChanged = _selection != selectedId;
        var id = selectedId;
        while (id is not null && visited.Add(id) && byId.TryGetValue(id, out var current))
        {
            id = current.ParentSessionId;
            if (id is null) break;
            if (selectedChanged) _foldedByUser.Remove(id);
            if (!_foldedByUser.Contains(id)) _expanded.Add(id);
        }
        _selection = selectedId;
        return SessionTree.Flatten(sessions, id => !_expanded.Contains(id));
    }

    public void Toggle(string id)
    {
        if (_expanded.Remove(id)) _foldedByUser.Add(id);
        else { _expanded.Add(id); _foldedByUser.Remove(id); }
    }
}
