using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.Processes;
using SwarmCore.Agent.Communication;
using SwarmCore.Agent.Runtime;
using SwarmCore.Agent.ToolUseEngine;
using SwarmCore.Coding.Native;
using SwarmCore.Coding.Routing;

namespace SwarmCore.Coding.Tests;
public sealed class CodexReadOnlyInspectionTests
{
    private static NativeLaneTurn Turn() => new("held-chat", "FULL_HELD_BODY: inspect and acknowledge without replay.",
        new("native", "CodexNative", "codex-cli:gpt-5.6-terra@high"), Path.GetTempPath(), "user", true)
        { ReadOnlyInspection = true, PlanMode = true, HostToolBridgeCapability = "opaque-working-attempt", GoalMessageLeaseToken = "private-working-lease" };
    [Fact]
    public void Exact_model_and_effort_use_reviewed_pack_with_no_workspace_bridge_or_working_thread()
    {
        var turn = Turn();
        using var workspace = new CodexNativeLaneRunner.InspectionWorkspace();
        var psi = CodexNativeLaneRunner.BuildReadOnlyInspectionStartInfo("selected-codex", turn, workspace);
        var args = psi.ArgumentList.ToList();
        Assert.Equal("selected-codex", psi.FileName);
        Assert.Equal(workspace.Root, psi.WorkingDirectory);
        Assert.NotEqual(turn.RepositoryRoot, psi.WorkingDirectory);
        Assert.Contains("--ignore-user-config", args); Assert.Contains("--ignore-rules", args);
        Assert.Contains("--ephemeral", args); Assert.Contains("approval_policy='never'", args);
        Assert.Equal("read-only", args[args.IndexOf("--sandbox") + 1]);
        Assert.Equal("gpt-5.6-terra", args[args.IndexOf("--model") + 1]);
        Assert.Contains("model_reasoning_effort='high'", args);
        Assert.Contains("mcp_servers={}", args); Assert.Contains("web_search='disabled'", args);
        Assert.DoesNotContain("resume", args); Assert.DoesNotContain("--dangerously-bypass-approvals-and-sandbox", args);
        Assert.DoesNotContain(HostToolBridgeBroker.PipeEnvironmentVariable, psi.Environment.Keys);
        Assert.DoesNotContain(HostToolBridgeBroker.AttemptEnvironmentVariable, psi.Environment.Keys);
        Assert.DoesNotContain("private-working-lease", string.Join(" ", args));
        Assert.Contains("FULL_HELD_BODY", CodexNativeLaneRunner.ReadOnlyInspectionPrompt(turn));
        Assert.Contains("Do not invoke any tools, including planning or progress tools.", CodexNativeLaneRunner.ReadOnlyInspectionPrompt(turn));
        Assert.Contains("final text only", CodexNativeLaneRunner.ReadOnlyInspectionPrompt(turn));
        Assert.DoesNotContain("Approve", CodexNativeLaneRunner.ReadOnlyInspectionPrompt(turn));
        Assert.DoesNotContain("edit files and run commands", CodexNativeLaneRunner.ReadOnlyInspectionPrompt(turn));
    }
    [Theory]
    [InlineData(null)]
    [InlineData("{\"type\":\"item.started\",\"item\":{\"id\":\"operation\",\"type\":\"command_execution\",\"command\":\"read only\"}}")]
    [InlineData("{\"type\":\"item.started\",\"item\":{\"id\":\"operation\",\"type\":\"mcp_tool_call\"}}")]
    [InlineData("{\"type\":\"item.completed\",\"item\":{\"id\":\"operation\",\"type\":\"file_change\",\"changes\":[]}}")]
    [InlineData("{\"type\":\"item.started\",\"item\":{\"id\":\"operation\",\"type\":\"web_search\",\"query\":\"a query\"}}")]
    [InlineData("{\"type\":\"item.started\",\"item\":{\"id\":\"operation\",\"type\":\"collab_tool_call\",\"tool\":\"spawn_agent\"}}")]
    [InlineData("{\"type\":\"item.updated\",\"item\":{\"id\":\"operation\",\"type\":\"todo_list\",\"items\":[]}}")]
    [InlineData("{\"type\":\"tool.started\",\"command\":\"unknown operation\"}")]
    [InlineData("{\"type\":\"item.started\"}")]
    public async Task Inspection_preserves_working_thread_and_never_accepts_ack_after_any_tool_or_unknown_protocol(string? forbidden)
    {
        NativeCliCapabilities.ProbeOverride = (_, _) => "--ignore-user-config --ignore-rules read-only";
        try
        {
            var conversation = new Conversations();
            var process = new StaticProcess("{\"type\":\"thread.started\",\"thread_id\":\"ephemeral-inspection\"}\n{\"type\":\"turn.started\"}\n"
                + (forbidden is null ? "" : forbidden + "\n")
                + "{\"type\":\"item.completed\",\"item\":{\"id\":\"answer\",\"type\":\"agent_message\",\"text\":\"Explicit inspection reply.\"}}\n{\"type\":\"turn.completed\"}");
            ProcessStartInfo? launch = null;
            var runner = new CodexNativeLaneRunner(conversation, new InMemoryAgentSessionStore(), Options.Create(new CodingOptions()), NullLogger<CodexNativeLaneRunner>.Instance)
            { ExecutableResolver = () => "inspection-test-" + Guid.NewGuid().ToString("N"), ProcessStarter = psi => { launch = psi; return process; } };
            var events = new List<TurnEvent>();
            await foreach (var item in runner.RunAsync(Turn())) events.Add(item);
            Assert.Equal(0, conversation.StateReads); Assert.Equal(0, conversation.StateWrites);
            Assert.Equal("preserved-working-thread", conversation.State[CodexNativeLaneRunner.ThreadStateKey]);
            Assert.NotNull(launch); Assert.False(Directory.Exists(launch!.WorkingDirectory));
            Assert.Contains("FULL_HELD_BODY", Encoding.UTF8.GetString(process.Input.ToArray()));
            if (forbidden is null) Assert.Equal("Explicit inspection reply.", Assert.Single(events.OfType<TurnCompleted>()).FinalText);
            else { Assert.Empty(events.OfType<TurnCompleted>()); Assert.Single(events.OfType<TurnErrored>()); }
        }
        finally { NativeCliCapabilities.ProbeOverride = null; }
    }

    [Theory]
    [InlineData(null, null, true, "Native inspection diagnostic: An optional capability is unavailable.")]
    [InlineData(null, "{\"type\":\"turn.failed\",\"error\":{\"message\":\"Actual turn failure.\"}}", false, "Actual turn failure.")]
    [InlineData(null, "{\"type\":\"error\",\"message\":\"Actual stream failure.\"}", false, "Actual stream failure.")]
    [InlineData(null, "{\"type\":\"item.started\",\"item\":{\"id\":\"operation\",\"type\":\"command_execution\",\"command\":\"read only\"}}", false, "unsupported operation")]
    [InlineData(null, "{\"type\":\"item.started\",\"item\":{\"id\":\"operation\",\"type\":\"unknown_operation\"}}", false, "unknown_operation")]
    [InlineData("{\"type\":\"item.completed\",\"item\":{\"id\":\"diagnostic\",\"type\":\"error\"}}", null, false, "incomplete error item")]
    [InlineData("{\"type\":\"item.started\",\"item\":{\"id\":\"diagnostic\",\"type\":\"error\",\"message\":\"An unexpected error-start frame.\"}}", null, false, "incomplete error item")]
    [InlineData("{\"type\":\"item.updated\",\"item\":{\"id\":\"diagnostic\",\"type\":\"error\",\"message\":\"An unexpected error-update frame.\"}}", null, false, "incomplete error item")]
    [InlineData("{\"type\":\"item.completed\",\"item\":{\"id\":12,\"type\":\"error\",\"message\":\"Not a valid warning.\"}}", null, false, "incomplete error item")]
    [InlineData("{\"type\":\"item.completed\",\"item\":{\"id\":\"diagnostic\",\"type\":\"error\",\"message\":\"Not a valid warning.\",\"command\":\"hidden operation\"}}", null, false, "incomplete error item")]
    [InlineData("{\"type\":\"item.completed\",\"item\":{\"id\":\"diagnostic\",\"type\":\"error\",\"message\":\" model rerouted: selected -> different (reason)\"}}", null, false, "selected inspection model changed")]
    public async Task Nonfatal_native_diagnostics_preserve_text_only_completion_without_relaxing_fatal_errors_operations_or_exact_model(
        string? diagnostic, string? following, bool succeeds, string expected)
    {
        NativeCliCapabilities.ProbeOverride = (_, _) => "--ignore-user-config --ignore-rules read-only";
        try
        {
            var process = new StaticProcess("{\"type\":\"thread.started\",\"thread_id\":\"ephemeral\"}\n{\"type\":\"turn.started\"}\n"
                + (diagnostic ?? "{\"type\":\"item.completed\",\"item\":{\"id\":\"diagnostic\",\"type\":\"error\",\"message\":\"An optional capability is unavailable. token=private-value\"}}")
                + "\n" + following + "\n{\"type\":\"item.completed\",\"item\":{\"id\":\"answer\",\"type\":\"agent_message\",\"text\":\"Explicit inspection reply.\"}}\n{\"type\":\"turn.completed\"}");
            var conversation = new Conversations();
            var runner = new CodexNativeLaneRunner(conversation, new InMemoryAgentSessionStore(), Options.Create(new CodingOptions()), NullLogger<CodexNativeLaneRunner>.Instance)
            { ExecutableResolver = () => "inspection-test-" + Guid.NewGuid().ToString("N"), ProcessStarter = _ => process };
            var events = new List<TurnEvent>();
            await foreach (var item in runner.RunAsync(Turn())) events.Add(item);
            if (succeeds)
            {
                Assert.Equal("Explicit inspection reply.", Assert.Single(events.OfType<TurnCompleted>()).FinalText);
                Assert.Contains(events.OfType<CodingLaneNotice>(), notice => notice.Message.Contains(expected, StringComparison.Ordinal));
                Assert.Empty(events.OfType<TurnErrored>());
            }
            else
            {
                Assert.Contains(expected, Assert.Single(events.OfType<TurnErrored>()).Message);
                Assert.Empty(events.OfType<TurnCompleted>());
            }
            Assert.DoesNotContain(events.OfType<CodingLaneNotice>(), notice => notice.Message.Contains("private-value", StringComparison.Ordinal));
            if (following is null || !following.Contains("item.started", StringComparison.Ordinal))
            {
                Assert.Empty(events.OfType<ToolCallStarted>()); Assert.Empty(events.OfType<ToolCallCompleted>());
            }
            Assert.Equal(0, conversation.StateReads); Assert.Equal(0, conversation.StateWrites);
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
        public readonly Dictionary<string, object> State = new() { [CodexNativeLaneRunner.ThreadStateKey] = "preserved-working-thread" };
        public int StateReads, StateWrites;
        public Task<IReadOnlyDictionary<string, object>> GetStateAsync(string id, CancellationToken ct = default) { StateReads++; return Task.FromResult<IReadOnlyDictionary<string, object>>(State); }
        public Task UpdateStateAsync(string id, IReadOnlyDictionary<string, object> changes, CancellationToken ct = default) { StateWrites++; foreach (var item in changes) State[item.Key] = item.Value; return Task.CompletedTask; }
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

}
