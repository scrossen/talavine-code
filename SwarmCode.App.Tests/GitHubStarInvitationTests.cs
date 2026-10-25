using Bunit;
using SwarmCode.App.Components.Shared;

namespace SwarmCode.App.Tests;

public sealed class GitHubStarInvitationTests : BunitContext
{
    [Fact]
    public void Hidden_invitation_renders_nothing_and_never_invokes_an_action()
    {
        var opens = 0;
        var dismissals = 0;
        var view = Render<GitHubStarInvitation>(p => p
            .Add(x => x.Error, "Previous browser error")
            .Add(x => x.OnOpen, () => opens++)
            .Add(x => x.OnDismiss, () => dismissals++));

        Assert.Empty(view.FindAll("section, button, [role=alert]"));
        Assert.Equal(0, opens);
        Assert.Equal(0, dismissals);
        Assert.Empty(JSInterop.Invocations);
    }

    [Fact]
    public void Showing_and_rerendering_is_nonmodal_and_does_not_open_dismiss_or_claim_focus()
    {
        var opens = 0;
        var dismissals = 0;
        var view = Render<GitHubStarInvitation>(p => p
            .Add(x => x.OnOpen, () => opens++)
            .Add(x => x.OnDismiss, () => dismissals++));

        view.Render(p => p.Add(x => x.Visible, true));
        view.Render();

        var invitation = view.Find("section[aria-label='Support Talavine Code']");
        Assert.Contains("It's optional.", invitation.TextContent);
        Assert.Equal("Finding Talavine Code useful?", view.Find("h2").TextContent);
        Assert.Empty(view.FindAll("dialog, [role=dialog], [aria-modal=true], [autofocus], a, iframe"));
        Assert.All(view.FindAll("button"), button => Assert.Equal("button", button.GetAttribute("type")));
        Assert.Equal(0, opens);
        Assert.Equal(0, dismissals);
        Assert.Empty(JSInterop.Invocations);
    }

    [Fact]
    public void Only_the_explicit_star_button_requests_the_exact_github_destination()
    {
        var opens = 0;
        var dismissals = 0;
        var view = Render<GitHubStarInvitation>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.OnOpen, () => opens++)
            .Add(x => x.OnDismiss, () => dismissals++));

        var open = view.FindAll("button").Single(button => button.TextContent == "Star on GitHub");
        Assert.Equal("Open https://github.com/scrossen/talavine-code", open.GetAttribute("title"));
        open.Click();

        Assert.Equal(1, opens);
        Assert.Equal(0, dismissals);
        Assert.Single(view.FindAll("section")); // Visibility belongs to the host.
        Assert.Empty(JSInterop.Invocations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Both_dismiss_controls_request_the_same_explicit_dismissal(bool close)
    {
        var opens = 0;
        var dismissals = 0;
        var view = Render<GitHubStarInvitation>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.OnOpen, () => opens++)
            .Add(x => x.OnDismiss, () => dismissals++));

        var dismiss = close
            ? view.Find("button[aria-label='Dismiss GitHub invitation']")
            : view.FindAll("button").Single(button => button.TextContent == "Don't ask again");
        dismiss.Click();

        Assert.Equal(1, dismissals);
        Assert.Equal(0, opens);
        Assert.Empty(JSInterop.Invocations);
    }

    [Fact]
    public void Busy_state_disables_actions_without_sending_callbacks()
    {
        var opens = 0;
        var dismissals = 0;
        var view = Render<GitHubStarInvitation>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.Busy, true)
            .Add(x => x.OnOpen, () => opens++)
            .Add(x => x.OnDismiss, () => dismissals++));

        Assert.All(view.FindAll("button"), button => Assert.True(button.HasAttribute("disabled")));
        Assert.Equal(0, opens);
        Assert.Equal(0, dismissals);
        Assert.Empty(JSInterop.Invocations);
    }

    [Fact]
    public void Error_is_encoded_and_shows_the_exact_copyable_url_without_navigating()
    {
        var view = Render<GitHubStarInvitation>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.Error, "Could not open <script>browser</script>."));

        Assert.Equal("Could not open <script>browser</script>.", view.Find("[role=alert]").TextContent);
        Assert.Empty(view.FindAll("script, a, iframe"));
        Assert.Contains("https://github.com/scrossen/talavine-code", view.Find(".sc-note").TextContent);
        Assert.Empty(JSInterop.Invocations);

        view.Render(p => p.Add(x => x.Error, null));
        Assert.Empty(view.FindAll("[role=alert], .sc-note"));
    }
}
