using Bunit;
using SwarmCode.App.Components.Chat;
using SwarmCore.Agent.Communication;
using SwarmCore.Agent.Continual;
using SwarmCore.Coding.Goals;

namespace SwarmCode.App.Tests;

public sealed class GoalLedgerViewTests : BunitContext
{
    public GoalLedgerViewTests() => JSInterop.Mode = JSRuntimeMode.Loose;
    private static readonly Guid Goal = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly DateTimeOffset At = new(2026, 10, 24, 20, 16, 22, TimeSpan.Zero);

    private sealed class Mailbox : IGoalMailbox
    {
        private static readonly GoalContact Contact = new(Goal, "conversation-ledger", "Grow the app", "growth", "/repo", "Working", 1);
        public Task<GoalMailboxIdentity> GetIdentityAsync(CancellationToken ct = default) => Task.FromResult(new GoalMailboxIdentity("test", "Test"));
        public Task<IReadOnlyList<GoalContact>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<GoalContact>>([Contact]);
        public Task<GoalContact> RenameAsync(Guid goalId, string name, string handle, long expectedRevision, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GoalMessageThread> ReadAsync(Guid goalId, long afterSequence = 0, int take = 200, CancellationToken ct = default)
            => Task.FromResult(new GoalMessageThread(Contact, [], [], afterSequence, false));
        public Task<GoalInboxMessage> SendAsync(Guid goalId, GoalMessageCommand message, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GoalDelivery?> BeginDeliveryAsync(Guid goalId, string conversationId, string workerId, int maxCharacters = 16000, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RecordDeliveryAsync(string deliveryId, GoalDeliveryState state, string? detail = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GoalReply> ReplyAsync(Guid goalId, string conversationId, string deliveryId, string messageId, string text, GoalDeliveryState disposition, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Ledger : ICodingGoalLedger
    {
        public List<(int Offset, int Take, bool Include)> Reads = [];
        public Task<CodingGoalLedgerPage?> ReadAsync(Guid goalId, int offset = 0, int take = 50, bool includeObservations = false, CancellationToken ct = default)
        {
            Reads.Add((offset, take, includeObservations));
            Assert.Equal(Goal, goalId);
            var entries = new List<CodingGoalLedgerEntry>
            {
                new(3, 20, "call-20", "codex.mcp_tool", At, CodingGoalLedgerKind.Published, ContinualSideEffectClass.ExternalSend, "Browser act · swarmcode",
                    "Posted the revised setup guide.", [new("https://github.com/scrossen/talavine-code/discussions/8", "github.com/scrossen/talavine-code/discussions/8")], [], HostVerified: true),
                new(3, 18, "call-18", "codex.file_change", At.AddMinutes(-9), CodingGoalLedgerKind.Changed, ContinualSideEffectClass.WritesLocal, "Edited 1 file",
                    "completed", [], ["/repo/docs/guide.md"], HostVerified: false),
            };
            if (includeObservations)
                entries.Add(new(3, 17, "call-17", "codex.mcp_tool", At.AddMinutes(-12), CodingGoalLedgerKind.Observed, ContinualSideEffectClass.ReadOnly, "Fs read json · swarmcode",
                    "{\"ok\":true}", [], [], HostVerified: false));
            return Task.FromResult<CodingGoalLedgerPage?>(new(entries, entries.Count, offset, false, 7, 99, "/repo"));
        }
    }

    [Fact]
    public void The_done_view_lists_entries_with_links_files_and_verification_and_toggles_observations()
    {
        var ledger = new Ledger();
        var cut = Render<GoalInboxPanel>(parameters => parameters
            .Add(p => p.Mailbox, new Mailbox()).Add(p => p.Ledger, ledger)
            .Add(p => p.InitialGoalId, Goal).Add(p => p.Poll, false).Add(p => p.InitialView, "done"));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".sc-ledger__entry").Count));
        var tabs = cut.FindAll(".sc-goals__view");
        Assert.Equal("true", tabs[1].GetAttribute("aria-pressed"));
        Assert.Equal("false", tabs[0].GetAttribute("aria-pressed"));
        var anchor = cut.Find(".sc-ledger__links a");
        Assert.Equal("https://github.com/scrossen/talavine-code/discussions/8", anchor.GetAttribute("href"));
        Assert.Equal("_blank", anchor.GetAttribute("target"));
        Assert.Contains("Verified", cut.Find(".sc-ledger__entry--published").TextContent);
        Assert.Equal("docs/guide.md", cut.Find(".sc-ledger__files code").TextContent);
        Assert.Empty(cut.FindAll(".sc-ledger__files button"));
        Assert.DoesNotContain("Looked at", cut.Find(".sc-ledger").TextContent);
        Assert.Empty(cut.FindAll(".sc-ledger__attempt"));
        Assert.Contains("Showing 2 of 2", cut.Markup);
        Assert.All(ledger.Reads, read => Assert.False(read.Include));

        cut.Find(".sc-toggle input").Change(true);
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".sc-ledger__entry").Count));
        Assert.Contains(ledger.Reads, read => read.Include);
        Assert.Contains("Looked at", cut.Find(".sc-ledger").TextContent);

        cut.FindAll(".sc-goals__view")[0].Click();
        cut.WaitForAssertion(() => Assert.Contains("No messages yet", cut.Markup));
        Assert.Empty(cut.FindAll(".sc-ledger__entry"));
    }

    private sealed class ResultsLedger : IGoalResultLedger
    {
        public List<(int Offset, int Take)> Reads = [];
        public Task<GoalResult> RecordAsync(Guid goalId, string conversationId, string leaseToken, GoalResultDraft draft, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GoalResultPage> ListAsync(Guid goalId, int offset = 0, int take = 50, CancellationToken ct = default)
        {
            Reads.Add((offset, take));
            Assert.Equal(Goal, goalId);
            var results = new List<GoalResult>
            {
                new("r2", Goal, 3, 2, At, At, GoalResultKind.Post, GoalResultState.Verified, "Start here: make three models defend their designs",
                    "https://github.com/scrossen/talavine-code/discussions/8", "GitHub discussions", "Pinned onboarding guide.", "agent", "github-first-run-discussion-20261024", null),
                new("r1", Goal, 3, 1, At.AddHours(-1), At.AddHours(-1), GoalResultKind.Email, GoalResultState.Pending, "Pitched the comparison article",
                    null, "editor@example.com", "Awaiting a reply.", "agent", "pycoders-20261024", null),
            };
            return Task.FromResult(new GoalResultPage(results, results.Count, offset, false));
        }
    }

    [Fact]
    public void The_done_view_shows_recorded_results_with_links_and_keeps_activity_behind_its_own_tab()
    {
        var results = new ResultsLedger();
        var cut = Render<GoalInboxPanel>(parameters => parameters
            .Add(p => p.Mailbox, new Mailbox()).Add(p => p.Ledger, new Ledger()).Add(p => p.Results, results)
            .Add(p => p.InitialGoalId, Goal).Add(p => p.Poll, false).Add(p => p.InitialView, "done"));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".sc-ledger__entry--result").Count));
        var tabs = cut.FindAll(".sc-goals__view");
        // No Reports ledger is wired here, so the Daily reports tab stays out of the strip.
        Assert.Equal(["Messages", "What's been done", "All activity"], tabs.Select(tab => tab.TextContent.Trim()));
        Assert.DoesNotContain("Daily reports", tabs.Select(tab => tab.TextContent.Trim()));
        Assert.Equal("true", tabs[1].GetAttribute("aria-pressed"));
        var link = cut.Find(".sc-ledger__entry--result .sc-ledger__title a");
        Assert.Equal("https://github.com/scrossen/talavine-code/discussions/8", link.GetAttribute("href"));
        Assert.Equal("Start here: make three models defend their designs", link.TextContent);
        Assert.Contains("Posted", cut.Find(".sc-ledger__entry--verified").TextContent);
        Assert.Contains("Confirmed", cut.Find(".sc-ledger__entry--verified").TextContent);
        Assert.Contains("github.com", cut.Find(".sc-ledger__entry--verified .sc-ledger__domain").TextContent);
        Assert.Contains("Email sent", cut.Find(".sc-ledger__entry--pending").TextContent);
        Assert.Contains("editor@example.com", cut.Find(".sc-ledger__entry--pending").TextContent);
        Assert.Empty(cut.FindAll(".sc-ledger__entry--pending .sc-ledger__title a"));
        Assert.Contains("Showing 2 of 2", cut.Markup);
        Assert.Empty(cut.FindAll(".sc-toggle"));
        tabs[2].Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".sc-ledger__entry--published")));
        Assert.Single(cut.FindAll(".sc-toggle"));
    }

    private sealed class ReportsLedger : IGoalReportLedger
    {
        public List<(int Offset, int Take)> Reads = [];
        public Task<GoalReport> RecordAsync(Guid goalId, string conversationId, string leaseToken, GoalReportDraft draft, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GoalReportPage> ListAsync(Guid goalId, int offset = 0, int take = 30, CancellationToken ct = default)
        {
            Reads.Add((offset, take));
            Assert.Equal(Goal, goalId);
            // Oldest first on purpose: the view must still put the newest day at the top.
            var reports = new List<GoalReport>
            {
                new("day-20261023", Goal, 3, 1, At.AddDays(-1), new DateOnly(2026, 10, 23), "Waited for repository access", null,
                    new Dictionary<string, string>(), [], [], [], "agent", null),
                new("day-20261024", Goal, 3, 2, At, new DateOnly(2026, 10, 24), "Pinned the onboarding guide and pitched one editor",
                    "Posted the revised setup guide at https://github.com/scrossen/talavine-code/discussions/8, then pinned it.\n\nEmailed the editor; the draft is at https://example.com/pitches/2026-10-24?ref=agent (sent 4 PM).",
                    new Dictionary<string, string> { ["stars"] = "1 (+0 today)", ["downloads"] = "12" },
                    [new GoalHeldItem("Post the comparison to Hacker News", "The repository is still private, so the link would fail for readers.") { Unblock = "Make the repository public or share a public mirror." }],
                    ["Follow up with the editor on Monday", "Reply to <em>every</em> discussion comment"], ["r1", "r2"], "agent", "/repo/reports/2026-10-24.md") { Revision = 2 },
            };
            return Task.FromResult(new GoalReportPage(reports, reports.Count, offset, false));
        }
    }

    [Fact]
    public void The_reports_view_shows_daily_reports_with_held_items_links_and_tabs()
    {
        var reports = new ReportsLedger();
        var cut = Render<GoalInboxPanel>(parameters => parameters
            .Add(p => p.Mailbox, new Mailbox()).Add(p => p.Ledger, new Ledger()).Add(p => p.Results, new ResultsLedger()).Add(p => p.Reports, reports)
            .Add(p => p.InitialGoalId, Goal).Add(p => p.Poll, false).Add(p => p.InitialView, "reports"));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".sc-report").Count));
        var tabs = cut.FindAll(".sc-goals__view");
        Assert.Equal(["Messages", "Daily reports", "What's been done", "All activity"], tabs.Select(tab => tab.TextContent.Trim()));
        Assert.Equal("true", tabs[1].GetAttribute("aria-pressed"));
        Assert.Equal("false", tabs[2].GetAttribute("aria-pressed"));
        Assert.All(reports.Reads, read => Assert.Equal(0, read.Offset));

        var articles = cut.FindAll(".sc-report");
        var newest = articles[0];
        var older = articles[1];
        Assert.Contains("sc-report--held", newest.ClassName);
        Assert.Equal(new DateOnly(2026, 10, 24).ToString("D"), newest.QuerySelector(".sc-report__day")!.GetAttribute("title"));
        Assert.Equal("Pinned the onboarding guide and pitched one editor", newest.QuerySelector(".sc-report__headline")!.TextContent);
        var updated = newest.QuerySelector(".sc-report__head .sc-report__updated")!.TextContent;
        Assert.StartsWith("Updated", updated);
        Assert.Contains("revision 2", updated);

        var paragraphs = newest.QuerySelectorAll(".sc-report__achieved p");
        Assert.Equal(2, paragraphs.Length);
        Assert.Contains(", then pinned it.", paragraphs[0].TextContent);
        Assert.EndsWith("(sent 4 PM).", paragraphs[1].TextContent);
        var links = newest.QuerySelectorAll(".sc-report__achieved a");
        Assert.Equal(2, links.Length);
        Assert.Equal("https://github.com/scrossen/talavine-code/discussions/8", links[0].GetAttribute("href"));
        Assert.Equal("github.com/scrossen/talavine-code/discussions/8", links[0].TextContent);
        Assert.Equal("https://example.com/pitches/2026-10-24?ref=agent", links[1].GetAttribute("href"));
        Assert.Equal("example.com/pitches/2026-10-24", links[1].TextContent);
        Assert.All(links, link => { Assert.Equal("_blank", link.GetAttribute("target")); Assert.Equal("noopener noreferrer", link.GetAttribute("rel")); });

        Assert.Equal(["stars 1 (+0 today)", "downloads 12"], newest.QuerySelectorAll(".sc-report__metrics .sc-chip").Select(chip => chip.TextContent.Trim()));

        var held = newest.QuerySelector(".sc-report__held")!;
        Assert.Equal("Held", held.QuerySelector(".sc-chip--danger")!.TextContent);
        Assert.Equal("Post the comparison to Hacker News", held.QuerySelector("li strong")!.TextContent);
        Assert.Contains("The repository is still private, so the link would fail for readers.", held.TextContent);
        Assert.Contains("To unblock: Make the repository public or share a public mirror.", held.TextContent);

        var next = newest.QuerySelector(".sc-report__next")!;
        Assert.Contains("Follow up with the editor on Monday", next.TextContent);
        Assert.Contains("Reply to <em>every</em> discussion comment", next.TextContent);
        Assert.Empty(next.QuerySelectorAll("em"));

        Assert.Contains("sc-report--clear", older.ClassName);
        Assert.Equal("Nothing moved the goal forward this day.", older.QuerySelector(".sc-report__achieved .sc-muted")!.TextContent);
        Assert.Null(older.QuerySelector(".sc-report__held"));
        Assert.Null(older.QuerySelector(".sc-report__metrics"));
        Assert.Null(older.QuerySelector(".sc-report__next"));
        Assert.Null(older.QuerySelector(".sc-report__results"));
        Assert.Contains("Showing 2 of 2", cut.Markup);

        var results = newest.QuerySelector(".sc-report__results")!;
        Assert.Equal("2 results recorded", results.TextContent.Trim());
        results.Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".sc-ledger__entry--result").Count));
        Assert.Equal("true", cut.FindAll(".sc-goals__view")[2].GetAttribute("aria-pressed"));
        Assert.Empty(cut.FindAll(".sc-report"));
    }

    [Fact]
    public void Without_a_ledger_the_panel_keeps_its_message_only_layout()
    {
        var cut = Render<GoalInboxPanel>(parameters => parameters
            .Add(p => p.Mailbox, new Mailbox()).Add(p => p.InitialGoalId, Goal).Add(p => p.Poll, false).Add(p => p.InitialView, "done"));
        cut.WaitForAssertion(() => Assert.Contains("No messages yet", cut.Markup));
        Assert.Empty(cut.FindAll(".sc-goals__view"));
    }
}
