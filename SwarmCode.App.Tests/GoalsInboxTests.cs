using Bunit;
using SwarmCode.App.Components.Chat;
using SwarmCore.Agent.Communication;
using SwarmCore.Chat.Components;

namespace SwarmCode.App.Tests;

public sealed class GoalsInboxTests : BunitContext
{
    public GoalsInboxTests() => JSInterop.Mode = JSRuntimeMode.Loose;
    // Interop is recorded by the renderer; enumerate on its dispatcher, not the polling thread.
    private Task<int> ReadScrollsAsync() => Renderer.Dispatcher.InvokeAsync(() =>
        JSInterop.Invocations.Count(i => i.Identifier == "chatComponents.scrollToBottom"));
    private async Task WaitForScrollsAsync(int expected)
    {
        // Parent render notifications precede child OnAfterRender interop; observe that
        // actual side effect without forcing another render or clicking Refresh.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { while (await ReadScrollsAsync() < expected) await Task.Delay(10, deadline.Token); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        Assert.Equal(expected, await ReadScrollsAsync());
    }
    private static readonly Guid Goal = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static GoalContact Contact(Guid? id = null, string name = "Grow the app", string handle = "growth", long revision = 1)
        => new(id ?? Goal, "conversation-" + (id ?? Goal).ToString("N"), name, handle, "/test/repo", "Paused", revision);

    private sealed class Mailbox : IGoalMailbox
    {
        public List<GoalContact> Contacts = [Contact()];
        public List<GoalInboxMessage> Messages = [];
        public List<GoalReply> Replies = [];
        public List<(Guid Goal, GoalMessageCommand Command)> Sends = [];
        public List<(Guid Goal, string Name, string Handle, long Revision)> Renames = [];
        public bool FailAfterCommit;
        public int PageSize = 200;
        private long _sequence;
        private readonly Dictionary<string, GoalInboxMessage> _saved = [];
        public Task<GoalMailboxIdentity> GetIdentityAsync(CancellationToken ct = default) => Task.FromResult(new GoalMailboxIdentity("test-profile", "Test"));
        public Task<IReadOnlyList<GoalContact>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<GoalContact>>(Contacts.ToArray());
        public Task<GoalContact> RenameAsync(Guid goalId, string name, string handle, long expectedRevision, CancellationToken ct = default)
        {
            Renames.Add((goalId, name, handle, expectedRevision));
            var index = Contacts.FindIndex(c => c.GoalId == goalId);
            if (Contacts[index].Revision != expectedRevision) throw new InvalidOperationException("Revision changed");
            var value = Contacts[index] with { Name = name, Handle = handle, Revision = expectedRevision + 1 };
            Contacts[index] = value;
            return Task.FromResult(value);
        }
        public Task<GoalMessageThread> ReadAsync(Guid goalId, long afterSequence = 0, int take = 200, CancellationToken ct = default)
        {
            var all = Messages.Where(m => m.GoalId == goalId).Select(m => (m.Sequence, Message: m, Reply: (GoalReply?)null))
                .Concat(Replies.Where(r => r.GoalId == goalId).Select(r => (r.Sequence, Message: (GoalInboxMessage)null!, Reply: (GoalReply?)r)))
                .Where(e => e.Sequence > afterSequence).OrderBy(e => e.Sequence).ToArray();
            var page = all.Take(Math.Min(take, PageSize)).ToArray();
            return Task.FromResult(new GoalMessageThread(Contacts.Single(c => c.GoalId == goalId),
                page.Where(e => e.Message is not null).Select(e => e.Message).ToArray(),
                page.Where(e => e.Reply is not null).Select(e => e.Reply!).ToArray(),
                page.Length == 0 ? afterSequence : page[^1].Sequence, all.Length > page.Length));
        }
        public Task<GoalInboxMessage> SendAsync(Guid goalId, GoalMessageCommand message, CancellationToken ct = default)
        {
            Sends.Add((goalId, message));
            if (_saved.TryGetValue(message.IdempotencyKey, out var existing)) return Task.FromResult(existing);
            var result = new GoalInboxMessage("message-" + message.IdempotencyKey, goalId, ++_sequence, message.Kind,
                message.Text, message.Origin, DateTimeOffset.UtcNow, GoalDeliveryState.Received);
            _saved.Add(message.IdempotencyKey, result); Messages.Add(result);
            if (FailAfterCommit) { FailAfterCommit = false; throw new IOException("Lost ACK"); }
            return Task.FromResult(result);
        }
        public Task<GoalDelivery?> BeginDeliveryAsync(Guid goalId, string conversationId, string workerId, int maxCharacters = 16000, CancellationToken ct = default)
            => throw new InvalidOperationException("UI must not claim worker delivery");
        public Task RecordDeliveryAsync(string deliveryId, GoalDeliveryState state, string? detail = null, CancellationToken ct = default)
            => throw new InvalidOperationException("UI must not mark worker acknowledgment");
        public Task<GoalReply> ReplyAsync(Guid goalId, string conversationId, string deliveryId, string messageId, string text, GoalDeliveryState disposition, CancellationToken ct = default)
            => throw new InvalidOperationException("UI must not impersonate the goal");
    }

    [Fact]
    public async Task Unknown_ack_retries_the_exact_command_once_and_preserves_a_newer_draft()
    {
        var mailbox = new Mailbox { FailAfterCommit = true };
        var draft = new GoalInboxDraft { Text = "First idea", Kind = GoalMessageKind.Idea };
        Assert.Null(await draft.SubmitAsync(mailbox, Goal));
        var pending = draft.Pending!;
        draft.Text = "A newer draft";
        Assert.Null(await draft.SubmitAsync(mailbox, Goal));
        Assert.Single(mailbox.Sends);
        var saved = await draft.RetryAsync(mailbox);
        Assert.NotNull(saved);
        Assert.Equal("A newer draft", draft.Text);
        Assert.Null(draft.Pending);
        Assert.Equal(2, mailbox.Sends.Count);
        Assert.Same(pending, mailbox.Sends[1].Command);
        Assert.Equal(pending.IdempotencyKey, pending.Origin.SourceId);
        Assert.Equal("code-ui", pending.Origin.Channel);
        Assert.Equal("local-user", pending.Origin.AuthorId);
        Assert.Single(mailbox.Messages);
        Assert.Equal("Paused", mailbox.Contacts.Single().Status);
    }

    [Fact]
    public void Drafts_survive_navigation_in_the_same_mailbox_without_crossing_profile_boundaries()
    {
        var first = new Mailbox(); var second = new Mailbox();
        GoalInboxDraft.For(first, "goal:" + Goal).Text = "Kept draft";
        Assert.Equal("Kept draft", GoalInboxDraft.For(first, "goal:" + Goal).Text);
        Assert.Equal("", GoalInboxDraft.For(second, "goal:" + Goal).Text);
    }

    [Theory]
    [InlineData("@growth Suggest this", "Suggest this")]
    [InlineData("@GROWTH Suggest this", "Suggest this")]
    [InlineData("@\"Grow the app\" Suggest this", "Suggest this")]
    [InlineData("@goal:growth Suggest this", "Suggest this")]
    public void Named_messages_resolve_to_stable_goal_identity(string text, string body)
    {
        var route = GoalMessageRouting.Resolve(text, [Contact()], ["provider"]);
        Assert.Equal(Goal, route.Contact!.GoalId);
        Assert.Equal(body, route.Text);
        Assert.Null(route.Error);
    }

    [Fact]
    public void Ambiguous_goal_names_and_goal_provider_collisions_fail_before_provider_selection()
    {
        Assert.NotNull(GoalMessageRouting.Resolve("@\"Grow the app\" message", [Contact(), Contact(Guid.NewGuid(), handle: "other")], []).Error);
        Assert.NotNull(GoalMessageRouting.Resolve("@growth message", [Contact()], ["growth"]).Error);
        Assert.Equal(Goal, GoalMessageRouting.Resolve("@goal:growth message", [Contact()], ["growth"]).Contact!.GoalId);
        Assert.NotNull(GoalMessageRouting.Resolve("@missing message", [Contact()], ["provider"]).Error);
        var lane = GoalMessageRouting.Resolve("@provider message", [Contact()], ["provider"]);
        Assert.Null(lane.Contact); Assert.Null(lane.Error); Assert.Equal("@provider message", lane.Text);
    }

    [Fact]
    public void Closed_named_goal_uses_host_metadata_and_rejects_before_provider_selection_without_changing_input()
    {
        const string raw = "@goal:growth Keep this draft";
        var route = GoalMessageRouting.Resolve(raw, [Contact() with { Status = "Achieved", IsClosed = true }], ["provider"]);
        Assert.Null(route.Contact); Assert.Equal(raw, route.Text); Assert.Contains("closed", route.Error);
        Assert.NotNull(GoalMessageRouting.Resolve(raw, [Contact() with { Status = "Completed", IsClosed = false }], ["provider"]).Contact);
    }

    [Fact]
    public async Task Closed_goal_keeps_history_open_work_and_exact_pending_retry_but_disables_new_messages()
    {
        var mailbox = new Mailbox { FailAfterCommit = true };
        var draft = GoalInboxDraft.For(mailbox, "goal:" + Goal);
        draft.Text = "Previously saved message";
        Assert.Null(await draft.SubmitAsync(mailbox, Goal));
        var pending = draft.Pending;
        draft.Text = "An unsent newer draft";
        mailbox.Contacts[0] = Contact() with { Status = "Achieved", IsClosed = true };
        mailbox.Replies.Add(new("reply", Goal, mailbox.Messages[0].Id, 2, "An existing reply", GoalDeliveryState.Replied, DateTimeOffset.UtcNow));
        var view = Render<GoalInboxPanel>(p => p.Add(x => x.Mailbox, mailbox).Add(x => x.InitialGoalId, Goal).Add(x => x.Poll, false));
        view.WaitForElement("textarea");
        Assert.True(view.Find("textarea").HasAttribute("disabled")); Assert.True(view.Find("select").HasAttribute("disabled"));
        Assert.True(view.FindAll("button").Single(b => b.TextContent == "Send to agent inbox").HasAttribute("disabled"));
        Assert.Contains("This goal is closed", view.Markup); Assert.Contains("An existing reply", view.Markup);
        Assert.Contains("/session/", view.Find("a").GetAttribute("href"));
        await view.FindAll("button").Single(b => b.TextContent == "Retry same message").ClickAsync(new());
        Assert.Equal(2, mailbox.Sends.Count); Assert.Same(pending, mailbox.Sends[1].Command);
        Assert.Single(mailbox.Messages); Assert.Equal("An unsent newer draft", draft.Text);
    }

    [Fact]
    public async Task Rendered_send_is_an_inbox_message_and_does_not_acknowledge_or_resume_the_paused_goal()
    {
        var mailbox = new Mailbox();
        var view = Render<GoalInboxPanel>(p => p.Add(x => x.Mailbox, mailbox).Add(x => x.InitialGoalId, Goal).Add(x => x.Poll, false));
        view.WaitForElement("textarea");
        view.Find("textarea").Input("A useful question");
        view.Find("select").Change(nameof(GoalMessageKind.Question));
        await view.FindAll("button").Single(b => b.TextContent == "Send to agent inbox").ClickAsync(new());
        Assert.Equal(GoalMessageKind.Question, Assert.Single(mailbox.Sends).Command.Kind);
        Assert.Equal("A useful question", Assert.Single(mailbox.Messages).Text);
        Assert.Equal("Paused", mailbox.Contacts.Single().Status);
        Assert.Contains("Received", view.Markup);
        Assert.Equal("", view.Find("textarea").GetAttribute("value") ?? view.Find("textarea").TextContent);
        Assert.Contains("/session/", view.Find("a").GetAttribute("href"));
        Assert.Contains("@growth", view.Markup);
    }

    [Fact]
    public void Held_status_is_distinct_from_an_agent_reply_and_preserves_unread_message()
    {
        var mailbox = new Mailbox();
        mailbox.Contacts[0] = Contact() with { StatusDetail = "Work is paused.", Blocker = "Waiting for a scope decision." };
        mailbox.Messages.Add(new("pending", Goal, 1, GoalMessageKind.Question, "Why stopped?", new("code-ui", "pending", "local-user"), DateTimeOffset.UtcNow, GoalDeliveryState.Received));
        mailbox.Replies.Add(new("status", Goal, "pending", 2, "Your message is saved.", GoalDeliveryState.Received, DateTimeOffset.UtcNow)
            { Origin = GoalReplyOrigin.HostStatus, ObservedGoalVersion = 7 });
        var view = Render<GoalInboxPanel>(p => p.Add(x => x.Mailbox, mailbox).Add(x => x.InitialGoalId, Goal).Add(x => x.Poll, false));
        view.WaitForAssertion(() => Assert.Contains("Code status", view.Markup));
        Assert.DoesNotContain("Goal reply", view.Markup);
        Assert.Contains("unread when recorded", view.Markup);
        Assert.Contains("Waiting for a scope decision.", view.Markup);
        Assert.Equal(GoalDeliveryState.Received, Assert.Single(mailbox.Messages).State);
    }

    [Fact]
    public async Task Rename_uses_the_edit_baseline_even_when_polling_finds_a_newer_revision()
    {
        var mailbox = new Mailbox();
        var view = Render<GoalInboxPanel>(p => p.Add(x => x.Mailbox, mailbox).Add(x => x.InitialGoalId, Goal).Add(x => x.Poll, false));
        view.WaitForElement("textarea");
        await view.FindAll("button").Single(b => b.TextContent == "Rename agent").ClickAsync(new());
        view.Find("[aria-label='Agent name']").Change("My edit");
        mailbox.Contacts[0] = Contact(name: "Competing edit", revision: 2);
        await view.FindAll("button").Single(b => b.TextContent == "Refresh").ClickAsync(new());
        await view.FindAll("button").Single(b => b.TextContent == "Save name").ClickAsync(new());
        Assert.Equal(1, Assert.Single(mailbox.Renames).Revision);
        Assert.Equal("Competing edit", mailbox.Contacts[0].Name);
        Assert.Equal("My edit", view.Find("[aria-label='Agent name']").GetAttribute("value"));
        Assert.Contains("edits are kept", view.Find("[role='alert']").TextContent);
    }

    [Fact]
    public async Task Periodic_status_updates_replace_the_same_bubble_without_claiming_a_semantic_read()
    {
        var mailbox = new Mailbox();
        mailbox.Messages.Add(new("one", Goal, 1, GoalMessageKind.Question, "An actual question", new("test", "source", "user"),
            DateTimeOffset.UtcNow, GoalDeliveryState.Received));
        var view = Render<GoalInboxPanel>(p => p.Add(x => x.Mailbox, mailbox).Add(x => x.InitialGoalId, Goal));
        view.WaitForElement("article");
        mailbox.Messages[0] = mailbox.Messages[0] with { Sequence = 2, State = GoalDeliveryState.Delivered };
        view.WaitForAssertion(() => Assert.Contains("Delivered", view.Find("article").TextContent), TimeSpan.FromSeconds(5));
        Assert.Single(view.FindAll("article"));
        Assert.Equal(GoalDeliveryState.Delivered, mailbox.Messages[0].State);
    }

    [Fact]
    public async Task Inbox_poll_follows_new_replies_at_bottom_but_keeps_older_reading_and_drafts_in_place()
    {
        var mailbox = new Mailbox();
        mailbox.Messages.Add(new("one", Goal, 1, GoalMessageKind.Question, "Original question", new("test", "source", "user"),
            DateTimeOffset.UtcNow.AddMinutes(-1), GoalDeliveryState.Received));
        var view = Render<GoalInboxPanel>(p => p.Add(x => x.Mailbox, mailbox).Add(x => x.InitialGoalId, Goal));
        view.WaitForElement("textarea");
        await WaitForScrollsAsync(1);
        view.Find("textarea").Input("Keep this draft");
        await view.FindAll("button").Single(b => b.TextContent == "Refresh").ClickAsync(new());
        Assert.Equal(1, await ReadScrollsAsync()); // A rebuilt, unchanged snapshot is not new content.
        mailbox.Replies.Add(new("reply-one", Goal, "one", 2, "A polled reply", GoalDeliveryState.Replied, DateTimeOffset.UtcNow));
        view.WaitForAssertion(() => Assert.Contains("A polled reply", view.Markup), TimeSpan.FromSeconds(5));
        await WaitForScrollsAsync(2);
        var region = view.FindComponent<ChatScrollRegion>();
        await view.InvokeAsync(() => region.Instance.OnScrollState(false));
        mailbox.Replies.Add(new("reply-two", Goal, "one", 3, "Another polled reply", GoalDeliveryState.Replied, DateTimeOffset.UtcNow.AddSeconds(1)));
        view.WaitForAssertion(() => Assert.Contains("Another polled reply", view.Markup), TimeSpan.FromSeconds(5));
        Assert.Equal(2, await ReadScrollsAsync());
        Assert.Equal("Keep this draft", view.Find("textarea").GetAttribute("value"));
        await view.FindAll("button").Single(b => b.TextContent.Contains("Jump to latest")).ClickAsync(new());
        Assert.Equal(3, await ReadScrollsAsync());
    }

    [Fact]
    public async Task Selecting_another_agent_resets_scroll_away_and_opens_at_latest()
    {
        var mailbox = new Mailbox(); var other = Guid.NewGuid();
        mailbox.Contacts.Add(Contact(other, "Other agent", "other"));
        var view = Render<GoalInboxPanel>(p => p.Add(x => x.Mailbox, mailbox).Add(x => x.InitialGoalId, Goal).Add(x => x.Poll, false));
        view.WaitForElement("textarea");
        await WaitForScrollsAsync(1);
        await view.InvokeAsync(() => view.FindComponent<ChatScrollRegion>().Instance.OnScrollState(false));
        await view.FindAll(".sc-goals__contact").Single(b => b.TextContent.Contains("Other agent")).ClickAsync(new());
        await WaitForScrollsAsync(2);
        Assert.Empty(view.FindAll(".chat-jump"));
    }

    [Fact]
    public async Task Rendered_retry_keeps_a_newer_draft_and_reuses_the_original_message_identity()
    {
        var mailbox = new Mailbox { FailAfterCommit = true };
        var view = Render<GoalInboxPanel>(p => p.Add(x => x.Mailbox, mailbox).Add(x => x.InitialGoalId, Goal).Add(x => x.Poll, false));
        view.WaitForElement("textarea");
        view.Find("textarea").Input("Initial idea");
        await view.FindAll("button").Single(b => b.TextContent == "Send to agent inbox").ClickAsync(new());
        view.Find("textarea").Input("Newer thought");
        await view.FindAll("button").Single(b => b.TextContent == "Retry same message").ClickAsync(new());
        Assert.Single(mailbox.Messages);
        Assert.Equal(mailbox.Sends[0].Command.IdempotencyKey, mailbox.Sends[1].Command.IdempotencyKey);
        Assert.Equal("Newer thought", GoalInboxDraft.For(mailbox, "goal:" + Goal).Text);
    }

    [Fact]
    public async Task Messages_replies_and_transport_states_stay_in_sequence_across_pagination_and_refresh()
    {
        var mailbox = new Mailbox { PageSize = 2 };
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        foreach (var state in new[] { GoalDeliveryState.Received, GoalDeliveryState.Delivered, GoalDeliveryState.Read, GoalDeliveryState.Incorporated, GoalDeliveryState.Uncertain })
            mailbox.Messages.Add(new("m" + mailbox.Messages.Count, Goal, mailbox.Messages.Count * 2 + 1, GoalMessageKind.Idea,
                "Message " + state, new("test", "s", "u"), start.AddSeconds(mailbox.Messages.Count * 2 + 1), state));
        mailbox.Replies.Add(new("reply", Goal, "m0", 2, "Actual reply", GoalDeliveryState.Replied, start.AddSeconds(2)));
        var view = Render<GoalInboxPanel>(p => p.Add(x => x.Mailbox, mailbox).Add(x => x.InitialGoalId, Goal).Add(x => x.Poll, false));
        view.WaitForElement("article");
        Assert.Equal(2, view.FindAll("article").Count);
        await view.FindAll("button").Single(b => b.TextContent == "Load more messages").ClickAsync(new());
        await view.FindAll("button").Single(b => b.TextContent == "Load more messages").ClickAsync(new());
        Assert.Equal(6, view.FindAll("article").Count);
        Assert.Contains("Actual reply", view.FindAll("article")[1].TextContent);
        foreach (var state in new[] { "Received", "Delivered", "Read", "Incorporated", "Replied", "Uncertain" }) Assert.Contains(state, view.Markup);
        mailbox.Messages[0] = mailbox.Messages[0] with { Sequence = 11, State = GoalDeliveryState.Read };
        await view.FindAll("button").Single(b => b.TextContent == "Refresh").ClickAsync(new());
        Assert.Contains("Read", view.FindAll("article")[0].TextContent);
    }
}
