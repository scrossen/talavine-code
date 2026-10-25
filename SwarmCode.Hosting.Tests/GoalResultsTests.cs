using System.Text.Json;
using SwarmCode.Hosting.Goals;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Communication;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Goals;

namespace SwarmCode.Hosting.Tests;

public sealed class GoalResultsTests : TempDirTest
{
    private async Task<(FileCodingGoalStore Store, FileGoalResults Results, ContinualObjectiveSnapshot Goal, ContinualObjectiveLease Lease)> Seed()
    {
        var store = new FileCodingGoalStore(Paths);
        var goal = await store.CreateOrJoinAsync(new ContinualObjectiveStart
        {
            Surface = "coding-goal", PartitionKey = CodingGoalOrchestrator.Partition, IdempotencyKey = "results",
            ConversationId = "conversation", ExecutorKey = CodingGoalOrchestrator.ExecutorKey,
            ObjectiveText = "Make the product known", ObjectiveSummary = "Make the product known",
            Authority = ContinualObjectiveAuthority.UserRequested("test"),
        }, DateTimeOffset.UtcNow);
        var lease = (await store.TryClaimAsync(goal.Id, goal.Version, "worker", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5)))!;
        return (store, new FileGoalResults(Paths, store), goal, lease);
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

    [Fact]
    public async Task Only_the_bound_worker_records_and_a_later_record_with_the_same_external_id_updates_the_earlier_one()
    {
        var h = await Seed();
        var draft = new GoalResultDraft(GoalResultKind.Post, "  Start here: make three models defend their designs ")
        {
            Url = "https://github.com/scrossen/talavine-code/discussions/8", Audience = "GitHub discussions", Summary = "Pinned onboarding guide.",
            State = GoalResultState.Done, ExternalId = "github-first-run-discussion-20261024", OccurredAtUtc = new DateTimeOffset(2026, 10, 24, 2, 16, 44, TimeSpan.Zero),
        };
        var first = await h.Results.RecordAsync(h.Goal.Id, "conversation", h.Lease.Token, draft);
        Assert.Equal("Start here: make three models defend their designs", first.Title);
        Assert.Equal("agent", first.Source); Assert.Equal(1, first.Revision); Assert.Equal(draft.OccurredAtUtc, first.OccurredAtUtc);
        var updated = await h.Results.RecordAsync(h.Goal.Id, "conversation", h.Lease.Token, draft with { State = GoalResultState.Verified, Summary = null, OccurredAtUtc = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero) });
        Assert.Equal(first.Id, updated.Id); Assert.Equal(2, updated.Revision); Assert.Equal(GoalResultState.Verified, updated.State);
        Assert.Equal("Pinned onboarding guide.", updated.Summary);
        Assert.True(updated.OccurredAtUtc <= DateTimeOffset.UtcNow.AddMinutes(5), "A future time was stored on update.");
        var control = await h.Results.RecordAsync(h.Goal.Id, "conversation", h.Lease.Token, draft with { Url = "https://GitHub.com/scrossen/talavine-code/discussions/8/", ExternalId = null } with { Title = "Same link, other spelling" });
        Assert.NotEqual(first.Id, control.Id); // a different identity (no external id) is its own record even for a similar link
        Assert.Equal("https://github.com/scrossen/talavine-code/discussions/8/", control.Url);
        var page = await h.Results.ListAsync(h.Goal.Id);
        Assert.Equal(2, page.Total); Assert.Equal(GoalResultState.Verified, page.Results.Single(r => r.Id == first.Id).State);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Results.RecordAsync(h.Goal.Id, "conversation", "someone-else", draft));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Results.RecordAsync(h.Goal.Id, "other-conversation", h.Lease.Token, draft));
        var current = (await h.Store.GetAsync(h.Goal.Id))!;
        Assert.True(await h.Store.ControlAsync(h.Goal.Id, current.Version, CodingGoalControl.Pause, DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Results.RecordAsync(h.Goal.Id, "conversation", h.Lease.Token, draft with { ExternalId = "another" }));
        Assert.Equal(2, (await h.Results.ListAsync(h.Goal.Id)).Total);
        Assert.Equal(current.Version + 1, (await h.Store.GetAsync(h.Goal.Id))!.Version); // the ledger never touched the goal row beyond the pause
    }

    [Fact]
    public async Task Drafts_are_validated_and_results_list_newest_first_with_paging()
    {
        var h = await Seed();
        await Assert.ThrowsAsync<ArgumentException>(() => h.Results.RecordAsync(h.Goal.Id, "conversation", h.Lease.Token, new GoalResultDraft(GoalResultKind.Email, " ")));
        await Assert.ThrowsAsync<ArgumentException>(() => h.Results.RecordAsync(h.Goal.Id, "conversation", h.Lease.Token, new GoalResultDraft(GoalResultKind.Post, "x") { Url = "ftp://example" }));
        await Assert.ThrowsAsync<ArgumentException>(() => h.Results.RecordAsync(h.Goal.Id, "conversation", h.Lease.Token, new GoalResultDraft(GoalResultKind.Post, new string('t', 201))));
        var start = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 7; i++)
            await h.Results.RecordAsync(h.Goal.Id, "conversation", h.Lease.Token, new GoalResultDraft(i % 2 == 0 ? GoalResultKind.Post : GoalResultKind.Email, "Result " + i)
                { ExternalId = "r" + i, OccurredAtUtc = start.AddHours(i), Audience = i % 2 == 0 ? "forum" : "editor@example.com" });
        var first = await h.Results.ListAsync(h.Goal.Id, 0, 3);
        Assert.Equal(7, first.Total); Assert.True(first.HasMore);
        Assert.Equal(["Result 6", "Result 5", "Result 4"], first.Results.Select(r => r.Title));
        var last = await h.Results.ListAsync(h.Goal.Id, 6, 3);
        Assert.Single(last.Results); Assert.False(last.HasMore); Assert.Equal("Result 0", last.Results[0].Title);
        Assert.Empty((await h.Results.ListAsync(Guid.NewGuid())).Results);
        Assert.True(File.Exists(Path.Combine(Paths.RootDirectory, "goals", "results.json")));
    }

    [Fact]
    public async Task The_record_tool_requires_the_ambient_worker_and_the_list_tool_reads_back()
    {
        var h = await Seed();
        var catalog = new InMemoryCapabilityCatalog();
        await new GoalReplyToolRegistration(catalog, new FileGoalMailbox(Paths, h.Store), h.Store, h.Results).StartAsync(default);
        var record = catalog.Get("goal_results.record")!; var list = catalog.Get("goal_results.list")!;
        Assert.Equal(ToolSideEffect.ExecutionMetadata, record.SideEffect); Assert.Equal(ToolSideEffect.ReadOnly, list.SideEffect);
        var input = new GoalResultInput("post", "Invited directory PR", "https://github.com/example/awesome/pull/1", "GitHub", "Added the project.", "pending", "pr-1", "2026-10-24T03:19:46Z");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => record.Invoke(input, default));
        using (new WorkerScope(h.Goal.Id, "wrong-lease")) await Assert.ThrowsAsync<UnauthorizedAccessException>(() => record.Invoke(input, default));
        using (new WorkerScope(h.Goal.Id, h.Lease.Token))
        {
            using var saved = JsonDocument.Parse(Assert.IsType<string>(await record.Invoke(input, default)));
            Assert.True(saved.RootElement.GetProperty("saved").GetBoolean());
            Assert.Equal("Pending", saved.RootElement.GetProperty("state").GetString());
            // Rejected drafts come back as feedback, never as a failed (inspection-required) tool call.
            foreach (var bad in new[] { input with { Kind = "tweet" }, input with { OccurredAtUtc = "yesterday" }, input with { Title = " " }, input with { Url = "javascript:alert(1)" } })
            {
                using var refused = JsonDocument.Parse(Assert.IsType<string>(await record.Invoke(bad, default)));
                Assert.False(refused.RootElement.GetProperty("saved").GetBoolean());
                Assert.False(string.IsNullOrWhiteSpace(refused.RootElement.GetProperty("error").GetString()));
            }
            using (new WorkerScope(Guid.NewGuid(), h.Lease.Token))
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Results.RecordAsync(h.Goal.Id, "conversation", h.Lease.Token, new GoalResultDraft(GoalResultKind.Post, "cross-goal")));
            using var listed = JsonDocument.Parse(Assert.IsType<string>(await list.Invoke(new GoalResultsListInput(), default)));
            Assert.Equal(1, listed.RootElement.GetProperty("total").GetInt32());
            Assert.Equal("pr-1", listed.RootElement.GetProperty("results")[0].GetProperty("externalId").GetString());
        }
        Assert.Contains("goal_results.record", GoalResults.WorkerGuidance);
    }
}
