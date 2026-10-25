using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmCode.Hosting.Goals;
using SwarmCore.Agent.AppControl;
using SwarmCore.Agent.Communication;
using SwarmCore.Agent.Continual;
using SwarmCore.Coding.Goals;

namespace SwarmCode.Hosting.Tests;

public sealed class GoalMailboxBridgeTests : TempDirTest
{
    private async Task<(FileGoalMailbox Mailbox, ContinualObjectiveSnapshot Goal, GoalMailboxServer Server)> Start()
    {
        var goals = new FileCodingGoalStore(Paths);
        var goal = await goals.CreateOrJoinAsync(new ContinualObjectiveStart
        {
            Surface = "coding-goal", PartitionKey = CodingGoalOrchestrator.Partition, IdempotencyKey = "bridge",
            ConversationId = "conversation", ExecutorKey = CodingGoalOrchestrator.ExecutorKey,
            ObjectiveText = "Keep the existing work", ObjectiveSummary = "Existing work", Authority = ContinualObjectiveAuthority.UserRequested("test"),
        }, DateTimeOffset.UtcNow);
        Assert.True(await goals.ControlAsync(goal.Id, goal.Version, CodingGoalControl.Pause, DateTimeOffset.UtcNow));
        var mailbox = new FileGoalMailbox(Paths, goals);
        var directory = new LoopbackDiscoveryDirectory(Path.Combine(Paths.RootDirectory, "owned-discovery"));
        var server = new GoalMailboxServer(mailbox, Paths, NullLogger<GoalMailboxServer>.Instance, directory,
            instanceId => directory.Find(GoalMailboxDiscovery.Prefix + instanceId));
        await server.StartAsync();
        return (mailbox, goal, server);
    }

    private static GoalMailboxRemoteClient Client(GoalMailboxServer server) => new(
        new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }, _ => server.PublishedEndpoint);

    [Fact]
    public async Task Closed_rejection_is_typed_but_lost_ack_retry_still_returns_the_original_receipt()
    {
        var h = await Start();
        await using var server = h.Server;
        using var client = Client(server);
        var identity = (await client.DiscoverAsync())!;
        var command = new GoalMessageCommand("before-close", "Saved before cancellation", GoalMessageKind.Idea,
            new("talavine-ui", "before-close", "local-user"));
        var receipt = await client.SendAsync(identity.InstanceId, h.Goal.Id, command);
        var goals = new FileCodingGoalStore(Paths);
        var current = (await goals.GetAsync(h.Goal.Id))!;
        Assert.True(await goals.ControlAsync(current.Id, current.Version, CodingGoalControl.Cancel, DateTimeOffset.UtcNow));
        Assert.True(Assert.Single(await client.ListAsync(identity.InstanceId)).IsClosed);
        Assert.Equal(receipt, await client.SendAsync(identity.InstanceId, h.Goal.Id, command));
        await Assert.ThrowsAsync<GoalMailboxClosedException>(() => client.SendAsync(identity.InstanceId, h.Goal.Id,
            command with { IdempotencyKey = "after-close", Text = "New work" }));
        Assert.Single((await client.ReadAsync(identity.InstanceId, h.Goal.Id)).Messages);
    }

    [Fact]
    public async Task Repeated_concurrent_start_keeps_one_endpoint_and_disposal_cannot_republish()
    {
        var h = await Start();
        var endpoint = h.Server.PublishedEndpoint;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => h.Server.StartAsync()));
        Assert.Same(endpoint, h.Server.PublishedEndpoint);
        using (var client = Client(h.Server)) Assert.NotNull(await client.DiscoverAsync());
        await h.Server.DisposeAsync();
        await h.Server.DisposeAsync();
        Assert.Null(h.Server.PublishedEndpoint);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => h.Server.StartAsync());
    }

    [Fact]
    public async Task Real_loopback_companion_can_name_message_and_read_same_paused_goal()
    {
        var h = await Start();
        await using var server = h.Server;
        using var client = Client(server);
        var identity = (await client.DiscoverAsync())!;
        var contact = Assert.Single(await client.ListAsync(identity.InstanceId));
        await client.RenameAsync(identity.InstanceId, contact.GoalId, "Pressroom", "pressroom", contact.Revision);
        var command = new GoalMessageCommand("email-stable-id", "A new premise", GoalMessageKind.Idea,
            new("email", "provider-stable-id", "verified-owner", "explicit-owner-account"));
        var first = await client.SendAsync(identity.InstanceId, contact.GoalId, command);
        Assert.Equal(first, await client.SendAsync(identity.InstanceId, contact.GoalId, command));
        var thread = await client.ReadAsync(identity.InstanceId, contact.GoalId);
        Assert.Equal("Paused", thread.Contact.Status);
        Assert.Equal("Pressroom", thread.Contact.Name);
        Assert.Equal(h.Goal.Id, thread.Contact.GoalId);
        Assert.Equal(first, Assert.Single(thread.Messages));
        Assert.Equal(GoalReplyOrigin.HostStatus, Assert.Single(thread.Replies).Origin);
    }

    [Fact]
    public async Task Legacy_companion_receives_no_host_notice_that_it_could_mistake_for_an_agent_email_reply()
    {
        var h = await Start(); await using var server = h.Server;
        var identity = await h.Mailbox.GetIdentityAsync();
        await h.Mailbox.SendAsync(h.Goal.Id, new("legacy", "Why is work paused?", GoalMessageKind.Question,
            new("email", "legacy", "verified-owner")));
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
        var endpoint = server.PublishedEndpoint!;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
        request.Content = JsonContent.Create(new { operation = "read", instanceId = identity.InstanceId, goalId = h.Goal.Id });
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, body.RootElement.GetProperty("thread").GetProperty("replies").GetArrayLength());
        Assert.Equal(1, body.RootElement.GetProperty("thread").GetProperty("messages").GetArrayLength());
        using var current = Client(server);
        Assert.Equal(GoalReplyOrigin.HostStatus, Assert.Single((await current.ReadAsync(identity.InstanceId, h.Goal.Id)).Replies).Origin);
        Assert.True(await h.Mailbox.HasPendingAsync(h.Goal.Id));
    }

    [Fact]
    public async Task Wrong_profile_changed_retry_and_unknown_goal_never_create_messages()
    {
        var h = await Start();
        await using var server = h.Server;
        using var client = Client(server);
        var identity = (await client.DiscoverAsync())!;
        var command = new GoalMessageCommand("stable-id", "Original idea", GoalMessageKind.Idea, new("talavine-ui", "stable-id", "owner"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(Guid.NewGuid().ToString("N"), h.Goal.Id, command));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(identity.InstanceId, Guid.NewGuid(), command));
        await client.SendAsync(identity.InstanceId, h.Goal.Id, command);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(identity.InstanceId, h.Goal.Id, command with { Text = "Changed body" }));
        Assert.Equal("Original idea", Assert.Single((await h.Mailbox.ReadAsync(h.Goal.Id)).Messages).Text);
    }

    [Fact]
    public async Task HTTP_requires_bearer_rejects_cross_origin_and_cannot_resume_or_impersonate_worker()
    {
        var h = await Start();
        await using var server = h.Server;
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
        var endpoint = server.PublishedEndpoint!;
        using (var noBearer = await http.PostAsJsonAsync(endpoint.Url, new { operation = "hello" }))
            Assert.Equal(HttpStatusCode.Unauthorized, noBearer.StatusCode);
        using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
            request.Headers.Add("Origin", "https://unrelated.example");
            request.Content = JsonContent.Create(new { operation = "hello" });
            using var rejected = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        }
        foreach (var operation in new[] { "resume", "reply", "beginDelivery", "execute" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
            request.Content = JsonContent.Create(new { operation, instanceId = (await h.Mailbox.GetIdentityAsync()).InstanceId, goalId = h.Goal.Id });
            using var rejected = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
        Assert.Equal("Paused", Assert.Single(await h.Mailbox.ListAsync()).Status);
        Assert.Empty((await h.Mailbox.ReadAsync(h.Goal.Id)).Messages);
    }

    [Fact]
    public async Task Read_cursor_and_name_conflicts_cross_the_wire_without_losing_a_draft_message()
    {
        var h = await Start();
        await using var server = h.Server;
        using var client = Client(server);
        var identity = (await client.DiscoverAsync())!;
        await client.RenameAsync(identity.InstanceId, h.Goal.Id, "Pressroom", "pressroom", 0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RenameAsync(identity.InstanceId, h.Goal.Id, "Stale", "stale", 0));
        for (var index = 0; index < 3; index++)
            await client.SendAsync(identity.InstanceId, h.Goal.Id, new("id-" + index, "Idea " + index, GoalMessageKind.Idea, new("code-ui", "id-" + index, "local-user")));
        var first = await client.ReadAsync(identity.InstanceId, h.Goal.Id, 0, 2);
        var second = await client.ReadAsync(identity.InstanceId, h.Goal.Id, first.NextSequence, 2);
        var third = await client.ReadAsync(identity.InstanceId, h.Goal.Id, second.NextSequence, 2);
        Assert.True(first.HasMore);
        Assert.True(second.HasMore); Assert.False(third.HasMore);
        Assert.Equal(3, first.Messages.Concat(second.Messages).Concat(third.Messages).Select(message => message.Id).Distinct().Count());
        Assert.Equal(3, first.Replies.Concat(second.Replies).Concat(third.Replies).Select(reply => reply.Id).Distinct().Count());
    }
}
