using SwarmCore.Agent.Runtime;
using SwarmCore.Skills.Models;

namespace SwarmCode.Hosting.Tests;

public class UiPermissionBrokerTests
{
    internal static PermissionConsentRequest Request() => new()
    {
        SkillId = "s", SkillName = "Skill", FunctionName = "fn",
        MissingPermissions = [new SkillPermission { PermissionId = "p", Name = "Perm", Level = PermissionLevel.Elevated }],
    };

    [Fact]
    public async Task AutoApprove_ApprovesWithoutSubscribers()
    {
        var broker = new UiPermissionBroker { AutoApprove = true };
        var r = await broker.RequestAsync(Request());
        Assert.True(r.Approved);
        Assert.Empty(broker.PendingRequestIds);
    }

    [Fact]
    public async Task Subscriber_ReceivesRequest_AndRespondResolves()
    {
        var broker = new UiPermissionBroker();
        PendingPermissionRequest? seen = null;
        using var sub = broker.Subscribe(r => seen = r);
        var task = broker.RequestAsync(Request());
        Assert.False(task.IsCompleted);
        Assert.NotNull(seen);
        Assert.Equal(["Perm"], seen!.Permissions.ToArray());
        Assert.Equal(PermissionLevel.Elevated, seen.PermissionLevels[0]);
        Assert.Single(broker.PendingRequestIds);

        broker.Respond(seen.RequestId, true, PermissionGrantScope.Conversation);
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Approved);
        Assert.Equal(PermissionGrantScope.Conversation, result.Scope);
        Assert.Empty(broker.PendingRequestIds);
    }

    [Fact]
    public async Task Denied_ReturnsNotApproved()
    {
        var broker = new UiPermissionBroker();
        using var sub = broker.Subscribe(r => broker.Respond(r.RequestId, false, PermissionGrantScope.Once));
        Assert.False((await broker.RequestAsync(Request()).WaitAsync(TimeSpan.FromSeconds(5))).Approved);
    }

    [Fact]
    public async Task NoSubscriber_NotAutoApprove_DeniesImmediately()
    {
        var broker = new UiPermissionBroker();
        var task = broker.RequestAsync(Request());
        Assert.True(task.IsCompleted);
        Assert.False((await task).Approved);
        Assert.Empty(broker.PendingRequestIds);
    }

    [Fact]
    public async Task Unsubscribe_StopsDelivery()
    {
        var broker = new UiPermissionBroker();
        var sub = broker.Subscribe(_ => { });
        sub.Dispose();
        Assert.False((await broker.RequestAsync(Request())).Approved);
    }

    [Fact]
    public async Task Cancellation_CancelsWait()
    {
        var broker = new UiPermissionBroker();
        using var sub = broker.Subscribe(_ => { });
        using var cts = new CancellationTokenSource();
        var task = broker.RequestAsync(Request(), cts.Token);
        Assert.False(task.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(broker.PendingRequestIds);
    }

    [Fact]
    public async Task ThrowingSubscriber_DoesNotBreakOthers()
    {
        var broker = new UiPermissionBroker();
        using var bad = broker.Subscribe(_ => throw new InvalidOperationException());
        using var good = broker.Subscribe(r => broker.Respond(r.RequestId, true, PermissionGrantScope.Once));
        Assert.True((await broker.RequestAsync(Request()).WaitAsync(TimeSpan.FromSeconds(5))).Approved);
    }

    internal static PermissionConsentRequest Request(string? sessionId, string skillId = "s", string permissionId = "p") => new()
    {
        SkillId = skillId, SkillName = "Skill", FunctionName = "fn", SessionId = sessionId,
        MissingPermissions = [new SkillPermission { PermissionId = permissionId, Name = "Perm", Level = PermissionLevel.Elevated }],
    };

    private static async Task<(PermissionConsentResult Result, int Prompts)> AskAsync(UiPermissionBroker broker, PermissionConsentRequest request, PermissionGrantScope answer)
    {
        var prompts = 0;
        using var sub = broker.Subscribe(r => { prompts++; broker.Respond(r.RequestId, true, answer); });
        var result = await broker.RequestAsync(request).WaitAsync(TimeSpan.FromSeconds(5));
        return (result, prompts);
    }

    [Fact]
    public async Task ConversationGrant_IsRemembered_ForTheSameConversationAndSkill()
    {
        var broker = new UiPermissionBroker();
        var first = await AskAsync(broker, Request("conv-a"), PermissionGrantScope.Conversation);
        Assert.Equal(1, first.Prompts);

        var again = await AskAsync(broker, Request("conv-a"), PermissionGrantScope.Once);
        Assert.Equal(0, again.Prompts);
        Assert.True(again.Result.Approved);
        Assert.True(again.Result.AlreadyGranted);
        Assert.Equal(PermissionGrantScope.Conversation, again.Result.Scope);
    }

    [Fact]
    public async Task ConversationGrant_DoesNotCoverOtherConversations_OtherSkills_OrUnattributedRequests()
    {
        var broker = new UiPermissionBroker();
        await AskAsync(broker, Request("conv-a"), PermissionGrantScope.Conversation);

        Assert.Equal(1, (await AskAsync(broker, Request("conv-b"), PermissionGrantScope.Once)).Prompts);
        Assert.Equal(1, (await AskAsync(broker, Request("conv-a", skillId: "other"), PermissionGrantScope.Once)).Prompts);
        Assert.Equal(1, (await AskAsync(broker, Request("conv-a", permissionId: "q"), PermissionGrantScope.Once)).Prompts);
        Assert.Equal(1, (await AskAsync(broker, Request(null), PermissionGrantScope.Once)).Prompts);
    }

    [Fact]
    public async Task OnceAndUnattributedApprovals_CreateNoGrant()
    {
        var broker = new UiPermissionBroker();
        await AskAsync(broker, Request("conv-a"), PermissionGrantScope.Once);
        await AskAsync(broker, Request(null), PermissionGrantScope.Conversation);
        Assert.False(broker.IsGranted(Request("conv-a")));
        Assert.False(broker.IsGranted(Request(null)));
    }

    [Fact]
    public async Task RevokeGrant_MakesTheNextRequestAskAgain()
    {
        var broker = new UiPermissionBroker();
        string? requestId = null;
        using (broker.Subscribe(r => { requestId = r.RequestId; broker.Respond(r.RequestId, true, PermissionGrantScope.Conversation); }))
            await broker.RequestAsync(Request("conv-a")).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(broker.IsGranted(Request("conv-a")));

        Assert.True(broker.RevokeGrant(requestId!));
        Assert.False(broker.RevokeGrant(requestId!));
        Assert.False(broker.IsGranted(Request("conv-a")));
        Assert.Equal(1, (await AskAsync(broker, Request("conv-a"), PermissionGrantScope.Once)).Prompts);
    }

    [Fact]
    public async Task ClearConversation_ForgetsOnlyThatConversation()
    {
        var broker = new UiPermissionBroker();
        await AskAsync(broker, Request("conv-a"), PermissionGrantScope.Conversation);
        await AskAsync(broker, Request("conv-b"), PermissionGrantScope.Conversation);

        broker.ClearConversation("conv-a");

        Assert.False(broker.IsGranted(Request("conv-a")));
        Assert.True(broker.IsGranted(Request("conv-b")));
    }
}
