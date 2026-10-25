using Bunit;
using Microsoft.Extensions.DependencyInjection;
using SwarmCode.App.Components.Shared;
using SwarmCode.Hosting;
using SwarmCore.Coding.Engagement;
using Talavine.TestSupport;

namespace SwarmCode.App.Tests;

public sealed class GitHubStarInvitationHostTests
{
    private static readonly CodingCompletionEvidence VerifiedDirectSuccess = new(
        Completed: true, Cancelled: false, HasError: false, AcceptancePassed: true,
        DirectUserRequest: true, PlanOnly: false, Temporary: false, Continuation: false,
        JournalSaved: true, SuccessfulWorkTool: false, AcceptanceFailed: false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_history_stays_hidden_until_useful_success_and_the_next_idle_boundary(bool workReceipt)
    {
        await using var fixture = new Fixture();
        var opens = 0;
        var view = fixture.Context.Render<GitHubStarInvitationHost>(p => p
            .Add(x => x.Allowed, true)
            .Add(x => x.OpenLinkAsync, _ => { opens++; return Task.FromResult(true); }));

        Assert.Empty(view.FindAll("section"));
        Assert.False(File.Exists(fixture.Paths.PreferencesPath));
        await fixture.Store.RecordAsync(workReceipt
            ? VerifiedDirectSuccess with { AcceptancePassed = false, SuccessfulWorkTool = true }
            : VerifiedDirectSuccess);
        view.Render(p => p.Add(x => x.Busy, true));
        Assert.Empty(view.FindAll("section"));
        view.Render(p => p.Add(x => x.Busy, false));

        view.WaitForAssertion(() => Assert.Single(view.FindAll("section[aria-label='Support Talavine Code']")));
        Assert.Equal(0, opens);
        Assert.False((await fixture.Store.ReadAsync()).Dismissed);
        Assert.Empty(fixture.Context.JSInterop.Invocations);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task Disallowed_or_active_work_hides_an_eligible_invitation_without_effects(
        bool allowed, bool busy, bool liveBusy)
    {
        await using var fixture = new Fixture();
        await fixture.Store.RecordAsync(VerifiedDirectSuccess);
        var opens = 0;
        var view = fixture.Context.Render<GitHubStarInvitationHost>(p => p
            .Add(x => x.Allowed, allowed)
            .Add(x => x.Busy, busy)
            .Add(x => x.IsWorkBusy, () => liveBusy)
            .Add(x => x.OpenLinkAsync, _ => { opens++; return Task.FromResult(true); }));

        Assert.Empty(view.FindAll("section, button"));
        Assert.Equal(0, opens);
        var saved = await fixture.Store.ReadAsync();
        Assert.True(saved.Eligible);
        Assert.False(saved.Dismissed);
    }

    [Fact]
    public async Task Explicit_dismissal_survives_a_new_component_and_a_new_store()
    {
        await using var fixture = new Fixture();
        await fixture.Store.RecordAsync(VerifiedDirectSuccess);
        var view = fixture.Context.Render<GitHubStarInvitationHost>(p => p.Add(x => x.Allowed, true));
        view.WaitForAssertion(() => Assert.Single(view.FindAll("section")));

        await view.FindAll("button").Single(button => button.TextContent == "Don't ask again").ClickAsync(new());
        Assert.Empty(view.FindAll("section"));
        view.Dispose();

        var reopenedStore = new StarInvitationStore(new AppPreferencesStore(fixture.Paths));
        await reopenedStore.RecordAsync(VerifiedDirectSuccess); // A later useful request cannot undo dismissal.
        Assert.True((await reopenedStore.ReadAsync()).Dismissed);
        await using var reopenedContext = new BunitContext();
        reopenedContext.Services.AddSingleton(reopenedStore);
        var reopened = reopenedContext.Render<GitHubStarInvitationHost>(p => p.Add(x => x.Allowed, true));
        Assert.Empty(reopened.FindAll("section, button"));
        Assert.Empty(reopenedContext.JSInterop.Invocations);
    }

    [Fact]
    public async Task Explicit_open_uses_only_the_exact_github_uri_and_saves_dismissal_after_success()
    {
        await using var fixture = new Fixture();
        await fixture.Store.RecordAsync(VerifiedDirectSuccess);
        var opened = new List<Uri>();
        var view = fixture.Context.Render<GitHubStarInvitationHost>(p => p
            .Add(x => x.Allowed, true)
            .Add(x => x.OpenLinkAsync, uri => { opened.Add(uri); return Task.FromResult(true); }));
        view.WaitForAssertion(() => Assert.Single(view.FindAll("section")));
        Assert.Empty(opened);

        await view.FindAll("button").Single(button => button.TextContent == "Star on GitHub").ClickAsync(new());

        Assert.Equal("https://github.com/scrossen/talavine-code", Assert.Single(opened).AbsoluteUri);
        Assert.Empty(view.FindAll("section"));
        Assert.True((await new StarInvitationStore(new AppPreferencesStore(fixture.Paths)).ReadAsync()).Dismissed);
        Assert.Empty(fixture.Context.JSInterop.Invocations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_throwing_launcher_keeps_the_invitation_retryable_without_dismissal(bool throws)
    {
        await using var fixture = new Fixture();
        await fixture.Store.RecordAsync(VerifiedDirectSuccess);
        var opens = 0;
        var view = fixture.Context.Render<GitHubStarInvitationHost>(p => p
            .Add(x => x.Allowed, true)
            .Add(x => x.OpenLinkAsync, _ =>
            {
                opens++;
                return throws ? Task.FromException<bool>(new IOException("Synthetic browser failure")) : Task.FromResult(false);
            }));
        view.WaitForAssertion(() => Assert.Single(view.FindAll("section")));

        await view.FindAll("button").Single(button => button.TextContent == "Star on GitHub").ClickAsync(new());

        Assert.Single(view.FindAll("section"));
        Assert.Contains(throws ? "Could not confirm" : "Could not open", view.Find("[role=alert]").TextContent);
        Assert.Contains("https://github.com/scrossen/talavine-code", view.Find(".sc-note").TextContent);
        Assert.False((await fixture.Store.ReadAsync()).Dismissed);
        await view.FindAll("button").Single(button => button.TextContent == "Star on GitHub").ClickAsync(new());
        Assert.Equal(2, opens);
        Assert.False((await fixture.Store.ReadAsync()).Dismissed);
    }

    [Fact]
    public async Task Live_work_starting_after_render_blocks_a_stale_open_button()
    {
        await using var fixture = new Fixture();
        await fixture.Store.RecordAsync(VerifiedDirectSuccess);
        var liveBusy = false;
        var opens = 0;
        var view = fixture.Context.Render<GitHubStarInvitationHost>(p => p
            .Add(x => x.Allowed, true)
            .Add(x => x.Busy, false)
            .Add(x => x.IsWorkBusy, () => liveBusy)
            .Add(x => x.OpenLinkAsync, _ => { opens++; return Task.FromResult(true); }));
        view.WaitForAssertion(() => Assert.Single(view.FindAll("section")));
        var staleButton = view.FindAll("button").Single(button => button.TextContent == "Star on GitHub");

        liveBusy = true; // Deliberately do not render a new Busy parameter before this event.
        await staleButton.ClickAsync(new());

        Assert.Equal(0, opens);
        Assert.False((await fixture.Store.ReadAsync()).Dismissed);
        Assert.Empty(view.FindAll("section"));
    }

    [Fact]
    public async Task Another_windows_dismissal_is_rechecked_before_opening_the_browser()
    {
        await using var fixture = new Fixture();
        await fixture.Store.RecordAsync(VerifiedDirectSuccess);
        var opens = 0;
        var view = fixture.Context.Render<GitHubStarInvitationHost>(p => p
            .Add(x => x.Allowed, true)
            .Add(x => x.OpenLinkAsync, _ => { opens++; return Task.FromResult(true); }));
        view.WaitForAssertion(() => Assert.Single(view.FindAll("section")));
        var staleButton = view.FindAll("button").Single(button => button.TextContent == "Star on GitHub");

        await new StarInvitationStore(new AppPreferencesStore(fixture.Paths)).DismissAsync();
        await staleButton.ClickAsync(new());

        Assert.Equal(0, opens);
        Assert.True((await fixture.Store.ReadAsync()).Dismissed);
        Assert.Empty(view.FindAll("section"));
    }

    [Fact]
    public async Task A_second_queued_click_while_the_launcher_is_awaited_cannot_repeat_the_external_effect()
    {
        await using var fixture = new Fixture();
        await fixture.Store.RecordAsync(VerifiedDirectSuccess);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opens = 0;
        var view = fixture.Context.Render<GitHubStarInvitationHost>(p => p
            .Add(x => x.Allowed, true)
            .Add(x => x.OpenLinkAsync, _ =>
            {
                Interlocked.Increment(ref opens);
                entered.TrySetResult(true);
                return release.Task;
            }));
        view.WaitForAssertion(() => Assert.Single(view.FindAll("section")));
        // Keep the callback from this rendered button: a second event may already be queued
        // when the first event hides the card while awaiting the native launcher.
        var queuedOpen = view.FindComponent<GitHubStarInvitation>().Instance.OnOpen;
        var firstClick = view.FindAll("button").Single(button => button.TextContent == "Star on GitHub").ClickAsync(new());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await view.InvokeAsync(() => queuedOpen.InvokeAsync());
            Assert.Equal(1, opens);
            Assert.False((await fixture.Store.ReadAsync()).Dismissed);
        }
        finally
        {
            release.TrySetResult(true);
            await firstClick;
        }

        Assert.Equal(1, opens);
        Assert.True((await fixture.Store.ReadAsync()).Dismissed);
        Assert.Empty(view.FindAll("section"));
    }

    [Fact]
    public async Task Malformed_preferences_stay_unchanged_and_do_not_show_an_invitation()
    {
        await using var fixture = new Fixture();
        const string malformed = "{malformed-preferences";
        await File.WriteAllTextAsync(fixture.Paths.PreferencesPath, malformed);
        var opens = 0;
        var view = fixture.Context.Render<GitHubStarInvitationHost>(p => p
            .Add(x => x.Allowed, true)
            .Add(x => x.OpenLinkAsync, _ => { opens++; return Task.FromResult(true); }));

        Assert.False((await fixture.Store.ReadAsync()).ShouldShow(false));
        Assert.Empty(view.FindAll("section, button"));
        Assert.Equal(0, opens);
        Assert.Equal(malformed, await File.ReadAllTextAsync(fixture.Paths.PreferencesPath));
    }

    [Fact]
    public async Task A_failed_dismissal_save_hides_this_run_reports_the_failure_and_preserves_the_bad_file()
    {
        await using var fixture = new Fixture();
        await fixture.Store.RecordAsync(VerifiedDirectSuccess);
        var view = fixture.Context.Render<GitHubStarInvitationHost>(p => p.Add(x => x.Allowed, true));
        view.WaitForAssertion(() => Assert.Single(view.FindAll("section")));
        const string malformed = "{changed-after-render";
        await File.WriteAllTextAsync(fixture.Paths.PreferencesPath, malformed);

        await view.FindAll("button").Single(button => button.TextContent == "Don't ask again").ClickAsync(new());

        Assert.Empty(view.FindAll("section, button"));
        Assert.Contains("Hidden for now, but your choice could not be saved", view.Find("[role=status]").TextContent);
        Assert.Equal(malformed, await File.ReadAllTextAsync(fixture.Paths.PreferencesPath));
        view.Render(p => p.Add(x => x.Busy, true));
        view.Render(p => p.Add(x => x.Busy, false));
        Assert.Empty(view.FindAll("section, button"));
        Assert.Equal(malformed, await File.ReadAllTextAsync(fixture.Paths.PreferencesPath));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TestTempDirectory _scratch = TestTempDirectory.Create("code-star-invitation-host");
        public SwarmCodeDataPaths Paths { get; }
        public AppPreferencesStore Preferences { get; }
        public StarInvitationStore Store { get; }
        public BunitContext Context { get; } = new();

        public Fixture()
        {
            Paths = new SwarmCodeDataPaths(Path.Combine(_scratch.Path, "profile"));
            Preferences = new AppPreferencesStore(Paths);
            Store = new StarInvitationStore(Preferences);
            Context.Services.AddSingleton(Preferences);
            Context.Services.AddSingleton(Store);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _scratch.DisposeAsync();
        }
    }
}
