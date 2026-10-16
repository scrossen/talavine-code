using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Routing;

namespace SwarmCore.Coding.Tests;

/// <summary>
/// Native lane attempts and host acceptance run outside <see cref="AgentTurnRunner"/>, so the orchestrator takes the
/// host's shared <see cref="AgentConversationExecutionGate"/> for them: they wait while another turn owns the
/// conversation, release it on completion or cancellation, and an engine lane (whose runner takes the gate itself)
/// is never wrapped in a second acquisition.
/// </summary>
public sealed class ConversationOwnershipTests
{
    private const string Conversation = "owned-conversation";

    private sealed class Native(List<NativeLaneTurn> seen, TaskCompletionSource started, bool hang) : INativeLaneRunner
    {
        public bool Supports(CodingLane lane) => lane.IsNative;
        public async IAsyncEnumerable<TurnEvent> RunAsync(NativeLaneTurn turn, [EnumeratorCancellation] CancellationToken ct = default)
        {
            lock (seen) seen.Add(turn);
            started.TrySetResult();
            if (hang) await Task.Delay(Timeout.Infinite, ct);
            await Task.Yield();
            yield return new TurnCompleted("n", "native done", TimeSpan.Zero);
        }
    }

    /// <summary>Behaves like <see cref="AgentTurnRunner"/>: owns the conversation for the whole turn.</summary>
    private sealed class OwningRunner(AgentConversationExecutionGate gate) : IAgentTurnRunner
    {
        public async IAsyncEnumerable<TurnEvent> RunAsync(AgentTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await using var owned = await gate.AcquireAsync(request.ConversationId, ct);
            await Task.Yield();
            yield return new TurnCompleted("t", "engine done", TimeSpan.Zero);
        }
    }

    /// <summary>Records whether the conversation was owned (by the orchestrator) while the checks ran.</summary>
    private sealed class OwnershipProbeVerifier(AgentConversationExecutionGate gate) : ICodingAcceptanceVerifier
    {
        public readonly List<bool> OwnedDuringCheck = [];
        public async Task<CodingAcceptanceResult> VerifyAsync(CodingAcceptanceContext context, CancellationToken ct = default)
        {
            using var probe = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            try
            {
                await using var _ = await gate.AcquireAsync(context.ConversationId, probe.Token);
                OwnedDuringCheck.Add(false);
            }
            catch (OperationCanceledException) { OwnedDuringCheck.Add(true); }
            return new CodingAcceptanceResult(CodingAcceptanceStatus.Passed, "ok", []);
        }
    }

    private static (CodingTurnOrchestrator Orchestrator, List<NativeLaneTurn> Native, TaskCompletionSource Started, OwnershipProbeVerifier Verifier)
        Build(AgentConversationExecutionGate gate, string lane, bool hang = false)
    {
        var native = new List<NativeLaneTurn>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verifier = new OwnershipProbeVerifier(gate);
        var services = new ServiceCollection();
        services.AddSingleton(gate);
        services.AddScoped<INativeLaneRunner>(_ => new Native(native, started, hang));
        services.AddScoped<IAgentTurnRunner>(_ => new OwningRunner(gate));
        services.AddSingleton<ICodingAcceptanceVerifier>(verifier);
        services.AddSingleton<ICodingLaneSource>(new OptionsCodingLaneSource(Options.Create(new CodingLaneOptions
        {
            Lanes = [new("claude", "ClaudeCodeNative", "claude-code:default"), new("api", "OpenRouter", "some/model")],
            HandOffOnRefusal = false,
        })));
        services.AddSingleton<ICodingWorkspaceResolver>(new TestOrchestrator.FixedWorkspace(new CodingWorkspace { RepositoryRoot = TestOrchestrator.Workspace }));
        var provider = services.BuildServiceProvider();
        return (new CodingTurnOrchestrator(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new CodingOptions()),
            NullLogger<CodingTurnOrchestrator>.Instance), native, started, verifier);
    }

    private static CodingTurnRequest Request(string lane) => new()
    {
        ConversationId = Conversation, UserMessage = "change it", LaneName = lane,
        CommunicationScope = AgentCommunicationScope.UserRequested("test"), AllowNativeLanes = true,
    };

    private static async Task<List<TurnEvent>> Drain(IAsyncEnumerable<TurnEvent> stream)
    {
        var events = new List<TurnEvent>();
        await foreach (var e in stream) events.Add(e);
        return events;
    }

    [Fact]
    public async Task A_native_attempt_waits_while_another_turn_owns_the_conversation()
    {
        var gate = new AgentConversationExecutionGate();
        var (orchestrator, native, started, _) = Build(gate, "claude");
        var foreground = await gate.AcquireAsync(Conversation); // a chat turn or background continuation is running

        var run = Task.Run(() => Drain(orchestrator.RunAsync(Request("claude"))));
        await Task.Delay(300);
        Assert.Empty(native); // the native agent has not started a second run in the owned conversation
        Assert.False(run.IsCompleted);

        await foreground.DisposeAsync();
        var events = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(native);
        Assert.Contains(events, e => e is TurnCompleted);
    }

    [Fact]
    public async Task Host_acceptance_runs_while_the_orchestrator_owns_the_conversation()
    {
        var gate = new AgentConversationExecutionGate();
        var (orchestrator, _, _, verifier) = Build(gate, "claude");

        await Drain(orchestrator.RunAsync(Request("claude"))).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([true], verifier.OwnedDuringCheck);
        // Released afterwards.
        await using var after = await gate.AcquireAsync(Conversation).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Cancelling_a_native_attempt_releases_the_conversation()
    {
        var gate = new AgentConversationExecutionGate();
        var (orchestrator, native, started, _) = Build(gate, "claude", hang: true);
        using var stop = new CancellationTokenSource();

        var run = Task.Run(() => Drain(orchestrator.RunAsync(Request("claude"), stop.Token)));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // While it runs, another owner has to wait.
        using (var probe = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await gate.AcquireAsync(Conversation, probe.Token));

        stop.Cancel();
        try { await run.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }

        await using var next = await gate.AcquireAsync(Conversation).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(native);
    }

    /// <summary>
    /// The child-containment marker directory the previous-worker check reads for a conversation's repository. A
    /// marker that cannot be proven stopped (here a malformed final marker) is what a running native worker, or one
    /// abandoned by a crashed process, leaves behind.
    /// </summary>
    private static string MarkerDirectory(string root, string conversation)
    {
        var key = new string(SwarmCore.Coding.Native.NativeLaneSupport.WorkloadKey(root, conversation).ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').Take(64).ToArray());
        return Path.Combine(Path.GetTempPath(), "SwarmCore", "child-process-owners", key);
    }

    /// <summary>A native agent whose worker is recorded (as unprovable-stopped) for as long as it runs.</summary>
    private sealed class WorkerRecordingNative(TaskCompletionSource firstStarted, Task releaseFirst) : INativeLaneRunner
    {
        public int Runs;
        public bool Supports(CodingLane lane) => lane.IsNative;
        public async IAsyncEnumerable<TurnEvent> RunAsync(NativeLaneTurn turn, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var run = Interlocked.Increment(ref Runs);
            var directory = MarkerDirectory(turn.RepositoryRoot, turn.ConversationId);
            Directory.CreateDirectory(directory);
            var marker = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".owner.json");
            await File.WriteAllTextAsync(marker, "{ not a marker", ct);
            try
            {
                if (run == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.WaitAsync(ct);
                }
                yield return new TurnCompleted("n" + run, "native done " + run, TimeSpan.Zero);
            }
            finally { File.Delete(marker); }
        }
    }

    [Fact]
    public async Task A_second_message_during_a_native_turn_waits_for_it_and_then_runs()
    {
        var conversation = "second-send-" + Guid.NewGuid().ToString("N");
        var gate = new AgentConversationExecutionGate();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new WorkerRecordingNative(started, release.Task);
        var orchestrator = BuildWith(gate, native);
        var request = Request("claude") with { ConversationId = conversation };

        var first = Task.Run(() => Drain(orchestrator.RunAsync(request)));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = Task.Run(() => Drain(orchestrator.RunAsync(request with { UserMessage = "and also this" })));
        await Task.Delay(400);
        Assert.False(second.IsCompleted); // queued behind the running turn, not rejected

        release.SetResult();
        var firstEvents = await first.WaitAsync(TimeSpan.FromSeconds(10));
        var secondEvents = await second.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(firstEvents, e => e is TurnCompleted { FinalText: "native done 1" });
        Assert.DoesNotContain(secondEvents, e => e is TurnErrored);
        Assert.Contains(secondEvents, e => e is TurnCompleted { FinalText: "native done 2" });
        Assert.Equal(2, native.Runs);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("api")]
    public async Task A_worker_abandoned_by_a_crashed_process_still_blocks_the_next_attempt(string lane)
    {
        var conversation = "abandoned-" + Guid.NewGuid().ToString("N");
        var gate = new AgentConversationExecutionGate();
        var native = new WorkerRecordingNative(new TaskCompletionSource(), Task.CompletedTask);
        var orchestrator = BuildWith(gate, native);
        var directory = MarkerDirectory(TestOrchestrator.Workspace, conversation);
        Directory.CreateDirectory(directory);
        var abandoned = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".owner.json");
        await File.WriteAllTextAsync(abandoned, "{ left by a crashed process");
        try
        {
            var events = await Drain(orchestrator.RunAsync(Request(lane) with { ConversationId = conversation })).WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Contains(events, e => e is TurnErrored { Message: CodingTurnOrchestrator.PreviousWorkerNotStopped });
            Assert.DoesNotContain(events, e => e is TurnCompleted);
            Assert.Equal(0, native.Runs);
            // The ownership taken for the check was released.
            await using var after = await gate.AcquireAsync(conversation).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { File.Delete(abandoned); }
    }

    private static CodingTurnOrchestrator BuildWith(AgentConversationExecutionGate gate, INativeLaneRunner native)
    {
        var services = new ServiceCollection();
        services.AddSingleton(gate);
        services.AddSingleton(native);
        services.AddScoped<IAgentTurnRunner>(_ => new OwningRunner(gate));
        services.AddSingleton<ICodingAcceptanceVerifier>(new OwnershipProbeVerifier(gate));
        services.AddSingleton<ICodingLaneSource>(new OptionsCodingLaneSource(Options.Create(new CodingLaneOptions
        {
            Lanes = [new("claude", "ClaudeCodeNative", "claude-code:default"), new("api", "OpenRouter", "some/model")],
            HandOffOnRefusal = false,
        })));
        services.AddSingleton<ICodingWorkspaceResolver>(new TestOrchestrator.FixedWorkspace(new CodingWorkspace { RepositoryRoot = TestOrchestrator.Workspace }));
        var provider = services.BuildServiceProvider();
        return new CodingTurnOrchestrator(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(new CodingOptions()),
            NullLogger<CodingTurnOrchestrator>.Instance);
    }

    [Fact]
    public async Task An_engine_lane_is_not_wrapped_in_a_second_acquisition()
    {
        var gate = new AgentConversationExecutionGate();
        var (orchestrator, _, _, verifier) = Build(gate, "api");

        // The runner takes the gate itself; a second acquisition around it would deadlock.
        var events = await Drain(orchestrator.RunAsync(Request("api"))).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(events, e => e is TurnCompleted { FinalText: "engine done" });
        Assert.Equal([true], verifier.OwnedDuringCheck);
    }
}
