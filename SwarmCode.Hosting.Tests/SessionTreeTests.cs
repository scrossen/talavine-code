namespace SwarmCode.Hosting.Tests;

public class SessionTreeTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static WorkspaceSessionInfo S(string id, int createdMin, int activeMin, string? parent = null)
        => new(id, id, "/repo", T0.AddMinutes(createdMin), T0.AddMinutes(activeMin)) { ParentSessionId = parent };

    private static string Shape(IEnumerable<SessionTreeRow> rows) => string.Join(" ", rows.Select(r => new string('-', r.Depth) + r.Session.Id));

    [Fact]
    public void Agent_sessions_nest_under_their_parent_in_start_order()
    {
        var rows = SessionTree.Flatten(
        [
            S("a", 0, 10), S("b", 1, 20),
            S("a2", 6, 7, "a"), S("a1", 5, 9, "a"), S("a1x", 8, 8, "a1"),
        ], _ => false);

        Assert.Equal("b a -a1 --a1x -a2", Shape(rows));
        var a = rows.Single(r => r.Session.Id == "a");
        Assert.Equal(2, a.ChildCount);
        Assert.Equal(["a1", "a1x", "a2"], a.Descendants);
        Assert.False(rows.Single(r => r.Session.Id == "b").HasChildren);
    }

    [Fact]
    public void A_parent_whose_agent_is_busy_rises_to_the_top()
    {
        var rows = SessionTree.Flatten([S("a", 0, 10), S("b", 1, 20), S("a1", 2, 30, "a")], _ => false);
        Assert.Equal("a -a1 b", Shape(rows));
    }

    [Fact]
    public void A_collapsed_parent_hides_its_whole_subtree_but_still_reports_it()
    {
        var rows = SessionTree.Flatten([S("a", 0, 10), S("a1", 1, 2, "a"), S("a1x", 2, 3, "a1")], id => id == "a");
        var a = Assert.Single(rows);
        Assert.True(a.Collapsed);
        Assert.Equal(["a1", "a1x"], a.Descendants);

        // Folding a leaf has no effect, and folding a nested node keeps its ancestors open.
        Assert.Equal("a -a1", Shape(SessionTree.Flatten([S("a", 0, 10), S("a1", 1, 2, "a"), S("a1x", 2, 3, "a1")], id => id == "a1")));
        Assert.False(SessionTree.Flatten([S("x", 0, 0)], _ => true).Single().Collapsed);
    }

    [Fact]
    public void Orphans_and_cycles_stay_visible_once()
    {
        var rows = SessionTree.Flatten([S("o", 0, 5, "gone"), S("self", 1, 4, "self"), S("p", 2, 3, "q"), S("q", 3, 2, "p")], _ => false);
        Assert.Equal(["o", "self"], rows.Select(r => r.Session.Id).Take(2));
        // A parent cycle has no top: its sessions are still listed, each once, without looping.
        Assert.Equal(4, rows.Count);
        Assert.All(rows.GroupBy(r => r.Session.Id), g => Assert.Single(g));
    }
}
