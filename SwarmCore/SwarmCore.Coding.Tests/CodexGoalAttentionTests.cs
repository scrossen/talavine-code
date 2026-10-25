using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SwarmCore.Abstractions.Processes;
using SwarmCore.Agent.Communication;
using SwarmCore.Coding.Native;
using SwarmCore.Coding.Routing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmCore.Agent.Runtime;
using SwarmCore.Agent.ToolUseEngine;

namespace SwarmCore.Coding.Tests;

public sealed class CodexGoalAttentionTests
{
    [InstalledNativeGoalFact]
    [Trait("Category", "InstalledNativeGoal")]
    public async Task Installed_codex_admits_message_in_same_turn_after_tool_without_cancelling_or_repeating_action()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var executable = CodexNativeLaneRunner.ResolveExecutable();
        Assert.False(string.IsNullOrWhiteSpace(executable));
        var directory = Path.Combine(Path.GetTempPath(), "goal-native-proof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var mailbox = new Mailbox { MessageText = "Keep the completed counter action. Change the remaining task: write updated-answer.txt containing exactly NEW instead of old-answer.txt. Do not execute the counter command again. Then briefly confirm." };
            var turn = Turn(mailbox) with { RepositoryRoot = directory, Lane = new("native", "CodexNative", "codex-cli:default") };
            var psi = CodexNativeLaneRunner.BuildAppServerStartInfo(executable!, turn, new() { McpServer = null });
            psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("mcp_servers={}");
            using var process = ChildProcessLauncher.Start(NativeLaneSupport.ForLaunch(psi));
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            using var terminate = timeout.Token.Register(() => { try { process.Kill(true); } catch { } });
            var recorder = new RecordingWriter(process.StandardInput);
            await using var connection = new CodexAppServerConnection(process.StandardOutput, recorder, timeout.Token);
            var events = new List<JsonObject>();
            var accepted = new List<GoalDelivery>();
            var enqueued = false;
            await CodexGoalAttention.RunAsync(connection, turn, "isolated-worker",
                "This is an isolated native transport verification. First execute exactly one shell command: printf 'first\\n' >> counter.txt; sleep 4. "
                + "Wait for its completed tool result. Only then, in a separate subsequent tool call, write old-answer.txt containing OLD. "
                + "Do not combine the second operation with the first. No network, credentials, repository changes, or other work. Then finish with a brief confirmation.",
                null, frame =>
                {
                    events.Add(frame);
                    if (!enqueued && (string?)frame["type"] == "item.started" && (string?)frame["item"]?["type"] == "command_execution")
                    { enqueued = true; mailbox.Pending = true; }
                    return Task.CompletedTask;
                }, delivery => { accepted.Add(delivery); return Task.CompletedTask; }, timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var diagnostic = await error;
            if (Environment.GetEnvironmentVariable("SWARMCODE_NATIVE_GOAL_REPORT") is { Length: > 0 } reportPath)
            {
                var report = new
                {
                    Executable = executable,
                    ExecutableSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(executable!, timeout.Token))),
                    CoreSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(typeof(CodexNativeLaneRunner).Assembly.Location, timeout.Token))),
                    process.ExitCode, Frames = recorder.Frames, Events = events,
                    Accepted = accepted, States = mailbox.States,
                    Counter = File.Exists(Path.Combine(directory, "counter.txt")) ? await File.ReadAllLinesAsync(Path.Combine(directory, "counter.txt"), timeout.Token) : [],
                    UpdatedAnswer = File.Exists(Path.Combine(directory, "updated-answer.txt")) ? await File.ReadAllTextAsync(Path.Combine(directory, "updated-answer.txt"), timeout.Token) : null,
                };
                await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), timeout.Token);
            }
            Assert.True(process.ExitCode == 0, diagnostic);
            Assert.Single(accepted);
            Assert.Equal([GoalDeliveryState.Delivered], mailbox.States);
            Assert.Equal(["first"], await File.ReadAllLinesAsync(Path.Combine(directory, "counter.txt"), timeout.Token));
            Assert.Equal("NEW", (await File.ReadAllTextAsync(Path.Combine(directory, "updated-answer.txt"), timeout.Token)).Trim());
            Assert.Single(recorder.Frames.Where(f => (string?)f["method"] == "turn/start"));
            Assert.Single(recorder.Frames.Where(f => (string?)f["method"] == "turn/steer"));
            Assert.DoesNotContain(recorder.Frames, f => (string?)f["method"] == "turn/interrupt");
            Assert.Contains(events, e => (string?)e["type"] == "turn.completed");
        }
        finally { try { Directory.Delete(directory, true); } catch { } }
    }

    private sealed class InstalledNativeGoalFactAttribute : FactAttribute
    {
        public InstalledNativeGoalFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("SWARMCODE_NATIVE_GOAL_PROBE") != "1")
                Skip = "Explicit installed-native subscription qualification only.";
        }
    }

    private sealed class RecordingWriter(TextWriter inner) : TextWriter
    {
        public List<JsonObject> Frames { get; } = [];
        public override Encoding Encoding => inner.Encoding;
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken ct = default)
        {
            Frames.Add(JsonNode.Parse(buffer.ToString())!.AsObject());
            return inner.WriteLineAsync(buffer, ct);
        }
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    }

    [Theory]
    [InlineData("accepted", GoalDeliveryState.Delivered)]
    [InlineData("no-start", GoalDeliveryState.Received)]
    [InlineData("unknown", GoalDeliveryState.Uncertain)]
    public async Task Unsupported_live_transport_delivers_full_input_at_next_turn_with_truthful_outcome(string scenario, GoalDeliveryState expected)
    {
        var mailbox = new Mailbox { Pending = true };
        var sessions = new InMemoryAgentSessionStore();
        var child = new StaticProcess(scenario == "accepted" ? """
            {"type":"thread.started","thread_id":"saved-thread"}
            {"type":"turn.started"}
            {"type":"item.completed","item":{"id":"answer","type":"agent_message","text":"Done"}}
            {"type":"turn.completed"}
            """ : "");
        var calls = 0;
        NativeCliCapabilities.ProbeOverride = (_, _) => null;
        try
        {
            var runner = new CodexNativeLaneRunner(new Conversations(), sessions, Options.Create(new CodingOptions()), NullLogger<CodexNativeLaneRunner>.Instance)
            {
                ExecutableResolver = () => "owned-unqualified-" + Guid.NewGuid().ToString("N"),
                ProcessStarter = _ => { calls++; return scenario == "no-start" ? throw new IOException("Process never started") : child; },
            };
            var events = new List<TurnEvent>();
            await foreach (var item in runner.RunAsync(Turn(mailbox) with { IsContinuation = false })) events.Add(item);
            Assert.Equal(1, calls);
            Assert.Equal([expected], mailbox.States);
            if (expected == GoalDeliveryState.Delivered)
            {
                Assert.Contains("full new message", Encoding.UTF8.GetString(child.Input.ToArray()));
                Assert.Contains(await sessions.LoadAsync("conversation"), m => m.Parts.OfType<SwarmCore.Abstractions.LlmRouting.TextPart>().Any(p => p.Text.Contains("full new message")));
                Assert.Single(events.OfType<TurnCompleted>());
            }
            else Assert.Single(events.OfType<TurnErrored>());
        }
        finally { NativeCliCapabilities.ProbeOverride = null; }
    }

    private sealed class StaticProcess(string output) : IChildProcess
    {
        public MemoryStream Input { get; } = new();
        private StreamWriter? _stdin;
        public StreamWriter StandardInput => _stdin ??= new(Input, Encoding.UTF8, leaveOpen: true);
        public StreamReader StandardOutput { get; } = new(new MemoryStream(Encoding.UTF8.GetBytes(output)));
        public StreamReader StandardError { get; } = new(new MemoryStream());
        public int Id => -1;
        public bool HasExited => true;
        public int ExitCode => 0;
        public event EventHandler? Exited { add { } remove { } }
        public event Action<string?>? OutputLineReceived { add { } remove { } }
        public event Action<string?>? ErrorLineReceived { add { } remove { } }
        public void BeginOutputReadLine() { }
        public void BeginErrorReadLine() { }
        public void Kill(bool entireProcessTree = false) { }
        public bool WaitForExit(TimeSpan timeout) => true;
        public Task WaitForExitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Process? DiagnosticsProcess => null;
        public Process? AttachDiagnosticsProcess() => null;
        public void Dispose() { _stdin?.Dispose(); StandardOutput.Dispose(); StandardError.Dispose(); }
    }

    private sealed class Conversations : IConversationStore
    {
        private readonly Dictionary<string, object> _state = [];
        public Task<IReadOnlyDictionary<string, object>> GetStateAsync(string id, CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<string, object>>(_state);
        public Task UpdateStateAsync(string id, IReadOnlyDictionary<string, object> changes, CancellationToken ct = default) { foreach (var item in changes) _state[item.Key] = item.Value; return Task.CompletedTask; }
        public Task<ChatTurnInfo> AppendUserTurnAsync(string id, string text, IReadOnlyList<TurnImageAttachment>? images = null, IReadOnlyList<TurnFileAttachment>? files = null, CancellationToken ct = default)
            => Task.FromResult(new ChatTurnInfo(Guid.NewGuid(), id, 1, text, null, null, null, DateTime.UtcNow, null));
        public Task CompleteAssistantTurnAsync(Guid id, string text, Guid? profile, TimeSpan duration, string? reasoningContent = null, CancellationToken ct = default, string? metadataJson = null) => Task.CompletedTask;
        public Task<ChatConversationInfo> GetOrCreateAsync(string id, string title, Guid? profileId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChatConversationInfo> CreateAsync(Guid? profileId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ChatConversationInfo?> GetAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ChatConversationInfo>> ListRecentAsync(int take = 25, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetLatestIdAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ChatTurnInfo>> LoadTurnsAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RenameAsync(string id, string title, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetPinnedAsync(string id, bool pinned, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task New_message_queues_in_native_turn_while_tool_runs_and_host_context_waits_for_its_completion()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var wire = new Wire();
        await using var connection = new CodexAppServerConnection(wire.Reader, wire.Writer, timeout.Token);
        var mailbox = new Mailbox();
        var turn = Turn(mailbox);
        var seen = new List<JsonObject>();
        var accepted = new List<GoalDelivery>();
        var run = CodexGoalAttention.RunAsync(connection, turn, "worker", "original", "existing-thread",
            frame => { seen.Add(frame); return Task.CompletedTask; }, d => { accepted.Add(d); return Task.CompletedTask; }, timeout.Token);
        await wire.InitializeAsync("thread/resume", timeout.Token);
        var start = await wire.NextAsync(timeout.Token);
        Assert.Equal("turn/start", (string?)start["method"]);
        Assert.Equal("existing-thread", (string?)start["params"]?["threadId"]);
        Assert.Equal("gpt-5.6", (string?)start["params"]?["model"]);
        Assert.Equal("high", (string?)start["params"]?["effort"]);
        wire.Reply(start, new() { ["turn"] = new JsonObject { ["id"] = "same-turn" } });
        wire.Notify("item/started", new() { ["item"] = new JsonObject { ["id"] = "physical-once", ["type"] = "commandExecution", ["command"] = "save-once" } });
        await WaitUntilAsync(() => seen.Any(f => (string?)f["item"]?["id"] == "physical-once"), timeout.Token);
        wire.Notify("item/started", new() { ["item"] = new JsonObject { ["id"] = "already-admitted", ["type"] = "commandExecution", ["command"] = "other-batch-action" } });
        await WaitUntilAsync(() => seen.Count(f => (string?)f["type"] == "item.started") == 2, timeout.Token);
        mailbox.Pending = true;
        var steer = await wire.NextAsync(timeout.Token);
        Assert.Empty(accepted);
        Assert.Equal("turn/steer", (string?)steer["method"]);
        Assert.Equal("same-turn", (string?)steer["params"]?["expectedTurnId"]);
        Assert.Equal("existing-thread", (string?)steer["params"]?["threadId"]);
        Assert.Contains("full new message", steer.ToJsonString());
        Assert.Null(steer["params"]?["sandboxPolicy"]);
        Assert.Null(steer["params"]?["model"]);
        wire.Reply(steer, new() { ["turnId"] = "same-turn" });
        await WaitUntilAsync(() => mailbox.States.Count == 1, timeout.Token);
        Assert.Empty(accepted); // Native queues input; the host must retain tool/result adjacency.
        wire.Notify("item/completed", new() { ["item"] = new JsonObject { ["id"] = "physical-once", ["type"] = "commandExecution", ["command"] = "save-once", ["exitCode"] = 0, ["aggregatedOutput"] = "saved" } });
        await WaitUntilAsync(() => seen.Count(f => (string?)f["type"] == "item.completed") == 1, timeout.Token);
        Assert.Empty(accepted); // The rest of an already admitted batch is allowed to finish.
        wire.Notify("item/completed", new() { ["item"] = new JsonObject { ["id"] = "already-admitted", ["type"] = "commandExecution", ["command"] = "other-batch-action", ["exitCode"] = 0 } });
        await WaitUntilAsync(() => accepted.Count == 1, timeout.Token);
        wire.Notify("turn/completed", new() { ["turn"] = new JsonObject { ["id"] = "same-turn", ["status"] = "completed" } });
        await run;
        Assert.Single(accepted);
        Assert.Equal([GoalDeliveryState.Delivered], mailbox.States);
        Assert.DoesNotContain(mailbox.States, s => s is GoalDeliveryState.Read or GoalDeliveryState.Incorporated);
        Assert.Equal(2, seen.Count(f => (string?)f["type"] == "item.started"));
        Assert.Equal(2, seen.Count(f => (string?)f["type"] == "item.completed"));
        Assert.False(wire.Outgoing.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Lost_steer_acknowledgement_is_uncertain_and_never_replayed()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var wire = new Wire();
        await using var connection = new CodexAppServerConnection(wire.Reader, wire.Writer, timeout.Token);
        var mailbox = new Mailbox();
        var accepted = new List<GoalDelivery>();
        var run = CodexGoalAttention.RunAsync(connection, Turn(mailbox), "worker", "original", null,
            _ => Task.CompletedTask, d => { accepted.Add(d); return Task.CompletedTask; }, timeout.Token);
        await wire.InitializeAsync("thread/start", timeout.Token);
        var start = await wire.NextAsync(timeout.Token);
        wire.Reply(start, new() { ["turn"] = new JsonObject { ["id"] = "same-turn" } });
        mailbox.Pending = true;
        var steer = await wire.NextAsync(timeout.Token);
        Assert.Equal("turn/steer", (string?)steer["method"]);
        wire.Incoming.Writer.Complete(); // Provider might have accepted; the host cannot know.
        await Assert.ThrowsAnyAsync<Exception>(() => run);
        Assert.Equal([GoalDeliveryState.Uncertain], mailbox.States);
        Assert.Empty(accepted);
        Assert.False(wire.Outgoing.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Continuous_reasoning_notifications_cannot_starve_an_inbox_delivery()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var wire = new Wire();
        await using var connection = new CodexAppServerConnection(wire.Reader, wire.Writer, timeout.Token);
        var mailbox = new Mailbox();
        var observed = 0;
        var run = CodexGoalAttention.RunAsync(connection, Turn(mailbox), "worker", "original", null,
            frame => { if ((string?)frame["type"] == "item.completed") observed++; return Task.CompletedTask; }, _ => Task.CompletedTask, timeout.Token);
        await wire.InitializeAsync("thread/start", timeout.Token);
        wire.Reply(await wire.NextAsync(timeout.Token), new() { ["turn"] = new JsonObject { ["id"] = "same-turn" } });
        mailbox.Pending = true;
        for (var i = 0; i < 1000; i++) wire.Notify("item/completed", new() { ["item"] = new JsonObject { ["id"] = "reasoning-" + i, ["type"] = "reasoning" } });
        var steer = await wire.NextAsync(timeout.Token);
        Assert.Equal("turn/steer", (string?)steer["method"]);
        Assert.InRange(observed, 0, 1); // Admission must not wait for the notification queue to empty.
        wire.Reply(steer, new() { ["turnId"] = "same-turn" });
        wire.Notify("turn/completed", new() { ["turn"] = new JsonObject { ["id"] = "same-turn", ["status"] = "completed" } });
        await run;
        Assert.Equal([GoalDeliveryState.Delivered], mailbox.States);
        Assert.False(wire.Outgoing.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Continuous_nonitem_deltas_cannot_reset_the_wall_clock_attention_tick()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var producing = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var wire = new Wire();
        await using var connection = new CodexAppServerConnection(wire.Reader, wire.Writer, timeout.Token);
        var mailbox = new Mailbox();
        var run = CodexGoalAttention.RunAsync(connection, Turn(mailbox), "worker", "original", null,
            _ => Task.CompletedTask, _ => Task.CompletedTask, timeout.Token);
        await wire.InitializeAsync("thread/start", timeout.Token);
        wire.Reply(await wire.NextAsync(timeout.Token), new() { ["turn"] = new JsonObject { ["id"] = "same-turn" } });
        mailbox.Pending = true;
        var stream = Task.Run(async () =>
        {
            while (!producing.IsCancellationRequested)
            {
                for (var i = 0; i < 100; i++) wire.Notify("item/reasoning/textDelta", new() { ["delta"] = "ongoing reasoning" });
                await Task.Delay(1, producing.Token);
            }
        }, producing.Token);
        try
        {
            var steer = await wire.NextAsync(timeout.Token);
            Assert.Equal("turn/steer", (string?)steer["method"]);
            Assert.False(producing.IsCancellationRequested);
            wire.Reply(steer, new() { ["turnId"] = "same-turn" });
            await WaitUntilAsync(() => mailbox.States.Count == 1, timeout.Token);
        }
        finally
        {
            producing.Cancel();
            try { await stream; } catch (OperationCanceledException) { }
        }
        wire.Notify("turn/completed", new() { ["turn"] = new JsonObject { ["id"] = "same-turn", ["status"] = "completed" } });
        await run;
        Assert.Equal([GoalDeliveryState.Delivered], mailbox.States);
        Assert.False(wire.Outgoing.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Missing_start_turn_identity_holds_the_initial_batch_as_uncertain()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var wire = new Wire();
        await using var connection = new CodexAppServerConnection(wire.Reader, wire.Writer, timeout.Token);
        var mailbox = new Mailbox { Pending = true };
        var run = CodexGoalAttention.RunAsync(connection, Turn(mailbox), "worker", "original", null,
            _ => Task.CompletedTask, _ => throw new InvalidOperationException("No exact admission identity."), timeout.Token);
        await wire.InitializeAsync("thread/start", timeout.Token);
        var start = await wire.NextAsync(timeout.Token);
        Assert.Contains("full new message", start.ToJsonString());
        wire.Reply(start, new() { ["turn"] = new JsonObject() });
        await Assert.ThrowsAsync<InvalidDataException>(() => run);
        Assert.Equal([GoalDeliveryState.Uncertain], mailbox.States);
        Assert.False(wire.Outgoing.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData(-32602, GoalDeliveryState.Received)]
    [InlineData(-32603, GoalDeliveryState.Uncertain)]
    public async Task Only_positive_pre_admission_rejection_can_release_input(int code, GoalDeliveryState expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var wire = new Wire();
        await using var connection = new CodexAppServerConnection(wire.Reader, wire.Writer, timeout.Token);
        var mailbox = new Mailbox();
        var run = CodexGoalAttention.RunAsync(connection, Turn(mailbox), "worker", "original", null,
            _ => Task.CompletedTask, _ => throw new InvalidOperationException("Rejected input was never admitted."), timeout.Token);
        await wire.InitializeAsync("thread/start", timeout.Token);
        var start = await wire.NextAsync(timeout.Token);
        wire.Reply(start, new() { ["turn"] = new JsonObject { ["id"] = "same-turn" } });
        mailbox.Pending = true;
        var steer = await wire.NextAsync(timeout.Token);
        wire.Incoming.Writer.TryWrite(new JsonObject { ["id"] = steer["id"]!.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = "Rejected" } }.ToJsonString());
        await WaitUntilAsync(() => mailbox.States.Count == 1, timeout.Token);
        await Task.Delay(300, timeout.Token);
        Assert.False(wire.Outgoing.Reader.TryRead(out _)); // no blind retry at an unchanged boundary
        wire.Notify("turn/completed", new() { ["turn"] = new JsonObject { ["id"] = "same-turn", ["status"] = "completed" } });
        await run;
        Assert.Equal([expected], mailbox.States);
    }

    [Fact]
    public void Unfamiliar_native_operations_retain_unknown_effect_receipts()
    {
        var item = CodexGoalAttention.NormalizeItem(new() { ["id"] = "new-tool", ["type"] = "futureOperation",
            ["operation"] = "external action", ["status"] = "failed", ["result"] = "partial" });
        var events = Channel.CreateUnbounded<SwarmCore.Agent.Runtime.TurnEvent>();
        var transcript = new List<SwarmCore.Abstractions.LlmRouting.ChatMessage>();
        CodexNativeLaneRunner.HandleEvent(new() { ["type"] = "item.completed", ["item"] = item }, "turn", events.Writer, transcript, new());
        var list = new List<SwarmCore.Agent.Runtime.TurnEvent>();
        while (events.Reader.TryRead(out var progress)) list.Add(progress);
        var started = Assert.Single(list.OfType<SwarmCore.Agent.Runtime.ToolCallStarted>());
        Assert.Equal(CodexNativeLaneRunner.NativeOperationToolName, started.ToolName);
        Assert.Null(started.SideEffect);
        Assert.Equal("futureOperation", (string?)started.Input["nativeType"]);
        Assert.True(Assert.Single(list.OfType<SwarmCore.Agent.Runtime.ToolCallCompleted>()).IsError);
    }

    [Fact]
    public async Task Native_permission_requests_are_rejected_without_user_impersonation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var wire = new Wire();
        await using var connection = new CodexAppServerConnection(wire.Reader, wire.Writer, timeout.Token);
        wire.Incoming.Writer.TryWrite("""{"id":99,"method":"item/commandExecution/requestApproval","params":{}}""");
        var response = await wire.NextAsync(timeout.Token);
        Assert.Equal(99, (int?)response["id"]);
        Assert.Equal(-32601, (int?)response["error"]?["code"]);
        Assert.Null(response["result"]);
    }

    [Fact]
    public void App_server_preserves_existing_mcp_and_permissions_without_mounting_in_confined_mode()
    {
        var turn = Turn(new());
        var psi = CodexNativeLaneRunner.BuildAppServerStartInfo("codex", turn, new());
        Assert.Equal("app-server", psi.ArgumentList[0]);
        Assert.Contains("sandbox_mode='danger-full-access'", psi.ArgumentList);
        Assert.Contains("approval_policy='never'", psi.ArgumentList);
        Assert.Contains(CodexNativeLaneRunner.AppConnectorsOff, psi.ArgumentList);
        Assert.Throws<InvalidOperationException>(() => CodexNativeLaneRunner.BuildAppServerStartInfo("codex", turn with { PlanMode = true }, new()));
        Assert.Throws<InvalidOperationException>(() => CodexNativeLaneRunner.BuildAppServerStartInfo("codex", turn with { Containment = NativeLaneContainment.WorkspaceConfined }, new()));
    }

    private static NativeLaneTurn Turn(Mailbox mailbox) => new("conversation", "original", new("native", "CodexNative", "codex-cli:gpt-5.6@high"), Path.GetTempPath(), "user", true)
        { GoalId = mailbox.GoalId, GoalMailbox = mailbox, GoalMessageLeaseToken = "private-worker-lease" };

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    { while (!condition()) await Task.Delay(10, ct); }

    private sealed class Wire
    {
        public Channel<string> Incoming { get; } = Channel.CreateUnbounded<string>();
        public Channel<string> Outgoing { get; } = Channel.CreateUnbounded<string>();
        public TextReader Reader => new ReaderAdapter(Incoming.Reader);
        public TextWriter Writer => new WriterAdapter(Outgoing.Writer);
        public async Task<JsonObject> NextAsync(CancellationToken ct) => JsonNode.Parse(await Outgoing.Reader.ReadAsync(ct))!.AsObject();
        public void Reply(JsonObject request, JsonObject result) => Incoming.Writer.TryWrite(new JsonObject { ["id"] = request["id"]!.DeepClone(), ["result"] = result }.ToJsonString());
        public void Notify(string method, JsonObject body) { body["threadId"] = "existing-thread"; body["turnId"] = "same-turn"; Incoming.Writer.TryWrite(new JsonObject { ["method"] = method, ["params"] = body }.ToJsonString()); }
        public async Task InitializeAsync(string threadMethod, CancellationToken ct)
        {
            var init = await NextAsync(ct); Assert.Equal("initialize", (string?)init["method"]); Reply(init, new());
            Assert.Equal("initialized", (string?)(await NextAsync(ct))["method"]);
            var thread = await NextAsync(ct); Assert.Equal(threadMethod, (string?)thread["method"]);
            Reply(thread, new() { ["thread"] = new JsonObject { ["id"] = "existing-thread" } });
        }
    }
    private sealed class ReaderAdapter(ChannelReader<string> channel) : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken ct)
        { try { return await channel.ReadAsync(ct); } catch (ChannelClosedException) { return null; } }
    }
    private sealed class WriterAdapter(ChannelWriter<string> channel) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
            => channel.WriteAsync(buffer.ToString(), cancellationToken).AsTask();
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Mailbox : IGoalMailbox, IGoalWorkerDeliverySink
    {
        public Guid GoalId { get; } = Guid.NewGuid();
        public volatile bool Pending;
        public string MessageText { get; init; } = "full new message";
        public List<GoalDeliveryState> States { get; } = [];
        public Task<GoalDelivery?> BeginDeliveryAsync(Guid goalId, string conversationId, string workerId, int maxCharacters = 16000, CancellationToken ct = default)
            => throw new InvalidOperationException("A native worker must use its private bound lease.");
        public Task<GoalDelivery?> BeginDeliveryForWorkerAsync(Guid goalId, string conversationId, string workerId, string expectedLeaseToken, int maxCharacters = 16000, CancellationToken ct = default)
        {
            Assert.Equal("private-worker-lease", expectedLeaseToken);
            if (!Pending) return Task.FromResult<GoalDelivery?>(null);
            Pending = false;
            GoalInboxMessage message = new("message", goalId, 1, GoalMessageKind.Idea, MessageText, new("ui", "ui", "user"), DateTimeOffset.UtcNow, GoalDeliveryState.Presenting, "delivery");
            return Task.FromResult<GoalDelivery?>(new("delivery", goalId, conversationId, workerId, [message], GoalMessaging.FormatDelivery("delivery", [message])));
        }
        public Task RecordDeliveryAsync(string id, GoalDeliveryState state, string? detail = null, CancellationToken ct = default) { States.Add(state); return Task.CompletedTask; }
        public Task<GoalMailboxIdentity> GetIdentityAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<GoalContact>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GoalContact> RenameAsync(Guid id, string name, string handle, long revision, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GoalMessageThread> ReadAsync(Guid id, long afterSequence = 0, int take = 200, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GoalInboxMessage> SendAsync(Guid id, GoalMessageCommand message, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GoalReply> ReplyAsync(Guid id, string conversation, string delivery, string message, string text, GoalDeliveryState state, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
