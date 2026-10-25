using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SwarmCode.Hosting.Goals;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;
using SwarmCore.Agent.ToolUseEngine;
using SwarmCore.Coding;
using SwarmCore.Coding.Goals;
using SwarmCore.Coding.Native;
using SwarmCore.Coding.Permissions;
using SwarmCore.Coding.Routing;
using SwarmCore.Missions;

namespace SwarmCode.Hosting.Tests;

/// <summary>
/// Attempt 53 of the live growth goal ended with five failed own-host <c>shell_exec</c> calls relayed by the
/// selected native lane; the goal parked in Uncertain and an operator helper had to write the proofs. With the
/// host's own process-completion inspector registered, the same receipts are settled during read-only
/// reconciliation and the goal continues; anything the host cannot vouch for still holds.
/// </summary>
public sealed class OwnHostProcessGoalRecoveryTests : TempDirTest
{
    private sealed class Runner : ICodingTurnOrchestrator
    {
        public IReadOnlyList<CodingLane> Lanes => [];
        public CodingLane ResolveLane(string? name) => new("synthetic", "Synthetic", "synthetic");
        public List<CodingTurnRequest> Requests { get; } = [];
        public async IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            await Task.Yield();
            yield return new ToolCallStarted(request.ConversationId, "fresh-read", "fs.read_file", new JsonObject())
                { SideEffect = SwarmCore.Agent.Catalog.ToolSideEffect.ReadOnly };
            yield return new ToolCallCompleted(request.ConversationId, "fresh-read", "fs.read_file", "Current state read.", false, TimeSpan.Zero)
                { SideEffect = SwarmCore.Agent.Catalog.ToolSideEffect.ReadOnly };
            yield return new TurnCompleted(request.ConversationId, "The remaining work is finished and verified.", TimeSpan.Zero);
        }
    }

    private sealed class Acceptance : IContinualObjectiveAcceptanceService
    {
        public Task<ContinualAcceptanceVerdict> EvaluateAsync(ContinualAcceptanceRequest request, CancellationToken ct = default)
            => Task.FromResult(new ContinualAcceptanceVerdict(ContinualAcceptanceOutcome.Delivered, "Synthetic requested outcome verified."));
    }

    private static string Envelope(int exitCode, string stdout, string stderr = "", bool timedOut = false)
        => JsonSerializer.Serialize(new ShellExecTool.Output { ExitCode = exitCode, Stdout = stdout, Stderr = stderr, TimedOut = timedOut },
            ToolUseAgentEngine.CreateToolJsonOptions());

    private static string Relayed(string text) => new JsonObject
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), ["structuredContent"] = null, ["_meta"] = null,
    }.ToJsonString();

    private static string Input(string server, string file, params string[] arguments) => new JsonObject
    {
        ["server"] = server, ["tool"] = "shell_exec",
        ["arguments"] = new JsonObject { ["fileName"] = file, ["argumentList"] = new JsonArray(arguments.Select(a => (JsonNode?)a).ToArray()), ["timeoutSeconds"] = 15 },
    }.ToJsonString();

    /// <summary>The five attempt-53 shapes: scripts that raised after partial work, a syntax error, and a read-only git query that exits 1.</summary>
    private static ContinualToolReceipt[] Attempt53(Guid goalId, DateTimeOffset now, string fifth = "known")
    {
        ContinualToolReceipt Make(int sequence, string id, string input, string result, ContinualSideEffectClass effect = ContinualSideEffectClass.Exec)
            => new(goalId, 1, sequence, id, CodexNativeLaneRunner.McpToolName, ContinualToolReceiptState.Completed, effect, input, result, true,
                now.AddSeconds(sequence - 10), now.AddSeconds(sequence - 9)) { WasDispatched = true };
        var fifthReceipt = fifth switch
        {
            "known" => Make(5, "exec-e9f967dd", Input("swarmcode", "python3", "-B", "-c", "assert False"), Relayed(Envelope(1, "", "AssertionError"))),
            "timed-out" => Make(5, "exec-timed-out", Input("swarmcode", "python3", "-B", "-c", "import time; time.sleep(60)"), Relayed(Envelope(-1, "", "", timedOut: true))),
            "external-send" => Make(5, "exec-send", Input("swarmcode", "curl", "-X", "POST", "https://example.invalid/hook"), Relayed(Envelope(22, "", "curl: (22)")), ContinualSideEffectClass.ExternalSend),
            "foreign-server" => Make(5, "exec-foreign", Input("other", "git", "status"), Relayed(Envelope(1, "", "fatal"))),
            _ => throw new ArgumentOutOfRangeException(nameof(fifth)),
        };
        return
        [
            Make(1, "exec-d97df21e", Input("swarmcode", "python3", "-B", "-c", "import json\nraise KeyError('schedule')"), Relayed(Envelope(1, "", "Traceback (most recent call last):\n  KeyError: 'schedule'"))),
            Make(2, "exec-2e5e5517", Input("swarmcode", "python3", "-B", "-c", "def (:"), Relayed(Envelope(1, "", "SyntaxError: invalid syntax"))),
            Make(3, "exec-bf6d2bcb", Input("swarmcode", "git", "check-ignore", "-v", "--non-matching", "--", "SwarmCode.Hosting/StarInvitationStore.cs"),
                Relayed(Envelope(1, "::\tSwarmCode.Hosting/StarInvitationStore.cs\n"))),
            Make(4, "exec-51042c4a", Input("swarmcode", "python3", "-B", "-c", "assert 1 == 2"), Relayed(Envelope(1, "", "AssertionError"))),
            fifthReceipt,
        ];
    }

    private async Task<(FileCodingGoalStore Store, CodingTurnRequest Request, ContinualObjectiveSnapshot Goal, ContinualToolReceipt[] Targets)> Seed(
        bool parkedUncertain, string fifth = "known")
    {
        var store = new FileCodingGoalStore(Paths);
        var request = SwarmCodeTurnPolicy.Apply(new CodingTurnRequest
        { ConversationId = "growth-goal", UserMessage = "Keep making verified progress toward the goal." },
            CodingPermissionPolicy.AutoNoRules, "synthetic-local-user");
        var goal = await new CodingGoalOrchestrator(new Runner(), store, new Acceptance()).StartAsync(request);
        var now = DateTimeOffset.UtcNow;
        var lease = (await store.TryClaimAsync(goal.Id, goal.Version, "attempt-53-worker", now, now.AddMinutes(1)))!;
        var targets = Attempt53(goal.Id, now, fifth);
        foreach (var target in targets) Assert.True(await store.UpsertReceiptAsync(goal.Id, lease.Token, target));
        foreach (var target in targets) Assert.True(MissionTransitions.NeedsInspection(target));
        if (parkedUncertain)
        {
            Assert.True(await store.ApplyTransitionAsync(goal.Id, lease.Token, new(ContinualObjectiveStatus.Uncertain,
                now, now, null, "Retained artifact", "Prior operations still have unverified outcomes.", null, null, 0)));
            goal = (await store.GetAsync(goal.Id))!;
            Assert.True(await store.ControlAsync(goal.Id, goal.Version, CodingGoalControl.Resume, DateTimeOffset.UtcNow));
        }
        else
            Assert.True(await store.ApplyTransitionAsync(goal.Id, lease.Token, new(ContinualObjectiveStatus.RetryScheduled,
                now, now, now, "Retained artifact", "Attempt ended after failed commands.", null, null, 0)));
        return (store, request, (await store.GetAsync(goal.Id))!, targets);
    }

    private static CodingGoalOrchestrator Goals(Runner runner, FileCodingGoalStore store)
        => new(runner, store, new Acceptance(), outcomeInspectors: [new OwnHostProcessCompletionInspector()]);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_own_host_commands_are_settled_by_the_host_and_the_goal_continues_without_a_recovery_turn(bool parkedUncertain)
    {
        var h = await Seed(parkedUncertain);
        var runner = new Runner();
        var events = new List<TurnEvent>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await foreach (var item in Goals(runner, h.Store).RunAsync(h.Request, h.Goal.Id, timeout.Token)) events.Add(item);

        var saved = (await new FileCodingGoalStore(Paths).GetAsync(h.Goal.Id))!;
        Assert.Equal(ContinualObjectiveStatus.Completed, saved.Status);
        Assert.Null(saved.LeaseToken);
        // The continuation was an ordinary attempt: no model-driven recovery turn, no native-lane refusal, no hold.
        var continuation = Assert.Single(runner.Requests);
        Assert.False(continuation.PlanMode); Assert.False(continuation.ReviewOnly); Assert.False(continuation.ReadOnlyInspection);
        Assert.DoesNotContain("requiring host inspection", continuation.UserMessage);
        Assert.Contains("Host-verified prior outcomes", continuation.UserMessage);
        Assert.Contains("never replay these operations", continuation.UserMessage);
        if (!parkedUncertain)
            Assert.DoesNotContain(events.OfType<CodingGoalChanged>(), changed => changed.Goal.Status == ContinualObjectiveStatus.Uncertain);
        Assert.Contains(events.OfType<CodingGoalRecoveryProgress>(), progress => progress.Total == 5 && progress.Resolved == 5);

        foreach (var target in h.Targets)
        {
            var original = saved.Receipts.Single(r => r.Attempt == target.Attempt && r.ToolCallId == target.ToolCallId);
            // Original facts are untouched: the proof is additive and bound to the stored bytes.
            Assert.Equal(JsonSerializer.Serialize(target), JsonSerializer.Serialize(original with { HostResolution = null }));
            Assert.True(ContinualReceiptResolutionPolicy.IsResolved(original, saved.Receipts));
            Assert.Equal(ContinualReceiptResolutionOutcome.KnownOutcome, original.HostResolution!.Outcome);
            Assert.Equal(OwnHostProcessCompletionInspector.InspectorIdentity, original.HostResolution.VerifierIdentity);
            Assert.Contains("timed out: false", original.HostResolution.Evidence);
        }
        Assert.Equal(5, saved.Receipts.Count(r => r.ToolName == "host.goal-outcome-inspection" && r.SideEffect == ContinualSideEffectClass.ReadOnly && !r.IsError));
        Assert.DoesNotContain(saved.Receipts, r => ContinualReceiptResolutionPolicy.NeedsInspection(r, saved.Receipts));
    }

    [Theory]
    [InlineData("timed-out")]
    [InlineData("external-send")]
    [InlineData("foreign-server")]
    public async Task Outcomes_the_host_cannot_vouch_for_keep_the_hold_and_recovery_stays_off_native_lanes(string fifth)
    {
        var h = await Seed(parkedUncertain: false, fifth);
        var runner = new Runner();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await foreach (var _ in Goals(runner, h.Store).RunAsync(h.Request, h.Goal.Id, timeout.Token)) { }

        var saved = (await new FileCodingGoalStore(Paths).GetAsync(h.Goal.Id))!;
        Assert.Equal(ContinualObjectiveStatus.Uncertain, saved.Status);
        Assert.Null(saved.LeaseToken);
        foreach (var target in h.Targets.Take(4))
            Assert.True(ContinualReceiptResolutionPolicy.IsResolved(saved.Receipts.Single(r => r.ToolCallId == target.ToolCallId), saved.Receipts));
        var held = saved.Receipts.Single(r => r.ToolCallId == h.Targets[4].ToolCallId);
        Assert.Null(held.HostResolution);
        Assert.True(ContinualReceiptResolutionPolicy.NeedsInspection(held, saved.Receipts));
        Assert.Equal(JsonSerializer.Serialize(h.Targets[4]), JsonSerializer.Serialize(held));
        // Decision record: the model-driven read-only recovery turn keeps the selected route and never admits a
        // native lane. A native "read-only" mode is a CLI promise the host cannot verify and it cannot produce
        // host proofs anyway, so the remaining uncertainty is parked instead of spending a native turn on it.
        var recovery = Assert.Single(runner.Requests);
        Assert.True(recovery.PlanMode); Assert.True(recovery.ReviewOnly);
        Assert.False(recovery.AllowNativeLanes); Assert.True(recovery.RequireSelectedRoute); Assert.False(recovery.ReadOnlyInspection);
        Assert.Contains(h.Targets[4].ToolCallId, recovery.UserMessage);
        Assert.DoesNotContain(h.Targets[0].ToolCallId + ";", recovery.UserMessage);
    }

    [Fact]
    public void The_host_registers_the_own_host_process_inspector_once()
    {
        var services = new ServiceCollection();
        services.AddSwarmCodeGoals();
        services.AddSwarmCodeGoals();
        using var provider = services.BuildServiceProvider();
        var inspector = Assert.Single(provider.GetServices<ICodingGoalOutcomeInspector>());
        Assert.IsType<OwnHostProcessCompletionInspector>(inspector);
        Assert.Equal(OwnHostProcessCompletionInspector.InspectorIdentity, inspector.Identity);
    }
}
