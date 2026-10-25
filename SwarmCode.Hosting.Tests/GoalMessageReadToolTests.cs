using System.Text.Json;
using SwarmCode.Hosting.Goals;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Communication;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Goals;

namespace SwarmCode.Hosting.Tests;

public sealed class GoalMessageReadToolTests : TempDirTest
{
    private async Task<(FileCodingGoalStore Store, FileGoalMailbox Mailbox, ContinualObjectiveSnapshot Goal,
        ContinualObjectiveLease Lease, ToolDescriptor Tool)> Seed()
    {
        var store = new FileCodingGoalStore(Paths);
        var goal = await store.CreateOrJoinAsync(new ContinualObjectiveStart
        {
            Surface = "coding-goal", PartitionKey = CodingGoalOrchestrator.Partition, IdempotencyKey = "read-tool",
            ConversationId = "conversation", ExecutorKey = CodingGoalOrchestrator.ExecutorKey,
            ObjectiveText = "Preserve progress", ObjectiveSummary = "Preserve progress",
            Authority = ContinualObjectiveAuthority.UserRequested("test"),
        }, DateTimeOffset.UtcNow);
        var lease = (await store.TryClaimAsync(goal.Id, goal.Version, "worker", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5)))!;
        var mailbox = new FileGoalMailbox(Paths, store);
        var catalog = new InMemoryCapabilityCatalog();
        await new GoalReplyToolRegistration(catalog, mailbox, store).StartAsync(default);
        return (store, mailbox, goal, lease, catalog.Get("goal_messages.read")!);
    }

    private sealed class WorkerScope : IDisposable
    {
        private readonly AsyncLocalCurrentTurnContextAccessor _accessor = new();
        private readonly CurrentTurnContext? _prior;
        public WorkerScope(Guid goal, string lease, string conversation = "conversation")
        {
            _prior = _accessor.Current;
            _accessor.Current = new CurrentTurnContext(conversation, null, null)
            { GoalMessageGoalId = goal, GoalMessageLeaseToken = lease };
        }
        public void Dispose() => _accessor.Current = _prior;
    }

    [Fact]
    public async Task Full_old_unicode_sources_can_be_paged_without_mutating_delivery_or_read_receipts()
    {
        var h = await Seed();
        var text = new string('語', GoalMessaging.MaximumMessageCharacters);
        var first = await h.Mailbox.SendAsync(h.Goal.Id, new("one", text, GoalMessageKind.Correction, new("code-ui", "one", "owner")));
        var second = await h.Mailbox.SendAsync(h.Goal.Id, new("two", "Another exact source", GoalMessageKind.Idea, new("code-ui", "two", "owner")));
        using var scope = new WorkerScope(h.Goal.Id, h.Lease.Token);
        var output = Assert.IsType<string>(await h.Tool.Invoke(new GoalReadInput(), default));
        Assert.True(output.Length < 12_000); // UTF-8 text stays text instead of six-character escape sequences.
        using var page = JsonDocument.Parse(output);
        var thread = page.RootElement.GetProperty("thread");
        Assert.Single(thread.GetProperty("messages").EnumerateArray());
        Assert.Equal(text, thread.GetProperty("messages")[0].GetProperty("text").GetString());
        Assert.True(thread.GetProperty("hasMore").GetBoolean());
        var cursor = thread.GetProperty("nextSequence").GetInt64();
        using var next = JsonDocument.Parse(Assert.IsType<string>(await h.Tool.Invoke(new GoalReadInput(cursor), default)));
        Assert.Equal(second.Id, next.RootElement.GetProperty("thread").GetProperty("messages")[0].GetProperty("id").GetString());
        Assert.Equal(1, page.RootElement.GetProperty("messageAuthority")[0].GetProperty("order").GetInt32());
        Assert.Equal(0, page.RootElement.GetProperty("currentObjective").GetProperty("order").GetInt32());
        Assert.All((await h.Mailbox.ReadAsync(h.Goal.Id)).Messages, m => Assert.Equal(GoalDeliveryState.Received, m.State));
        Assert.Empty((await h.Mailbox.ReadAsync(h.Goal.Id)).Replies);
        Assert.Equal(ToolSideEffect.ReadOnly, h.Tool.SideEffect);
        Assert.Equal(first.Id, thread.GetProperty("messages")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Ordinary_chats_foreign_conversations_and_revoked_workers_cannot_read_goal_input()
    {
        var h = await Seed();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Tool.Invoke(new GoalReadInput(), default));
        using (new WorkerScope(h.Goal.Id, h.Lease.Token, "other-conversation"))
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Tool.Invoke(new GoalReadInput(), default));
        var current = (await h.Store.GetAsync(h.Goal.Id))!;
        Assert.True(await h.Store.ControlAsync(current.Id, current.Version, CodingGoalControl.Pause, DateTimeOffset.UtcNow));
        using (new WorkerScope(h.Goal.Id, h.Lease.Token))
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Tool.Invoke(new GoalReadInput(), default));
    }
}
