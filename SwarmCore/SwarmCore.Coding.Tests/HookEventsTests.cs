using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SwarmCore.Coding.Agents;
using SwarmCore.Coding.Extensions;

namespace SwarmCore.Coding.Tests;

/// <summary>The lifecycle hook events added for Claude Code parity: SessionStart, SessionEnd, SubagentStop, PreCompact, Notification.</summary>
public sealed class HookEventsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "swarmcore-hook-events-" + Guid.NewGuid().ToString("N"));
    private string Repo => Path.Combine(_root, "repo");

    public HookEventsTests() => Directory.CreateDirectory(Repo);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static bool Win => OperatingSystem.IsWindows();
    private static string Exit2WithReason(string reason) => Win ? $"echo {reason} 1>&2 & exit /b 2" : $"echo '{reason}' 1>&2; exit 2";
    private static string EchoJson(string json) => Win ? $"echo {json}" : $"echo '{json}'";
    private static string Echo(string text) => Win ? $"echo {text}" : $"echo '{text}'";
    private string SaveStdin(string file) => Win ? $"findstr \"^\" > \"{file}\"" : $"cat > '{file}'";

    private static CodingHook Hook(CodingHookEvent evt, string command)
        => new(evt, null, command, TimeSpan.FromSeconds(30), ExtensionSource.User, "test");

    private CodingHooks Hooks(Action<CodingHooksOptions> configure)
    {
        var options = new CodingOptions();
        configure(options.Hooks);
        return new CodingHooks(new CodingHookSource(Options.Create(options), new NoCodingHookApprovals()), new CodingHookRunner());
    }

    private static JsonObject ReadPayload(string file) => JsonNode.Parse(File.ReadAllText(file).Trim())!.AsObject();

    [Fact]
    public void Options_ListTheNewEvents_AndTheRepositoryFileParsesThem()
    {
        var options = new CodingHooksOptions();
        options.SessionStart.Add(new CodingHookDefinition { Command = "a" });
        options.SessionEnd.Add(new CodingHookDefinition { Command = "b" });
        options.SubagentStop.Add(new CodingHookDefinition { Command = "c" });
        options.PreCompact.Add(new CodingHookDefinition { Command = "d" });
        options.Notification.Add(new CodingHookDefinition { Command = "e" });
        var events = options.All().Select(x => x.Event).ToList();
        Assert.Equal([CodingHookEvent.SessionStart, CodingHookEvent.SessionEnd, CodingHookEvent.SubagentStop, CodingHookEvent.PreCompact, CodingHookEvent.Notification], events);

        var json = """
            {
              "hooks": {
                "SessionStart": [ { "matcher": "startup", "command": "load-env" } ],
                "sessionEnd": [ { "command": "save" } ],
                "SubagentStop": [ { "hooks": [ { "type": "command", "command": "notify-agent" } ] } ],
                "PreCompact": [ { "command": "snapshot" } ],
                "Notification": [ { "command": "ping" } ]
              }
            }
            """;
        var hooks = CodingHookSource.ParseHooksFile(Encoding.UTF8.GetBytes(json), "hooks.json");
        Assert.Equal([CodingHookEvent.SessionStart, CodingHookEvent.SessionEnd, CodingHookEvent.SubagentStop, CodingHookEvent.PreCompact, CodingHookEvent.Notification],
            hooks.Select(h => h.Event).ToList());
        // Non-tool events apply whatever their matcher says.
        Assert.True(hooks[0].Matches(null));
        Assert.True(hooks[0].Matches("shell.exec"));
    }

    [Fact]
    public async Task SessionStart_PayloadHasSource_AndStdoutBecomesContext()
    {
        var file = Path.Combine(_root, "session-start.json");
        var hooks = Hooks(h =>
        {
            h.SessionStart.Add(new CodingHookDefinition { Command = SaveStdin(file) });
            h.SessionStart.Add(new CodingHookDefinition { Command = Echo("branch main, 3 open tickets") });
            h.SessionStart.Add(new CodingHookDefinition { Command = EchoJson("{\"additionalContext\":\"from json\"}") });
        });
        var outcome = await hooks.SessionStartAsync("c1", "resume", Repo);
        var payload = ReadPayload(file);
        Assert.Equal("SessionStart", (string?)payload["event"]);
        Assert.Equal("resume", (string?)payload["source"]);
        Assert.Equal("c1", (string?)payload["conversationId"]);
        Assert.Equal(Repo, (string?)payload["cwd"]);
        Assert.False(outcome.Blocked);
        Assert.Equal(["branch main, 3 open tickets", "from json"], outcome.Context);
        Assert.Equal("branch main, 3 open tickets\n\nfrom json", CodingHooks.SessionContext(outcome));
        Assert.StartsWith("[Context added by a SessionStart hook]\nbranch main, 3 open tickets\n\nfrom json\n\nfix it", CodingHooks.WithSessionContext("fix it", CodingHooks.SessionContext(outcome)));
        Assert.Equal("fix it", CodingHooks.WithSessionContext("fix it", null));
    }

    [Fact]
    public async Task SessionStart_CannotBlock_ABlockIsAWarning_AndLaterHooksStillRun()
    {
        var marker = Path.Combine(_root, "later.json");
        var hooks = Hooks(h =>
        {
            h.SessionStart.Add(new CodingHookDefinition { Command = Exit2WithReason("nope") });
            h.SessionStart.Add(new CodingHookDefinition { Command = EchoJson("{\"decision\":\"block\",\"reason\":\"still-nope\"}") });
            h.SessionStart.Add(new CodingHookDefinition { Command = SaveStdin(marker) });
        });
        var outcome = await hooks.SessionStartAsync("c1", "startup", Repo);
        Assert.False(outcome.Blocked);
        Assert.Equal(2, outcome.Warnings.Count);
        Assert.All(outcome.Warnings, w => Assert.Contains("cannot do", w));
        Assert.Contains(outcome.Warnings, w => w.Contains("nope"));
        Assert.True(File.Exists(marker));
        Assert.Equal(3, outcome.Executions.Count);
    }

    [Fact]
    public async Task SessionEnd_PreCompact_Notification_SubagentStop_PayloadShapes_AndInformationalSemantics()
    {
        var end = Path.Combine(_root, "end.json");
        var compact = Path.Combine(_root, "compact.json");
        var notify = Path.Combine(_root, "notify.json");
        var agent = Path.Combine(_root, "agent.json");
        var hooks = Hooks(h =>
        {
            h.SessionEnd.Add(new CodingHookDefinition { Command = SaveStdin(end) });
            h.SessionEnd.Add(new CodingHookDefinition { Command = Exit2WithReason("hold-on") });
            h.PreCompact.Add(new CodingHookDefinition { Command = SaveStdin(compact) });
            h.PreCompact.Add(new CodingHookDefinition { Command = EchoJson("{\"decision\":\"block\"}") });
            h.Notification.Add(new CodingHookDefinition { Command = SaveStdin(notify) });
            h.SubagentStop.Add(new CodingHookDefinition { Command = SaveStdin(agent) });
        });

        var ended = await hooks.SessionEndAsync("c1", "closed", Repo);
        Assert.False(ended.Blocked);
        Assert.Single(ended.Warnings);
        var endPayload = ReadPayload(end);
        Assert.Equal("SessionEnd", (string?)endPayload["event"]);
        Assert.Equal("closed", (string?)endPayload["reason"]);

        var compacted = await hooks.PreCompactAsync("c1", "PreflightSizeLimit", Repo);
        Assert.False(compacted.Blocked);
        Assert.Single(compacted.Warnings);
        var compactPayload = ReadPayload(compact);
        Assert.Equal("PreCompact", (string?)compactPayload["event"]);
        Assert.Equal("PreflightSizeLimit", (string?)compactPayload["reason"]);

        var notified = await hooks.NotificationAsync("c1", "permission", "shell / exec is waiting for your approval.", Repo);
        Assert.False(notified.Blocked);
        var notifyPayload = ReadPayload(notify);
        Assert.Equal("Notification", (string?)notifyPayload["event"]);
        Assert.Equal("permission", (string?)notifyPayload["kind"]);
        Assert.Equal("shell / exec is waiting for your approval.", (string?)notifyPayload["message"]);

        var longResult = new string('r', CodingHooks.MaxSubagentResultCharacters + 500);
        var stopped = await hooks.SubagentStopAsync("agent1", "parent", "Build A", "Idle", longResult, null, Repo);
        Assert.False(stopped.Blocked);
        var agentPayload = ReadPayload(agent);
        Assert.Equal("SubagentStop", (string?)agentPayload["event"]);
        Assert.Equal("agent1", (string?)agentPayload["agentId"]);
        Assert.Equal("parent", (string?)agentPayload["conversationId"]);
        Assert.Equal("Build A", (string?)agentPayload["title"]);
        Assert.Equal("Idle", (string?)agentPayload["state"]);
        Assert.Null(agentPayload["error"]?.GetValue<string>());
        var result = (string?)agentPayload["result"];
        Assert.NotNull(result);
        Assert.True(result.Length <= CodingHooks.MaxSubagentResultCharacters + 50, $"result was {result.Length} characters");
        Assert.StartsWith("rrrr", result);
    }

    [Fact]
    public void Runner_BlockRulesPerEvent()
    {
        Assert.True(CodingHookRunner.CanBlock(CodingHookEvent.PreToolUse));
        Assert.True(CodingHookRunner.CanBlock(CodingHookEvent.UserPromptSubmit));
        Assert.False(CodingHookRunner.CanBlock(CodingHookEvent.SessionStart));
        Assert.False(CodingHookRunner.CanBlock(CodingHookEvent.SessionEnd));
        Assert.False(CodingHookRunner.CanBlock(CodingHookEvent.SubagentStop));
        Assert.False(CodingHookRunner.CanBlock(CodingHookEvent.PreCompact));
        Assert.False(CodingHookRunner.CanBlock(CodingHookEvent.Notification));
        Assert.True(CodingHookRunner.AcceptsPlainContext(CodingHookEvent.SessionStart));
        Assert.False(CodingHookRunner.AcceptsPlainContext(CodingHookEvent.Stop));

        // Plain stdout of an informational hook is its own log, not context.
        var decision = CodingHookRunner.Interpret(new CodingHookExecution(Hook(CodingHookEvent.Notification, "x"), 0, false, "logged", "", false, null, TimeSpan.Zero));
        Assert.False(decision.Block);
        Assert.Null(decision.Context);
        Assert.Null(decision.Warning);
    }

    [Fact]
    public async Task NoHooksForAnEvent_RunsNothing()
    {
        var hooks = Hooks(h => h.Stop.Add(new CodingHookDefinition { Command = "echo stop" }));
        var outcome = await hooks.SessionStartAsync("c1", "new", Repo);
        Assert.False(outcome.Ran);
        Assert.Same(CodingHookOutcome.None, outcome);
    }

    // ---- SubagentStop source: the team raises AgentSettled once per settlement ----

    private sealed class FakeHost : IAgentTeamHost
    {
        private int _next;
        public System.Collections.Concurrent.ConcurrentDictionary<string, bool> Busy { get; } = new();
        public Task<string> CreateConversationAsync(string parentConversationId, string title, string? lane, string? model, CancellationToken ct)
            => Task.FromResult($"agent{Interlocked.Increment(ref _next)}");
        public Task<bool> StartTurnAsync(string conversationId, string message, string? lane, string? model, CancellationToken ct)
        {
            Busy[conversationId] = true;
            return Task.FromResult(true);
        }
        public void Cancel(string conversationId) { }
        public bool IsBusy(string conversationId) => Busy.GetValueOrDefault(conversationId);
        public Task<bool> DeliverAsync(string conversationId, string message, CancellationToken ct) => Task.FromResult(true);
    }

    [Fact]
    public async Task AgentTeam_RaisesAgentSettled_OncePerSettlement_WithTheTerminalRecord()
    {
        var options = new AgentTeamOptions { MaxRunning = 1 };
        var team = new AgentTeam(new InMemoryAgentTeamStore(), () => options);
        var host = new FakeHost();
        team.AttachHost(host);
        var settled = new List<AgentRecord>();
        var signal = new SemaphoreSlim(0);
        team.AgentSettled += record => { lock (settled) settled.Add(record); signal.Release(); return Task.CompletedTask; };
        team.AgentSettled += _ => throw new InvalidOperationException("a failing listener never stops the others");

        var a = await team.SpawnAsync("root", new AgentSpawnRequest("a", "Alpha"));
        var b = await team.SpawnAsync("root", new AgentSpawnRequest("b", "Beta")); // queued behind a (one slot)
        Assert.Empty(settled);

        host.Busy[a.Id] = false;
        await team.ReportTurnFinishedAsync(a.Id, new AgentTurnOutcome(true, false, null, "alpha done", 2));
        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(10)));
        lock (settled)
        {
            var only = Assert.Single(settled);
            Assert.Equal(a.Id, only.Id);
            Assert.Equal(AgentState.Idle, only.State);
            Assert.Equal("alpha done", only.Result);
        }

        // A repeated report for an already settled agent does not settle it again; the queued agent's start does not either.
        await team.ReportTurnFinishedAsync(a.Id, new AgentTurnOutcome(true, false, null, "alpha again", 2));
        await team.ReportTurnFinishedAsync("root", new AgentTurnOutcome(true, false, null, "root idle", 0));
        await Task.Delay(200);
        lock (settled) Assert.Single(settled);

        // Cancelling the running agent (b is now running) settles it once as Cancelled; a cancelled queued agent settles too.
        var c = await team.SpawnAsync("root", new AgentSpawnRequest("c", "Gamma")); // queued behind b
        await team.CancelAsync("root", c.Id);
        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(10)));
        lock (settled) Assert.Contains(settled, r => r.Id == c.Id && r.State == AgentState.Cancelled);

        host.Busy[b.Id] = false;
        await team.ReportTurnFinishedAsync(b.Id, new AgentTurnOutcome(false, true, null, null, 0));
        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(10)));
        lock (settled)
        {
            Assert.Equal(3, settled.Count);
            Assert.Contains(settled, r => r.Id == b.Id && r.State == AgentState.Cancelled);
        }

        // Detaching with interruption settles whatever is still working, once.
        var d = await team.SpawnAsync("root", new AgentSpawnRequest("d", "Delta"));
        Assert.True(team.IsAttached(host));
        team.DetachHost(host, interruptRunning: true);
        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(10)));
        lock (settled)
        {
            Assert.Equal(4, settled.Count);
            Assert.Contains(settled, r => r.Id == d.Id && r.State == AgentState.Interrupted);
        }
        Assert.False(team.IsAttached(host));
    }
}
