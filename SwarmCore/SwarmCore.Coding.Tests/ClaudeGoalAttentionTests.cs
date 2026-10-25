using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Abstractions.Processes;
using SwarmCore.Agent.Communication;
using SwarmCore.Agent.Runtime;
using SwarmCore.Agent.ToolUseEngine;
using SwarmCore.Coding.Native;
using SwarmCore.Coding.Routing;

namespace SwarmCore.Coding.Tests;

public sealed class ClaudeGoalAttentionTests
{
    [InstalledClaudeGoalFact]
    [Trait("Category", "InstalledNativeGoal")]
    public async Task Installed_claude_hook_changes_remaining_work_in_same_turn_without_replaying_completed_action()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var executable = ClaudeCodeNativeLaneRunner.ResolveExecutable();
        Assert.NotNull(executable);
        using (var versionProcess = ChildProcessLauncher.Start(NativeLaneSupport.TurnStartInfo(executable, ["--version"], Path.GetTempPath())))
        {
            versionProcess.StandardInput.Close();
            var version = await versionProcess.StandardOutput.ReadToEndAsync(timeout.Token);
            var diagnostic = await versionProcess.StandardError.ReadToEndAsync(timeout.Token);
            await versionProcess.WaitForExitAsync(timeout.Token);
            Assert.True(await ClaudeGoalAttentionHooks.IsSupportedAsync(executable, timeout.Token), $"Executable: {executable}; exit: {versionProcess.ExitCode}; version: {version}; diagnostic: {diagnostic}");
        }
        var helper = new McpServerLaunch
        {
            Command = Environment.GetEnvironmentVariable("SWARMCODE_NATIVE_CLAUDE_DOTNET")!,
            Arguments = [Environment.GetEnvironmentVariable("SWARMCODE_NATIVE_CLAUDE_HELPER_DLL")!, "--mcp-server"],
        };
        Assert.True(await ClaudeGoalAttentionHooks.IsHelperSupportedAsync(helper, timeout.Token));
        var directory = Path.Combine(Path.GetTempPath(), "claude-goal-proof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var events = new List<JsonObject>(); var checkpoints = new List<string>(); var accepted = new List<GoalDelivery>();
        try
        {
            var mailbox = new Mailbox { Text = "Keep the completed counter action. Change the remaining task: execute exactly one diagnostic shell command false (its failure is expected), then write updated-answer.txt containing exactly NEW instead of old-answer.txt. Do not execute the counter command again. Then briefly confirm." };
            var turn = Turn(mailbox) with { RepositoryRoot = directory, ExecutionLimits = new() { MaxIterations = 8 } };
            await using var broker = ClaudeGoalAttentionHooks.Create(turn, "isolated-native-worker", null, helper, d => { accepted.Add(d); return Task.CompletedTask; })!;
            broker.CheckpointObserved = name => checkpoints.Add(name);
            var psi = ClaudeCodeNativeLaneRunner.BuildStartInfo(executable, turn, null, goalSettingsPath: broker.SettingsPath);
            psi.ArgumentList.Add("--strict-mcp-config"); psi.ArgumentList.Add("--mcp-config"); psi.ArgumentList.Add("{\"mcpServers\":{}}");
            psi.ArgumentList.Add("--setting-sources"); psi.ArgumentList.Add("");
            psi.ArgumentList.Add("--tools"); psi.ArgumentList.Add("Bash,Write,Read");
            using var process = ChildProcessLauncher.Start(NativeLaneSupport.ForLaunch(psi));
            using var stop = timeout.Token.Register(() => { try { process.Kill(true); } catch { } });
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.WriteAsync("This is an isolated native transport verification. First execute exactly one shell command: printf 'first\\n' >> counter.txt; sleep 3. Wait for its completed tool result. Only then, in a separate subsequent tool call, write old-answer.txt containing OLD. Do not combine the second operation with the first. No network, credentials, repository changes, or other work. Then finish with a brief confirmation.");
            process.StandardInput.Close();
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (JsonNode.Parse(line) is not JsonObject frame) continue;
                events.Add(frame);
                if ((string?)frame["session_id"] is { Length: > 0 } session) broker.BindSession(session);
                if ((string?)frame["type"] == "assistant" && frame["message"]?["content"] is JsonArray blocks
                    && blocks.OfType<JsonObject>().Any(b => (string?)b["type"] == "tool_use" && (string?)b["name"] == "Bash"))
                    if (mailbox.Claims == 0) mailbox.Pending = true;
            }
            await process.WaitForExitAsync(timeout.Token);
            var diagnostic = await error;
            await broker.DisposeAsync();
            if (Environment.GetEnvironmentVariable("SWARMCODE_NATIVE_CLAUDE_REPORT") is { Length: > 0 } report)
            {
                var hashes = new Dictionary<string, string>();
                foreach (var path in new[] { executable, helper.Arguments[0], typeof(ClaudeGoalAttentionHooks).Assembly.Location })
                    hashes[path] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path, timeout.Token)));
                await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
                {
                    Executable = executable, Hashes = hashes, process.ExitCode, Events = events, Checkpoints = checkpoints,
                    Accepted = accepted, States = mailbox.States,
                    Counter = File.Exists(Path.Combine(directory, "counter.txt")) ? await File.ReadAllLinesAsync(Path.Combine(directory, "counter.txt"), timeout.Token) : [],
                    UpdatedAnswer = File.Exists(Path.Combine(directory, "updated-answer.txt")) ? await File.ReadAllTextAsync(Path.Combine(directory, "updated-answer.txt"), timeout.Token) : null,
                    OldAnswerExists = File.Exists(Path.Combine(directory, "old-answer.txt")),
                    Diagnostic = diagnostic,
                }, new JsonSerializerOptions { WriteIndented = true }), timeout.Token);
            }
            Assert.True(process.ExitCode == 0, diagnostic);
            Assert.Single(accepted); Assert.Equal([GoalDeliveryState.Delivered], mailbox.States);
            Assert.Equal(["first"], await File.ReadAllLinesAsync(Path.Combine(directory, "counter.txt"), timeout.Token));
            Assert.Equal("NEW", (await File.ReadAllTextAsync(Path.Combine(directory, "updated-answer.txt"), timeout.Token)).Trim());
            Assert.False(File.Exists(Path.Combine(directory, "old-answer.txt")));
            Assert.Contains("PostToolUse", checkpoints); Assert.Contains("PostToolUseFailure", checkpoints); Assert.Contains("Stop", checkpoints);
            Assert.Contains(events, f => (string?)f["type"] == "result" && (string?)f["subtype"] == "success");
        }
        finally { try { Directory.Delete(directory, true); } catch { } }
    }

    private sealed class InstalledClaudeGoalFactAttribute : FactAttribute
    {
        public InstalledClaudeGoalFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("SWARMCODE_NATIVE_CLAUDE_GOAL_PROBE") != "1")
                Skip = "Explicit installed-Claude subscription qualification only.";
        }
    }

    private static McpServerLaunch Launch => new() { Command = "/path with spaces/host", Arguments = ["/path with spaces/console.dll", "--mcp-server", "--repo", "/not-copied"], Environment = new() { ["SECRET"] = "not-copied" } };
    private static NativeLaneTurn Turn(Mailbox mailbox) => new("conversation", "original", new("native", "ClaudeCodeNative", "claude-code:default"), Path.GetTempPath(), "user", false)
        { GoalId = mailbox.GoalId, GoalMailbox = mailbox };
    private static string Input(string name = "PostToolBatch", string session = "native-session", string? agent = null)
        => new JsonObject { ["hook_event_name"] = name, ["session_id"] = session, ["cwd"] = Path.GetTempPath(), ["agent_id"] = agent }.ToJsonString();

    [Fact]
    public void Settings_use_exec_argv_and_never_copy_mcp_arguments_or_environment()
    {
        var settings = JsonNode.Parse(ClaudeGoalAttentionHooks.BuildSettings(Launch, "private-endpoint"))!;
        Assert.Equal(4, settings["hooks"]!.AsObject().Count);
        foreach (var entry in settings["hooks"]!.AsObject())
        {
            var hook = entry.Value![0]!["hooks"]![0]!;
            Assert.Equal(Launch.Command, (string?)hook["command"]);
            Assert.Equal(["/path with spaces/console.dll", "--goal-message-hook", "private-endpoint"], hook["args"]!.AsArray().Select(a => (string)a!).ToArray());
        }
        Assert.DoesNotContain("not-copied", settings.ToJsonString());
        Assert.DoesNotContain("decision", settings.ToJsonString());
    }

    [Theory]
    [InlineData(false, NativeLaneContainment.Bypass, true)]
    [InlineData(true, NativeLaneContainment.Bypass, false)]
    [InlineData(false, NativeLaneContainment.WorkspaceConfined, false)]
    public void Attention_settings_never_change_confinement_or_plan_contract(bool plan, NativeLaneContainment containment, bool expected)
    {
        var turn = Turn(new()) with { PlanMode = plan };
        var start = ClaudeCodeNativeLaneRunner.BuildStartInfo("claude", turn, null, containment: containment, restrictedSupported: true, goalSettingsPath: "attention.json");
        Assert.Equal(expected, start.ArgumentList.Contains("attention.json"));
        if (plan) Assert.Contains("plan", start.ArgumentList);
        if (containment == NativeLaneContainment.WorkspaceConfined) Assert.Contains("--restricted", start.ArgumentList);
    }

    [Theory]
    [InlineData("2.1.302 (Claude Code)", true)]
    [InlineData("2.1.281 (Claude Code)", false)]
    [InlineData("2.1.280", false)]
    [InlineData("2.2.0", true)]
    [InlineData("3.0.0", false)]
    [InlineData("unknown", false)]
    public void Unqualified_protocol_versions_keep_next_turn_fallback(string version, bool supported)
        => Assert.Equal(supported, ClaudeGoalAttentionHooks.SupportsVersion(version));

    // Fill the complete delivery budget, including current host guidance and identity framing.
    // A fixed 8000-character body can exceed the hook's 9500-character claim budget.
    private static string MessageAtHookContextBudget(char character)
    {
        var empty = new GoalInboxMessage("message", Guid.Empty, 1, GoalMessageKind.Idea, "",
            new("ui", "ui", "user"), DateTimeOffset.UtcNow, GoalDeliveryState.Presenting, "delivery");
        var overhead = GoalMessaging.FormatDelivery("delivery", [empty]).Length;
        return new string(character, Math.Min(GoalMessaging.MaximumMessageCharacters, ClaudeGoalAttentionHooks.MaxContextCharacters - overhead));
    }

    [Theory]
    [InlineData("PostToolBatch")]
    [InlineData("PostToolUse")]
    [InlineData("PostToolUseFailure")]
    [InlineData("Stop")]
    public async Task Bound_helper_outputs_complete_context_then_records_transport_only(string hookEvent)
    {
        var mailbox = new Mailbox { Pending = true, Text = MessageAtHookContextBudget('界') };
        var accepted = new List<GoalDelivery>();
        await using var broker = ClaudeGoalAttentionHooks.Create(Turn(mailbox), "worker", "native-session", Launch, d => { accepted.Add(d); return Task.CompletedTask; })!;
        var output = new StringWriter();
        Assert.Equal(0, await ClaudeGoalAttentionHooks.RunHelperAsync(broker.Endpoint, new StringReader(Input(hookEvent)), output));
        var value = JsonNode.Parse(output.ToString())!;
        Assert.Equal(hookEvent, (string?)value["hookSpecificOutput"]?["hookEventName"]);
        Assert.Contains(mailbox.Text, (string?)value["hookSpecificOutput"]?["additionalContext"]);
        Assert.True(output.ToString().Length <= ClaudeGoalAttentionHooks.MaxOutputCharacters);
        Assert.Equal([GoalDeliveryState.Delivered], mailbox.States);
        Assert.Single(accepted);
        Assert.DoesNotContain("decision", output.ToString());
        Assert.DoesNotContain("continue", value.AsObject().Select(p => p.Key));
    }

    [Theory]
    [InlineData("wrong-session", "PostToolBatch", null)]
    [InlineData("native-session", "StopFailure", null)]
    [InlineData("native-session", "PostToolUse", "subagent-id")]
    public async Task Wrong_session_subagent_and_unsupported_event_cannot_claim(string session, string hookEvent, string? agent)
    {
        var mailbox = new Mailbox { Pending = true };
        await using var broker = ClaudeGoalAttentionHooks.Create(Turn(mailbox), "worker", "native-session", Launch, _ => throw new Exception("No admission expected"))!;
        var output = new StringWriter();
        await ClaudeGoalAttentionHooks.RunHelperAsync(broker.Endpoint, new StringReader(Input(hookEvent, session, agent)), output);
        Assert.Empty(output.ToString()); Assert.Empty(mailbox.States); Assert.True(mailbox.Pending); Assert.Equal(0, mailbox.Claims);
    }

    [Fact]
    public async Task Fresh_session_must_be_observed_and_changed_session_stops_attention()
    {
        var mailbox = new Mailbox { Pending = true };
        await using var broker = ClaudeGoalAttentionHooks.Create(Turn(mailbox), "worker", null, Launch, _ => Task.CompletedTask)!;
        await ClaudeGoalAttentionHooks.RunHelperAsync(broker.Endpoint, new StringReader(Input()), new StringWriter());
        Assert.Equal(0, mailbox.Claims);
        broker.BindSession("native-session"); broker.BindSession("different-session");
        await ClaudeGoalAttentionHooks.RunHelperAsync(broker.Endpoint, new StringReader(Input()), new StringWriter());
        Assert.Equal(0, mailbox.Claims);
    }

    [Fact]
    public async Task Context_over_claim_budget_stays_pending_without_truncation_or_delivery()
    {
        var mailbox = new Mailbox { Pending = true, Text = MessageAtHookContextBudget('界') + "界" };
        Assert.True(mailbox.Text.Length <= GoalMessaging.MaximumMessageCharacters);
        await using var broker = ClaudeGoalAttentionHooks.Create(Turn(mailbox), "worker", "native-session", Launch,
            _ => throw new Exception("No admission expected"))!;
        var output = new StringWriter();
        await ClaudeGoalAttentionHooks.RunHelperAsync(broker.Endpoint, new StringReader(Input()), output);
        Assert.Empty(output.ToString()); Assert.Empty(mailbox.States);
        Assert.Equal(0, mailbox.Claims); Assert.True(mailbox.Pending);
    }

    [Fact]
    public async Task Oversized_serialized_context_is_not_truncated_or_claimed_delivered()
    {
        var mailbox = new Mailbox { Pending = true, Text = MessageAtHookContextBudget('"') };
        await using var broker = ClaudeGoalAttentionHooks.Create(Turn(mailbox), "worker", "native-session", Launch, _ => throw new Exception("No admission expected"))!;
        var output = new StringWriter();
        await ClaudeGoalAttentionHooks.RunHelperAsync(broker.Endpoint, new StringReader(Input()), output);
        Assert.Empty(output.ToString()); Assert.Equal([GoalDeliveryState.Received], mailbox.States); Assert.True(mailbox.Pending);
    }

    [Fact]
    public async Task Lost_output_ack_is_uncertain_and_never_requeued()
    {
        var mailbox = new Mailbox { Pending = true };
        await using var broker = ClaudeGoalAttentionHooks.Create(Turn(mailbox), "worker", "native-session", Launch, _ => throw new Exception("No admission expected"))!;
        using (var client = new NamedPipeClientStream(".", broker.Endpoint, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await client.ConnectAsync(2000);
            await LocalPipeEndpoint.WriteFrameAsync(client, Encoding.UTF8.GetBytes(Input()), 128 * 1024, default);
            var response = await LocalPipeEndpoint.ReadFrameAsync(client, 128 * 1024, default);
            Assert.Contains("full new message", Encoding.UTF8.GetString(response));
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (mailbox.States.Count == 0) await Task.Delay(10, timeout.Token);
        Assert.Equal([GoalDeliveryState.Uncertain], mailbox.States); Assert.False(mailbox.Pending);
    }

    [Theory]
    [InlineData("accepted", GoalDeliveryState.Delivered)]
    [InlineData("no-start", GoalDeliveryState.Received)]
    [InlineData("init-only", GoalDeliveryState.Uncertain)]
    [InlineData("error-result", GoalDeliveryState.Uncertain)]
    [InlineData("synthetic-api-error", GoalDeliveryState.Uncertain)]
    public async Task Next_turn_fallback_requires_worker_evidence_and_persists_accepted_context(string scenario, GoalDeliveryState expected)
    {
        var mailbox = new Mailbox { Pending = true };
        var sessions = new InMemoryAgentSessionStore();
        var child = new StaticProcess(scenario == "accepted" ? """
            {"type":"system","session_id":"same-session"}
            {"type":"assistant","message":{"content":[{"type":"text","text":"Done"}]}}
            {"type":"result","subtype":"success","session_id":"same-session","result":"Done"}
            """ : scenario == "synthetic-api-error" ? """
            {"type":"system","session_id":"same-session"}
            {"type":"assistant","is_api_error_message":true,"error":"rate_limit","message":{"model":"<synthetic>","content":[{"type":"text","text":"Subscription unavailable"}]}}
            {"type":"result","is_error":true,"subtype":"success","result":"Subscription unavailable"}
            """ : scenario == "error-result" ? """{"type":"result","is_error":true,"subtype":"error","result":"Rejected"}""" : """{"type":"system","session_id":"same-session"}""");
        var runner = new ClaudeCodeNativeLaneRunner(new Conversations(), sessions, Options.Create(new CodingOptions()), NullLogger<ClaudeCodeNativeLaneRunner>.Instance)
        {
            ExecutableResolver = () => "test-owned-claude",
            ProcessStarter = _ => scenario == "no-start" ? throw new IOException("Did not start") : child,
        };
        var events = new List<TurnEvent>();
        await foreach (var item in runner.RunAsync(Turn(mailbox))) events.Add(item);
        Assert.Equal([expected], mailbox.States);
        if (expected == GoalDeliveryState.Delivered)
        {
            Assert.Contains("full new message", Encoding.UTF8.GetString(child.Input.ToArray()));
            Assert.Contains(await sessions.LoadAsync("conversation"), m => m.Parts.OfType<TextPart>().Any(p => p.Text.Contains("full new message")));
            Assert.Single(events.OfType<TurnCompleted>());
        }
        else Assert.Single(events.OfType<TurnErrored>());
    }

    private sealed class StaticProcess(string output) : IChildProcess
    {
        public MemoryStream Input { get; } = new();
        private StreamWriter? _stdin;
        public StreamWriter StandardInput => _stdin ??= new(Input, Encoding.UTF8, leaveOpen: true);
        public StreamReader StandardOutput { get; } = new(new MemoryStream(Encoding.UTF8.GetBytes(output)));
        public StreamReader StandardError { get; } = new(new MemoryStream());
        public int Id => -1; public bool HasExited => true; public int ExitCode => 0;
        public event EventHandler? Exited { add { } remove { } }
        public event Action<string?>? OutputLineReceived { add { } remove { } }
        public event Action<string?>? ErrorLineReceived { add { } remove { } }
        public void BeginOutputReadLine() { } public void BeginErrorReadLine() { } public void Kill(bool entireProcessTree = false) { }
        public bool WaitForExit(TimeSpan timeout) => true; public Task WaitForExitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Process? DiagnosticsProcess => null; public Process? AttachDiagnosticsProcess() => null;
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

    private sealed class Mailbox : IGoalMailbox
    {
        public Guid GoalId { get; } = Guid.NewGuid(); public volatile bool Pending; public int Claims;
        public string Text { get; init; } = "full new message";
        public List<GoalDeliveryState> States { get; } = [];
        public Task<GoalDelivery?> BeginDeliveryAsync(Guid goalId, string conversationId, string workerId, int maxCharacters = 16000, CancellationToken ct = default)
        {
            if (!Pending) return Task.FromResult<GoalDelivery?>(null);
            GoalInboxMessage message = new("message", goalId, 1, GoalMessageKind.Idea, Text, new("ui", "ui", "user"), DateTimeOffset.UtcNow, GoalDeliveryState.Presenting, "delivery");
            var context = GoalMessaging.FormatDelivery("delivery", [message]);
            if (context.Length > maxCharacters) return Task.FromResult<GoalDelivery?>(null);
            Pending = false; Claims++;
            return Task.FromResult<GoalDelivery?>(new("delivery", goalId, conversationId, workerId, [message], context));
        }
        public Task RecordDeliveryAsync(string id, GoalDeliveryState state, string? detail = null, CancellationToken ct = default) { States.Add(state); if (state == GoalDeliveryState.Received) Pending = true; return Task.CompletedTask; }
        public Task<GoalMailboxIdentity> GetIdentityAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<GoalContact>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GoalContact> RenameAsync(Guid id, string name, string handle, long revision, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GoalMessageThread> ReadAsync(Guid id, long afterSequence = 0, int take = 200, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GoalInboxMessage> SendAsync(Guid id, GoalMessageCommand message, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GoalReply> ReplyAsync(Guid id, string conversation, string delivery, string message, string text, GoalDeliveryState state, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
