using SwarmCode.App.Components.Layout;
using SwarmCode.Hosting;

namespace SwarmCode.App.Tests;

public sealed class SidebarSessionTreeTests
{
    private static WorkspaceSessionInfo S(string id, string? parent = null) => new(id, id, "/repo", DateTime.UnixEpoch, DateTime.UnixEpoch) { ParentSessionId = parent };

    [Fact]
    public void Delegated_history_starts_folded_and_explicit_expansion_survives_refresh()
    {
        var tree = new SidebarSessionTree();
        WorkspaceSessionInfo[] sessions = [S("root"), S("child", "root"), S("grandchild", "child")];
        Assert.Equal(2, Assert.Single(tree.Rows(sessions, null)).Descendants.Count);
        tree.Toggle("root");
        Assert.Equal(["root", "child"], tree.Rows(sessions, null).Select(r => r.Session.Id));
        Assert.Equal(2, tree.Rows(sessions, null).Count);
        tree.Toggle("root");
        Assert.Single(tree.Rows(sessions, null));
    }

    [Fact]
    public void Selecting_a_child_reveals_its_path_without_opening_unrelated_branches()
    {
        var tree = new SidebarSessionTree();
        WorkspaceSessionInfo[] sessions = [S("root"), S("child", "root"), S("selected", "child"), S("other"), S("hidden", "other")];
        var rows = tree.Rows(sessions, "selected");
        Assert.Contains(rows, r => r.Session.Id == "selected" && r.Depth == 2);
        Assert.True(rows.Single(r => r.Session.Id == "other").Collapsed);
        Assert.DoesNotContain(rows, r => r.Session.Id == "hidden");
        tree.Toggle("root"); // deliberate user folding remains possible
        Assert.DoesNotContain(tree.Rows(sessions, "selected"), r => r.Session.Id == "selected");
        tree.Rows(sessions, "other");
        Assert.Contains(tree.Rows(sessions, "selected"), r => r.Session.Id == "selected");
    }

    [Fact]
    public void Damaged_parent_cycles_and_orphans_stay_bounded_and_visible()
    {
        var rows = new SidebarSessionTree().Rows([S("a", "b"), S("b", "a"), S("orphan", "gone")], "a");
        Assert.Equal(3, rows.Select(r => r.Session.Id).Distinct().Count());
    }
}
