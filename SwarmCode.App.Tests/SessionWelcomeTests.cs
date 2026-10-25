using Bunit;
using SwarmCode.App.Tests.Components;

namespace SwarmCode.App.Tests;

public sealed class SessionWelcomeTests : BunitContext
{
    [Fact]
    public async Task Goal_example_fills_a_draft_without_starting_work()
    {
        var drafts = new List<string>();
        var component = Render<SessionWelcome>(parameters => parameters
            .Add(p => p.HasProject, true)
            .Add(p => p.OnChoose, (string draft) => drafts.Add(draft)));

        Assert.Contains("Pause, resume, or cancel", component.Markup);
        var button = component.FindAll("button").Single(b => b.QuerySelector("code")?.TextContent == "/goal objective");
        await button.ClickAsync(new());

        Assert.Equal(["/goal "], drafts);
    }
}
