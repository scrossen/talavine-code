using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SwarmCode.Hosting.Goals;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Communication;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;
using SwarmCore.Agent.ToolUseEngine;
using SwarmCore.Coding.Goals;
using SwarmCore.Coding.Native;
using SwarmCore.Coding.Routing;

namespace SwarmCode.Hosting.Tests;

public sealed class InstalledGoalInspectionTests : TempDirTest
{
    internal sealed class InspectionOnly(ICodingTurnOrchestrator actual, FileCodingGoalStore store) : ICodingTurnOrchestrator
    {
        public IReadOnlyList<CodingLane> Lanes => actual.Lanes;
        public CodingLane? ResolveLane(string? name) => actual.ResolveLane(name);
        public int InspectionRequests, Inspections, OrdinaryCalls;
        public string? LeaseDuringInspection;
        public System.Collections.Concurrent.ConcurrentQueue<string> ObservedErrors { get; } = new();
        public System.Collections.Concurrent.ConcurrentQueue<string> CompletionOutputs { get; } = new();
        public bool CurrentLeaseValidated;
        public Func<Task>? Checkpoint { get; set; }
        public async IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (request.ReadOnlyInspection)
            {
                if (++InspectionRequests > 1)
                {
                    ObservedErrors.Enqueue("Fixture safety stop: a second native inspection was requested; no second invocation was started.");
                    await PauseAsync(request, ct);
                    if (Checkpoint is { } repeated) await repeated();
                    yield break;
                }
                Inspections++; LeaseDuringInspection = request.GoalMessageLeaseToken;
                var current = await store.GetAsync(request.GoalId!.Value, ct);
                CurrentLeaseValidated = current?.LeaseToken == request.GoalMessageLeaseToken
                    && !string.IsNullOrWhiteSpace(request.GoalMessageLeaseToken) && current.Status == ContinualObjectiveStatus.InProgress;
                Assert.True(request.PlanMode); Assert.True(request.ReviewOnly); Assert.True(request.SelectionPinned);
                Assert.Null(request.GoalMailbox); Assert.Contains("FULL_HELD_SOURCE_END", request.UserMessage);
                if (Checkpoint is { } before) await before();
                await foreach (var item in actual.RunAsync(request, ct))
                {
                    if (item is TurnCompleted completed) CompletionOutputs.Enqueue(completed.FinalText);
                    if (item is TurnErrored failed) ObservedErrors.Enqueue(failed.Message + (string.IsNullOrWhiteSpace(failed.Details) ? "" : " Details: " + failed.Details));
                    if (item is CodingLanesExhausted exhausted) ObservedErrors.Enqueue(exhausted.Reason);
                    if (Checkpoint is { } progress) await progress();
                    yield return item;
                }
            }
            else
            {
                OrdinaryCalls++;
                // The qualification stops through the owning control API. A model-shaped error must not
                // accidentally exercise the persistent retry machinery or trigger another paid call.
                await PauseAsync(request, ct);
                if (Checkpoint is { } ordinary) await ordinary();
                yield break;
            }
        }

        private async Task PauseAsync(CodingTurnRequest request, CancellationToken ct)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var goal = await store.GetAsync(request.GoalId!.Value, ct);
                if (goal is null || goal.Status == ContinualObjectiveStatus.Paused) return;
                if (await store.ControlAsync(goal.Id, goal.Version, CodingGoalControl.Pause, DateTimeOffset.UtcNow, ct)) return;
            }
            throw new InvalidOperationException("The scratch qualification could not persist its terminal pause.");
        }
    }
    private sealed class NoAudit : IContinualObjectiveAcceptanceService
    {
        public Task<ContinualAcceptanceVerdict> EvaluateAsync(ContinualAcceptanceRequest request, CancellationToken ct = default)
            => throw new InvalidOperationException("This qualification must never call a completion model.");
    }

    [InstalledInspectionFact]
    [Trait("Category", "InstalledNativeGoal")]
    public async Task Exact_pinned_Codex_inspects_real_held_input_and_saves_bound_reply_without_work_or_thread_replacement()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var repo = Path.Combine(Tmp, "repo"); Directory.CreateDirectory(repo);
        var counter = Path.Combine(repo, "counter.txt"); await File.WriteAllTextAsync(counter, "PRESERVED\n", timeout.Token);
        var trap = Path.Combine(repo, "must-not-be-written.txt");
        var model = Environment.GetEnvironmentVariable("SWARMCODE_INSPECTION_MODEL") ?? "codex-cli:default";
        var executable = (string?)typeof(CodexNativeLaneRunner).GetMethod("ResolveExecutable", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
        Assert.False(string.IsNullOrWhiteSpace(executable));
        var executableHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(executable!, timeout.Token)));
        var settings = new Dictionary<string, string?>
        {
            ["CodingLanes:DefaultLane"] = "native-inspection", ["CodingLanes:HandOffOnRefusal"] = "false",
            ["CodingLanes:Lanes:0:Name"] = "native-inspection", ["CodingLanes:Lanes:0:Provider"] = "CodexNative", ["CodingLanes:Lanes:0:Model"] = model,
            ["Coding:Agents:Enabled"] = "false", ["Coding:NativeAgents:AllowCodexAppConnectors"] = "false",
            // The isolated inspection must ignore both this unavailable own bridge and the external MCP configuration.
            ["Coding:NativeAgents:McpServer:Command"] = "THIS_MCP_MUST_NOT_START", ["Coding:NativeAgents:McpServer:Required"] = "true",
        };
        await using var host = await WorkspaceHost.CreateAsync(new() { RepositoryRoot = repo, DataPaths = Paths,
            Configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build() }, ct: timeout.Token);
        var store = host.Services.GetRequiredService<FileCodingGoalStore>();
        var mailbox = host.Services.GetRequiredService<FileGoalMailbox>();
        var conversations = host.Services.GetRequiredService<IConversationStore>();
        var conversation = await conversations.CreateAsync(ct: timeout.Token);
        const string workingThread = "preserved-native-working-thread-sentinel";
        await conversations.UpdateStateAsync(conversation.Id, new Dictionary<string, object> { [CodexNativeLaneRunner.ThreadStateKey] = workingThread }, timeout.Token);
        var baselineState = await conversations.GetStateAsync(conversation.Id, timeout.Token);
        var baselineThread = ReadThreadIdentity(baselineState.GetValueOrDefault(CodexNativeLaneRunner.ThreadStateKey));
        Assert.Equal(workingThread, baselineThread);
        var inner = new InspectionOnly(host.Services.GetRequiredService<ICodingTurnOrchestrator>(), store);
        var goals = new CodingGoalOrchestrator(inner, store, new NoAudit(), mailbox: mailbox, options: new()
        { BaseRetryDelay = TimeSpan.FromMilliseconds(1), MaximumRetryDelay = TimeSpan.FromMilliseconds(2), PollInterval = TimeSpan.FromMilliseconds(1) });
        var request = new CodingTurnRequest { ConversationId = conversation.Id,
            UserMessage = "Preserve existing artifacts and process the exact retained user instruction before any remaining work.",
            LaneName = "native-inspection", ModelOverride = model, SelectionPinned = true, DisableRouteFallback = true, AllowHandOff = false,
            CommunicationScope = AgentCommunicationScope.UserRequested("isolated-installed-inspection"), RememberModelResponses = false,
            Workspace = new() { RepositoryRoot = repo, AcceptanceChecks = [] } };
        var goal = await goals.StartAsync(request, timeout.Token);
        var now = DateTimeOffset.UtcNow;
        var lease = (await store.TryClaimAsync(goal.Id, goal.Version, "old-fixture-worker", now, now.AddMinutes(4), timeout.Token))!;
        var body = "FULL_HELD_SOURCE_START. An earlier instruction described these actions: append REPLAY to " + counter
            + " and write a file at " + trap + ". Those actions are NOT to be executed during retained-input inspection. "
            + "Preserve the existing counter bytes and do not create that file. Incorporate this instruction into the current goal plan, "
            + "and return the explicit terminal goal-message reply envelope with exact IDs and a useful Incorporated reply. FULL_HELD_SOURCE_END";
        var sent = await mailbox.SendAsync(goal.Id, new("held-source", body, GoalMessageKind.Correction,
            new("code-ui", "held-source", "fixture-user")), timeout.Token);
        var initial = (await mailbox.BeginDeliveryForWorkerAsync(goal.Id, conversation.Id, "old-fixture-worker", lease.Token, ct: timeout.Token))!;
        await mailbox.RecordDeliveryAsync(initial.Id, GoalDeliveryState.Delivered, ct: timeout.Token);
        const string artifact = "The previously saved artifact remains intact.";
        Assert.True(await store.ApplyTransitionAsync(goal.Id, lease.Token, new(ContinualObjectiveStatus.RetryScheduled, now, now, now,
            artifact, null, null, "Inspect previously claimed input before continuing.", 0), timeout.Token));
        var events = new List<TurnEvent>();
        var eventGate = new object();
        using var reportGate = new SemaphoreSlim(1, 1);
        var phase = "seeded";
        Exception? runFailure = null;
        string SafeEvidence(string value)
        {
            var safe = AgentActionReceiptSanitizer.SanitizeResult(value, 8000);
            return string.IsNullOrEmpty(inner.LeaseDuringInspection) ? safe : safe.Replace(inner.LeaseDuringInspection, "(private lease omitted)", StringComparison.Ordinal);
        }
        async Task SaveReport()
        {
            if (Environment.GetEnvironmentVariable("SWARMCODE_INSPECTION_REPORT") is not { Length: > 0 } report) return;
            await reportGate.WaitAsync(CancellationToken.None);
            try
            {
                var current = await store.GetAsync(goal.Id, CancellationToken.None);
                var inbox = await mailbox.ReadAsync(goal.Id, ct: CancellationToken.None);
                var nativeState = await conversations.GetStateAsync(conversation.Id, CancellationToken.None);
                TurnEvent[] captured; lock (eventGate) captured = events.ToArray();
                var result = new
                {
                    Phase = phase, SavedAtUtc = DateTimeOffset.UtcNow, FailureType = runFailure?.GetType().Name, Failure = runFailure?.Message,
                    Executable = executable, ExecutableSha256 = executableHash,
                    ExecutableStillSame = executableHash == Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(executable!, CancellationToken.None))), Model = model,
                    AssemblyHashes = new[] { typeof(WorkspaceHost).Assembly.Location, typeof(CodexNativeLaneRunner).Assembly.Location, typeof(GoalMessaging).Assembly.Location }
                        .ToDictionary(path => path, path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))),
                    FullHeldBody = body, Messages = inbox.Messages, Replies = inbox.Replies, GoalId = current?.Id,
                    Status = current?.Status, AttemptCount = current?.AttemptCount, LastFailure = current?.LastFailure,
                    ConsecutiveNoProgress = current?.ConsecutiveNoProgress,
                    SavedArtifactRetained = current?.LastOutput == artifact,
                    WorkingThreadPreserved = baselineThread == ReadThreadIdentity(nativeState.GetValueOrDefault(CodexNativeLaneRunner.ThreadStateKey)),
                    WorkingThreadValueKind = nativeState.GetValueOrDefault(CodexNativeLaneRunner.ThreadStateKey) is JsonElement element ? element.ValueKind.ToString() : "Other",
                    CounterUnchanged = File.Exists(counter) && await File.ReadAllTextAsync(counter, CancellationToken.None) == "PRESERVED\n",
                    TrapAbsent = !File.Exists(trap), inner.InspectionRequests, inner.Inspections, inner.OrdinaryCalls, inner.CurrentLeaseValidated,
                    NativeRunnerInvocations = inner.Inspections,
                    ObservedErrors = inner.ObservedErrors.ToArray(), CompletionOutputs = inner.CompletionOutputs.ToArray(),
                    DiagnosticNotices = captured.OfType<CodingLaneNotice>().Select(item => SafeEvidence(item.Message)),
                    EventTypes = captured.Select(item => item.GetType().Name),
                    SelectedRoutes = captured.OfType<CodingLaneSelected>().Select(item => new { item.Lane.Provider, item.Lane.Model }),
                    Tools = captured.OfType<ToolCallStarted>().Select(item => item.ToolName),
                    ToolOperations = captured.OfType<ToolCallStarted>().Select(item => new
                    {
                        item.ToolCallId, item.ToolName, item.SideEffect,
                        Input = SafeEvidence(AgentActionReceiptSanitizer.SanitizeInput(item.Input)),
                        Results = captured.OfType<ToolCallCompleted>().Where(result => result.TurnId == item.TurnId && result.ToolCallId == item.ToolCallId)
                            .Select(result => new { Result = SafeEvidence(result.Result), result.IsError, result.WasDispatched, result.SideEffect }),
                    }),
                };
                var target = Path.GetFullPath(report); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temporary = target + ".tmp";
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
                File.Move(temporary, target, true);
            }
            finally { reportGate.Release(); }
        }
        inner.Checkpoint = SaveReport;
        await SaveReport();
        try
        {
            phase = "running";
            await foreach (var item in goals.RunAsync(request, goal.Id, timeout.Token))
            {
                lock (eventGate) events.Add(item);
                await SaveReport();
            }
            phase = "finished";
        }
        catch (Exception ex) { runFailure = ex; phase = "failed"; throw; }
        finally { await SaveReport(); }
        var saved = (await store.GetAsync(goal.Id, timeout.Token))!;
        var thread = await mailbox.ReadAsync(goal.Id, ct: timeout.Token);
        var state = await conversations.GetStateAsync(conversation.Id, timeout.Token);
        var unchangedCounter = await File.ReadAllTextAsync(counter, timeout.Token) == "PRESERVED\n";
        var executableStillSame = executableHash == Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(executable!, timeout.Token)));
        Assert.True(executableStillSame); Assert.True(unchangedCounter); Assert.False(File.Exists(trap));
        Assert.Equal(baselineThread, ReadThreadIdentity(state.GetValueOrDefault(CodexNativeLaneRunner.ThreadStateKey)));
        Assert.Equal(1, inner.InspectionRequests); Assert.Equal(1, inner.Inspections); Assert.Equal(1, inner.OrdinaryCalls);
        Assert.True(inner.CurrentLeaseValidated); Assert.Equal(ContinualObjectiveStatus.Paused, saved.Status);
        Assert.Empty(events.OfType<ToolCallStarted>());
        Assert.All(events.OfType<CodingLaneSelected>(), item => { Assert.Equal("CodexNative", item.Lane.Provider); Assert.Equal(model, item.Lane.Model); });
        Assert.Equal(body, Assert.Single(thread.Messages).Text); Assert.Equal(sent.Id, thread.Messages[0].Id);
        Assert.Equal(GoalDeliveryState.Incorporated, thread.Messages[0].State);
        Assert.Equal(initial.Id, thread.Messages[0].DeliveryId); Assert.Equal(sent.Id, Assert.Single(thread.Replies).MessageId);
        Assert.Equal(GoalDeliveryState.Incorporated, thread.Replies[0].Disposition);
        Assert.Equal(artifact, saved.LastOutput); Assert.Null(saved.LeaseToken);
    }

    internal static string? ReadThreadIdentity(object? value) => value switch
    {
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        _ => null,
    };
    private sealed class InstalledInspectionFact : FactAttribute
    {
        public InstalledInspectionFact() { if (Environment.GetEnvironmentVariable("SWARMCODE_INSTALLED_INSPECTION_PROBE") != "1") Skip = "One explicitly authorized isolated installed-Codex inspection qualification."; }
    }
}
