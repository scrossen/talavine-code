using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCode.Hosting.Goals;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Communication;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Goals;

namespace SwarmCode.Hosting.Tests;

public sealed class GoalReportsTests : TempDirTest
{
    private async Task<(FileCodingGoalStore Store, FileGoalReports Reports, ContinualObjectiveSnapshot Goal, ContinualObjectiveLease Lease)> Seed()
    {
        var store = new FileCodingGoalStore(Paths);
        var goal = await store.CreateOrJoinAsync(new ContinualObjectiveStart
        {
            Surface = "coding-goal", PartitionKey = CodingGoalOrchestrator.Partition, IdempotencyKey = "reports",
            ConversationId = "conversation", ExecutorKey = CodingGoalOrchestrator.ExecutorKey,
            ObjectiveText = "Make the product known", ObjectiveSummary = "Make the product known",
            Authority = ContinualObjectiveAuthority.UserRequested("test"),
        }, DateTimeOffset.UtcNow);
        var lease = (await store.TryClaimAsync(goal.Id, goal.Version, "worker", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5)))!;
        return (store, new FileGoalReports(Paths, store), goal, lease);
    }

    private sealed class WorkerScope : IDisposable
    {
        private readonly AsyncLocalCurrentTurnContextAccessor _accessor = new();
        private readonly CurrentTurnContext? _prior;
        public WorkerScope(Guid goal, string? lease, string conversation = "conversation")
        {
            _prior = _accessor.Current;
            _accessor.Current = new CurrentTurnContext(conversation, null, null) { GoalMessageGoalId = goal, GoalMessageLeaseToken = lease };
        }
        public void Dispose() => _accessor.Current = _prior;
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    [Fact]
    public async Task Only_the_bound_worker_records_and_recording_the_same_day_again_replaces_the_earlier_report()
    {
        var h = await Seed();
        var draft = new GoalReportDraft(Today, "Two replies and a listing")
        {
            Achieved = "Answered the directory maintainer at https://example.com/directory/pull/1 and listed the tool in a catalog.",
            Metrics = new Dictionary<string, string> { ["stars"] = "1 (+0)", ["downloads"] = "12" },
            Held = [new GoalHeldItem("Reply on X to similar topics", "No permitted publishing route verified") { Unblock = "Owner decision on acting as the account holder" }],
            NextActions = ["Answer the catalog review", "Record the 72h observation"],
            ResultIds = ["directory-pr-1", "catalog-entry-2", "directory-pr-1"],
            Evidence = "reports/2026-10-25/daily-report.md",
        };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Reports.RecordAsync(h.Goal.Id, "conversation", "wrong-lease", draft));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Reports.RecordAsync(h.Goal.Id, "other-conversation", h.Lease.Token, draft));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Reports.RecordAsync(Guid.NewGuid(), "conversation", h.Lease.Token, draft));
        var first = await h.Reports.RecordAsync(h.Goal.Id, "conversation", h.Lease.Token, draft);
        Assert.Equal(1, first.Revision); Assert.Equal("agent", first.Source); Assert.Equal(Today, first.Day);
        Assert.Equal(2, first.ResultIds.Count);   // duplicates collapse
        Assert.Single(first.Held); Assert.Equal("Owner decision on acting as the account holder", first.Held[0].Unblock);
        Assert.Equal("1 (+0)", first.Metrics["stars"]);

        var second = await h.Reports.RecordAsync(h.Goal.Id, "conversation", h.Lease.Token, draft with { Headline = "Three replies", Held = [], Evidence = null });
        Assert.Equal(first.Id, second.Id); Assert.Equal(2, second.Revision); Assert.Equal("Three replies", second.Headline);
        Assert.Empty(second.Held); Assert.Null(second.Evidence);   // a day's new record replaces the earlier one whole
        var yesterday = await h.Reports.RecordAsync(h.Goal.Id, "conversation", h.Lease.Token, new GoalReportDraft(Today.AddDays(-1), "Quiet day"));
        Assert.Empty(yesterday.Metrics); Assert.Empty(yesterday.NextActions); Assert.Null(yesterday.Achieved);

        var page = await h.Reports.ListAsync(h.Goal.Id);
        Assert.Equal(2, page.Total); Assert.False(page.HasMore);
        Assert.Equal([Today, Today.AddDays(-1)], page.Reports.Select(report => report.Day));
        var paged = await h.Reports.ListAsync(h.Goal.Id, offset: 1, take: 1);
        Assert.Single(paged.Reports); Assert.Equal("Quiet day", paged.Reports[0].Headline); Assert.False(paged.HasMore);
        Assert.Empty((await h.Reports.ListAsync(Guid.NewGuid())).Reports);

        // The file is a sibling store of the goal inventory, stored under the profile's goals folder with readable names.
        var json = await File.ReadAllTextAsync(Path.Combine(Paths.RootDirectory, "goals", "reports.json"));
        Assert.Contains("\"headline\": \"Three replies\"".Replace(": ", ":"), json.Replace(": ", ":"));
        Assert.Contains(Today.ToString("yyyy-MM-dd"), json);
    }

    [Fact]
    public async Task Drafts_are_validated_and_an_ended_lease_or_impossible_day_is_refused()
    {
        var h = await Seed();
        Assert.Throws<ArgumentException>(() => GoalReports.Validate(new GoalReportDraft(Today, " ")));
        Assert.Throws<ArgumentException>(() => GoalReports.Validate(new GoalReportDraft(default, "Headline")));
        Assert.Throws<ArgumentException>(() => GoalReports.Validate(new GoalReportDraft(Today, "Head\u0007line")));
        Assert.Throws<ArgumentException>(() => GoalReports.Validate(new GoalReportDraft(Today, "Headline") { Held = [new GoalHeldItem("Reply", " ")] }));
        Assert.Throws<ArgumentException>(() => GoalReports.Validate(new GoalReportDraft(Today, "Headline") { Metrics = new Dictionary<string, string> { ["stars"] = "" } }));
        Assert.Throws<ArgumentException>(() => GoalReports.Validate(new GoalReportDraft(Today, "Headline") { Metrics = new Dictionary<string, string> { ["two\nlines"] = "1" } }));
        Assert.Throws<ArgumentException>(() => GoalReports.Validate(new GoalReportDraft(Today, "Headline") { Metrics = Enumerable.Range(0, GoalReports.MaximumMetrics + 1).ToDictionary(i => "m" + i, _ => "1") }));
        Assert.Throws<ArgumentException>(() => GoalReports.Validate(new GoalReportDraft(Today, "Headline") { Held = Enumerable.Repeat(new GoalHeldItem("r", "why"), GoalReports.MaximumHeldItems + 1).ToArray() }));
        Assert.Throws<ArgumentException>(() => GoalReports.Validate(new GoalReportDraft(Today, "Headline") { ResultIds = Enumerable.Range(0, GoalReports.MaximumResultIds + 1).Select(i => "id" + i).ToArray() }));
        Assert.Throws<ArgumentException>(() => GoalReports.Validate(new GoalReportDraft(Today, "Headline") { NextActions = Enumerable.Repeat("x", GoalReports.MaximumNextActions + 1).ToArray() }));
        Assert.Throws<ArgumentException>(() => GoalReports.Validate(new GoalReportDraft(Today, "Headline") { Achieved = new string('a', GoalReports.MaximumAchievedCharacters + 1) }));
        var cleaned = GoalReports.Validate(new GoalReportDraft(Today, "  Headline  ") { Achieved = "line one\r\n\tline two  ", NextActions = [" do this "] });
        Assert.Equal("Headline", cleaned.Headline); Assert.Equal("line one\nline two", cleaned.Achieved); Assert.Equal("do this", cleaned.NextActions![0]);

        await Assert.ThrowsAsync<ArgumentException>(() => h.Reports.RecordAsync(h.Goal.Id, "conversation", h.Lease.Token, new GoalReportDraft(Today.AddDays(3), "Future")));
        await Assert.ThrowsAsync<ArgumentException>(() => h.Reports.RecordAsync(h.Goal.Id, "conversation", h.Lease.Token, new GoalReportDraft(Today.AddYears(-1), "Before the goal")));
        Assert.True(await h.Store.ControlAsync(h.Goal.Id, (await h.Store.GetAsync(h.Goal.Id))!.Version, CodingGoalControl.Pause, DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Reports.RecordAsync(h.Goal.Id, "conversation", h.Lease.Token, new GoalReportDraft(Today, "After release")));
    }

    [Fact]
    public async Task The_record_tool_requires_the_ambient_worker_returns_feedback_for_bad_drafts_and_the_list_tool_reads_back()
    {
        var h = await Seed();
        var catalog = new InMemoryCapabilityCatalog();
        await new GoalReplyToolRegistration(catalog, new FileGoalMailbox(Paths, h.Store), h.Store, new FileGoalResults(Paths, h.Store), h.Reports).StartAsync(default);
        var record = catalog.Get("goal_reports.record")!; var list = catalog.Get("goal_reports.list")!;
        Assert.Equal(ToolSideEffect.ExecutionMetadata, record.SideEffect); Assert.Equal(ToolSideEffect.ReadOnly, list.SideEffect);
        // The worker need not know the date: day is optional and the host fills in today (UTC); nested shapes are explicit in the schema.
        var schema = record.InputSchema!;
        Assert.Equal(["name", "value"], schema["properties"]!["metrics"]!["items"]!["required"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Equal(["request", "reason"], schema["properties"]!["held"]!["items"]!["required"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.DoesNotContain("day", schema["required"]!.AsArray().Select(node => node!.GetValue<string>()));
        var input = new GoalReportInput("Two replies", null, "Answered https://example.com/directory/pull/1.",
            [new GoalMetricInput("stars", "0"), new GoalMetricInput(" stars ", "1 (+0)")], [new GoalHeldInput("Reply on X", "No permitted route", "Owner decision")], ["Answer the review"], ["directory-pr-1"]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => record.Invoke(input, default));
        using (new WorkerScope(h.Goal.Id, "wrong-lease")) await Assert.ThrowsAsync<UnauthorizedAccessException>(() => record.Invoke(input, default));
        using (new WorkerScope(h.Goal.Id, h.Lease.Token))
        {
            using var saved = JsonDocument.Parse(Assert.IsType<string>(await record.Invoke(input, default)));
            Assert.True(saved.RootElement.GetProperty("saved").GetBoolean());
            Assert.Equal(1, saved.RootElement.GetProperty("held").GetInt32());
            Assert.Equal(Today.ToString("yyyy-MM-dd"), saved.RootElement.GetProperty("day").GetString());   // defaulted by the host
            // Rejected drafts come back as feedback, never as a failed (inspection-required) tool call.
            foreach (var bad in new[] { input with { Day = "yesterday" }, input with { Day = "2026-13-01" }, input with { Day = Today.AddDays(5).ToString("yyyy-MM-dd") }, input with { Headline = " " }, input with { Held = [new GoalHeldInput("Reply", "")] } })
            {
                using var refused = JsonDocument.Parse(Assert.IsType<string>(await record.Invoke(bad, default)));
                Assert.False(refused.RootElement.GetProperty("saved").GetBoolean());
                Assert.False(string.IsNullOrWhiteSpace(refused.RootElement.GetProperty("error").GetString()));
            }
            using var again = JsonDocument.Parse(Assert.IsType<string>(await record.Invoke(input with { Headline = "Two replies, one listing" }, default)));
            Assert.Equal(2, again.RootElement.GetProperty("revision").GetInt64());
            using (new WorkerScope(Guid.NewGuid(), h.Lease.Token))
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Reports.RecordAsync(h.Goal.Id, "conversation", h.Lease.Token, new GoalReportDraft(Today, "cross-goal")));
            using var listed = JsonDocument.Parse(Assert.IsType<string>(await list.Invoke(new GoalReportsListInput(), default)));
            Assert.Equal(1, listed.RootElement.GetProperty("total").GetInt32());
            var report = listed.RootElement.GetProperty("reports")[0];
            Assert.Equal("Two replies, one listing", report.GetProperty("headline").GetString());
            Assert.Equal("Owner decision", report.GetProperty("held")[0].GetProperty("unblock").GetString());
            Assert.Equal("1 (+0)", report.GetProperty("metrics").GetProperty("stars").GetString());   // the repeated, trimmed name kept its last value
            Assert.Single(report.GetProperty("metrics").EnumerateObject());
            var earlier = Today.AddDays(-1).ToString("yyyy-MM-dd");   // an explicit day back-fills an earlier day of the goal's life
            Assert.Equal(earlier, JsonDocument.Parse(Assert.IsType<string>(await record.Invoke(new GoalReportInput("Back-filled", earlier), default))).RootElement.GetProperty("day").GetString());
        }
        Assert.Contains("goal_reports.record", GoalReports.WorkerGuidance);
        Assert.Contains("held", GoalReports.WorkerGuidance);
    }
}
