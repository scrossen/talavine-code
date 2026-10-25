using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Agent.ToolUseEngine;
using SwarmCore.Coding.Native;
using SwarmCore.Coding.Permissions;

namespace SwarmCore.Coding.Tests;

public sealed class HostToolBridgeAttemptTests
{
    private sealed record Input;
    private sealed class Resolver(ToolDescriptor tool) : IProfileResolver
    {
        public Func<Task>? BeforeResolve;
        public int Calls;
        public async Task<ResolvedProfile> ResolveAsync(string conversationId, Guid? explicitProfileId = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            if (BeforeResolve is { } wait) await wait();
            return new(new AgentProfileSnapshot { Id = Guid.Empty, Name = "test", SystemPrompt = "", EnabledToolIds = [] }, [tool]);
        }
    }
    private sealed class Guard : IToolInvocationGuard
    {
        public Func<Task>? BeforeInspect;
        public async Task<ToolGuardDecision> InspectAsync(ToolInvocationRequest request, CancellationToken ct = default)
        { if (BeforeInspect is { } wait) await wait(); return ToolGuardDecision.Allow(); }
    }

    [Fact]
    public async Task Replaced_worker_cannot_list_or_call_under_new_goal_lease_and_current_worker_still_can()
    {
        var seen = new List<string?>();
        var tool = ToolDescriptor.Create<Input, string>("goal_messages.reply", "test", "Reply", ToolCostClass.Cheap, ToolSideEffect.ExecutionMetadata,
            (_, _) => { seen.Add(AsyncLocalCurrentTurnContextAccessor.Ambient?.GoalMessageLeaseToken); return Task.FromResult("ok"); });
        var resolver = new Resolver(tool);
        var services = new ServiceCollection().AddSingleton<ActiveCodingWorkspaces>().AddSingleton<ActiveCodingPermissions>()
            .AddSingleton<ICurrentTurnContextAccessor, AsyncLocalCurrentTurnContextAccessor>().AddSingleton<IProfileResolver>(resolver);
        services.AddSwarmCoreHostToolBridge();
        await using var sp = services.BuildServiceProvider();
        var active = sp.GetRequiredService<ActiveCodingWorkspaces>();
        var broker = sp.GetRequiredService<HostToolBridgeBroker>();
        var workspace = new CodingWorkspace { RepositoryRoot = Path.GetTempPath() };
        using var old = active.Enter("chat", workspace, goalId: Guid.NewGuid(), goalMessageLeaseToken: "lease-A");
        var oldCap = active.BridgeCapability("chat")!;
        var oldProxy = new HostToolBridgeProxy(broker.GetEndpoint(), "chat", oldCap);
        Assert.Single(await oldProxy.ListAsync(default));
        using var current = active.Enter("chat", workspace, goalId: Guid.NewGuid(), goalMessageLeaseToken: "lease-B");
        var currentCap = active.BridgeCapability("chat")!;
        Assert.NotEqual(oldCap, currentCap);
        foreach (var proxy in new[] { oldProxy, new HostToolBridgeProxy(broker.GetEndpoint(), "chat"), new HostToolBridgeProxy(broker.GetEndpoint(), "other", currentCap) })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.ListAsync(default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.CallAsync("goal_messages_reply", JsonSerializer.SerializeToElement(new { }), default));
        }
        Assert.Empty(seen);
        Assert.Equal(1, resolver.Calls); // rejected before any current-B profile or ambient lookup
        old.Dispose();
        Assert.False((await new HostToolBridgeProxy(broker.GetEndpoint(), "chat", currentCap).CallAsync("goal_messages_reply", JsonSerializer.SerializeToElement(new { }), default)).IsError);
        Assert.Equal(["lease-B"], seen);
        Assert.Null(AsyncLocalCurrentTurnContextAccessor.Ambient);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replacing_attempt_during_profile_or_guard_await_never_dispatches_tool(bool duringGuard)
    {
        var ran = 0;
        var tool = ToolDescriptor.Create<Input, string>("goal_messages.reply", "test", "Reply", ToolCostClass.Cheap, ToolSideEffect.ExecutionMetadata,
            (_, _) => { Interlocked.Increment(ref ran); return Task.FromResult("ok"); });
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Wait() { reached.TrySetResult(); await release.Task; }
        var resolver = new Resolver(tool) { BeforeResolve = duringGuard ? null : Wait };
        var guard = new Guard { BeforeInspect = duringGuard ? Wait : null };
        var services = new ServiceCollection().AddSingleton<ActiveCodingWorkspaces>().AddSingleton<ActiveCodingPermissions>()
            .AddSingleton<ICurrentTurnContextAccessor, AsyncLocalCurrentTurnContextAccessor>()
            .AddSingleton<IProfileResolver>(resolver).AddSingleton<IToolInvocationGuard>(guard);
        services.AddSwarmCoreHostToolBridge();
        await using var sp = services.BuildServiceProvider();
        var active = sp.GetRequiredService<ActiveCodingWorkspaces>();
        var workspace = new CodingWorkspace { RepositoryRoot = Path.GetTempPath() };
        using var old = active.Enter("chat", workspace, goalId: Guid.NewGuid(), goalMessageLeaseToken: "lease-A");
        var proxy = new HostToolBridgeProxy(sp.GetRequiredService<HostToolBridgeBroker>().GetEndpoint(), "chat", active.BridgeCapability("chat"));
        var call = proxy.CallAsync("goal_messages_reply", JsonSerializer.SerializeToElement(new { }), default);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var current = active.Enter("chat", workspace, goalId: Guid.NewGuid(), goalMessageLeaseToken: "lease-B");
        release.TrySetResult();
        try { Assert.True((await call.WaitAsync(TimeSpan.FromSeconds(5))).IsError); }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or OperationCanceledException) { }
        Assert.Equal(0, ran);
    }

    [Fact]
    public void Capability_is_runtime_only_and_mounted_only_on_own_bridge()
    {
        var turn = new SwarmCore.Coding.Routing.NativeLaneTurn("chat", "Work.", new("native", "CodexNative", "codex-cli:default"), Path.GetTempPath(), "user", false)
            { HostToolBridgeCapability = "opaque-attempt", GoalMessageLeaseToken = "private-lease" };
        Assert.DoesNotContain("opaque-attempt", JsonSerializer.Serialize(turn));
        Assert.DoesNotContain("opaque-attempt", NativeLaneSupport.BuildPrompt(turn, true, null, "AGENTS.md"));
        var bridge = new McpServerLaunch { Command = "host", ServerName = "own" };
        var external = new McpServerLaunch { Command = "external", ServerName = "other" };
        var servers = NativeLaneSupport.MountedServers(bridge, [external], turn.RepositoryRoot, turn.ConversationId, turn.HostToolBridgeCapability);
        Assert.Equal("opaque-attempt", servers.Single(s => !s.IsExternal).Environment[HostToolBridgeBroker.AttemptEnvironmentVariable]);
        Assert.DoesNotContain(HostToolBridgeBroker.AttemptEnvironmentVariable, servers.Single(s => s.IsExternal).Environment.Keys);
        Assert.All(servers, s => Assert.DoesNotContain("private-lease", string.Join(" ", s.Arguments.Concat(s.Environment.Values))));
    }

    [Fact]
    public void Bound_attempt_keeps_its_permission_snapshot_and_cannot_fall_through_to_replacement()
    {
        var active = new ActiveCodingWorkspaces();
        var permissions = new ActiveCodingPermissions();
        using var services = new ServiceCollection().AddSingleton(active).AddSingleton(permissions).BuildServiceProvider();
        var host = new ServiceHostToolBridgeHost(services, _ => HostToolProfile.Full);
        var denied = new List<string> { "danger.*" };
        using var old = active.Enter("chat", new() { RepositoryRoot = Path.GetTempPath() }, goalMessageLeaseToken: "lease-A",
            permissions: new() { Deny = denied });
        var capability = active.BridgeCapability("chat");
        denied.Clear();
        using var binding = host.BeginAttempt("chat", capability);
        Assert.NotNull(binding);
        Assert.Equal(["danger.*"], permissions.Get("chat")!.Deny);
        using var newerPolicy = permissions.Enter("chat", new() { Mode = CodingPermissionMode.Auto });
        using var newer = active.Enter("chat", new() { RepositoryRoot = Path.GetTempPath() });
        Assert.False(host.IsAttemptCurrent("chat"));
        Assert.Null(active.Get("chat"));
        Assert.Null(active.GoalMessageLeaseToken("chat"));
        Assert.Equal(["danger.*"], permissions.Get("chat")!.Deny);
        Assert.True(host.AttemptCancellation("chat").IsCancellationRequested);
    }
}
