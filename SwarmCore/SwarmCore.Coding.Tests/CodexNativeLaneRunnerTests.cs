using System.Text.Json.Nodes;
using System.Threading.Channels;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Native;
using SwarmCore.Coding.Routing;
using Xunit;

namespace SwarmCore.Coding.Tests;

public sealed class CodexNativeLaneRunnerTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "codex-lane-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly CodingLane Lane = new("codex", CodexNativeLaneRunner.ProviderId, "codex-cli:default");

    public CodexNativeLaneRunnerTests() => Directory.CreateDirectory(_repo);

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch { /* best effort */ }
    }

    private NativeLaneTurn Turn(CodingLane? lane = null, bool continuation = false) => new("conv", "do X", lane ?? Lane, _repo, "swarmcode-cli", continuation);

    [Fact]
    public void Start_info_runs_exec_json_in_the_repo_with_approvals_and_sandbox_bypassed_by_default()
    {
        var psi = CodexNativeLaneRunner.BuildStartInfo(Path.Combine(_repo, "codex.exe"), Turn(), threadId: null, new NativeAgentOptions());
        var args = psi.ArgumentList.ToList();
        Assert.Equal(["exec", "--json", "--skip-git-repo-check", "-C", _repo], args.Take(5));
        Assert.Contains("--dangerously-bypass-approvals-and-sandbox", args);
        Assert.DoesNotContain("-m", args);
        Assert.DoesNotContain("resume", args);
        Assert.Equal("-", args[^1]); // prompt on stdin
        Assert.Equal(_repo, psi.WorkingDirectory);
        Assert.False(psi.Environment.ContainsKey("OPENAI_API_KEY"));
    }

    [Fact]
    public void Start_info_resumes_thread_pins_model_and_effort_and_honours_workspace_write_sandbox()
    {
        var lane = Lane with { Model = "codex-cli:gpt-5.6-terra@high" };
        var options = new NativeAgentOptions { CodexSandbox = "workspace-write" };
        var psi = CodexNativeLaneRunner.BuildStartInfo(Path.Combine(_repo, "codex.exe"), Turn(lane), "thread-1", options,
            ignoreUserConfigSupported: true, ignoreRulesSupported: true);
        var args = psi.ArgumentList.ToList();
        Assert.Equal("workspace-write", args[args.IndexOf("-s") + 1]);
        Assert.Contains("approval_policy='never'", args);
        Assert.DoesNotContain("--dangerously-bypass-approvals-and-sandbox", args);
        Assert.Equal("gpt-5.6-terra", args[args.IndexOf("-m") + 1]);
        Assert.Contains("model_reasoning_effort='high'", args);
        var resume = args.IndexOf("resume");
        Assert.True(resume >= 0);
        Assert.Equal("thread-1", args[resume + 1]);
        Assert.Equal("-", args[resume + 2]);
    }

    [Fact]
    public void Start_info_registers_the_mcp_bridge_with_toml_literals()
    {
        var options = new NativeAgentOptions
        {
            McpServer = new McpServerLaunch { Command = @"C:\tools\swarmcode.exe", Arguments = ["--mcp-server"], Environment = { ["SWARMCODE_HOME"] = @"C:\data" } },
        };
        var args = CodexNativeLaneRunner.BuildStartInfo(Path.Combine(_repo, "codex.exe"), Turn(), null, options).ArgumentList.ToList();
        Assert.Contains(@"mcp_servers.swarmcode.command='C:\tools\swarmcode.exe'", args);
        Assert.Contains($"mcp_servers.swarmcode.args=['--mcp-server','--repo','{_repo}']", args);
        // The launch names the conversation it serves, so host services the bridge forwards to act for it.
        Assert.Contains(@"mcp_servers.swarmcode.env={SWARMCODE_HOME='C:\data',SWARMCODE_CONVERSATION_ID='conv'}", args);
    }

    [Theory]
    [InlineData("codex-cli:default", null, null)]
    [InlineData("codex-cli:gpt-5.6-terra", "gpt-5.6-terra", null)]
    [InlineData("codex-cli:gpt-5.6-luna@xhigh", "gpt-5.6-luna", "xhigh")]
    [InlineData("codex-cli:gpt-6-astra@xhigh", "gpt-6-astra", "xhigh")]
    [InlineData("gpt-6-astra@bogus", "gpt-6-astra@bogus", null)]
    public void Model_normalization(string model, string? expectedModel, string? expectedEffort)
        => Assert.Equal((expectedModel, expectedEffort), CodexNativeLaneRunner.NormalizeModel(model));

    [Fact]
    public void Event_stream_maps_to_turn_events_and_a_neutral_transcript()
    {
        // Captured from a real `codex exec --json` run.
        string[] lines =
        [
            """{"type":"thread.started","thread_id":"01a11d04-9c7f-7832-8bd9-79a803bb9fa2"}""",
            """{"type":"turn.started"}""",
            """{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"I’ll run `python hello.py` and return its output exactly.\n"}}""",
            """{"type":"item.started","item":{"id":"item_1","type":"command_execution","command":"pwsh -Command 'python hello.py'","aggregated_output":"","exit_code":null,"status":"in_progress"}}""",
            """{"type":"item.completed","item":{"id":"item_1","type":"command_execution","command":"pwsh -Command 'python hello.py'","aggregated_output":"5\r\n","exit_code":0,"status":"completed"}}""",
            """{"type":"item.completed","item":{"id":"item_2","type":"file_change","changes":[{"path":"hello.py","kind":"add"}],"status":"completed"}}""",
            """{"type":"item.completed","item":{"id":"item_3","type":"agent_message","text":"5"}}""",
            """{"type":"turn.completed","usage":{"input_tokens":41501,"cached_input_tokens":33920,"output_tokens":61}}""",
        ];
        var channel = Channel.CreateUnbounded<TurnEvent>();
        var transcript = new List<ChatMessage>();
        var state = new CodexNativeLaneRunner.ParseState();
        foreach (var line in lines) CodexNativeLaneRunner.HandleEvent((JsonObject)JsonNode.Parse(line)!, "t", channel.Writer, transcript, state);
        channel.Writer.Complete();
        var events = new List<TurnEvent>();
        while (channel.Reader.TryRead(out var e)) events.Add(e);

        Assert.Equal("01a11d04-9c7f-7832-8bd9-79a803bb9fa2", state.ThreadId);
        Assert.True(state.TurnCompleted);
        Assert.Null(state.Error);
        Assert.Equal("5", state.LastMessage);

        Assert.Equal(2, events.OfType<TextChunk>().Count());
        var started = events.OfType<ToolCallStarted>().ToList();
        Assert.Equal([CodexNativeLaneRunner.CommandToolName, CodexNativeLaneRunner.FileChangeToolName], started.Select(s => s.ToolName));
        Assert.Equal("pwsh -Command 'python hello.py'", (string?)started[0].Input["command"]);
        var completed = events.OfType<ToolCallCompleted>().ToList();
        Assert.Equal(2, completed.Count);
        Assert.False(completed[0].IsError);
        Assert.Contains("\"exitCode\":0", completed[0].Result);

        Assert.Contains(transcript, m => m.Role == ChatRole.Assistant && m.Parts.OfType<ToolUsePart>().Any(p => p.Id == "item_1"));
        Assert.Contains(transcript, m => m.Role == ChatRole.Tool && m.Parts.OfType<ToolResultPart>().Any(p => p.ToolUseId == "item_1"));
        Assert.Equal("5", NativeLaneSupport.LastAssistantText(transcript));
        Assert.True(CodingTurnOrchestrator.MutatingToolIds.Contains(CodexNativeLaneRunner.CommandToolName));
        Assert.True(CodingTurnOrchestrator.MutatingToolIds.Contains(CodexNativeLaneRunner.FileChangeToolName));
    }

    [Fact]
    public void Duplicate_event_properties_do_not_abort_tool_receipts_or_completion()
    {
        var channel = Channel.CreateUnbounded<TurnEvent>();
        var transcript = new List<ChatMessage>();
        var state = new CodexNativeLaneRunner.ParseState();
        string[] lines =
        [
            """{"type":"item.started","item":{"id":"old","id":"search_1","type":"web_search","query":"example"}}""",
            """{"type":"item.completed","item":{"id":"old","id":"search_1","type":"web_search","query":"example","results":[{"id":"a","id":"b"}],"status":"completed"}}""",
            """{"type":"item.completed","item":{"id":"answer","id":"answer","type":"agent_message","text":"Done"}}""",
            """{"type":"ignored","type":"turn.completed","usage":{"input_tokens":1,"input_tokens":2,"output_tokens":3}}""",
        ];
        foreach (var line in lines)
            CodexNativeLaneRunner.HandleEvent(CodexNativeLaneRunner.ParseEvent(line)!, "t", channel.Writer, transcript, state);
        var events = new List<TurnEvent>();
        while (channel.Reader.TryRead(out var evt)) events.Add(evt);
        Assert.Equal("search_1", Assert.Single(events.OfType<ToolCallStarted>()).ToolCallId);
        var completed = Assert.Single(events.OfType<ToolCallCompleted>());
        Assert.Equal("search_1", completed.ToolCallId);
        Assert.False(completed.IsError);
        Assert.Contains("\"id\":\"b\"", completed.Result);
        Assert.True(state.TurnCompleted);
        Assert.Equal("Done", state.LastMessage);
        Assert.Null(state.Error);
    }

    [Fact]
    public void Failed_turn_and_errors_are_classified()
    {
        var state = new CodexNativeLaneRunner.ParseState();
        var channel = Channel.CreateUnbounded<TurnEvent>();
        CodexNativeLaneRunner.HandleEvent((JsonObject)JsonNode.Parse("""{"type":"turn.failed","error":{"message":"You are not logged in. Run codex login."}}""")!, "t", channel.Writer, [], state);
        Assert.False(state.TurnCompleted);
        Assert.Contains("not logged in", state.Error);
        Assert.Equal(LlmFailureCategory.AuthInvalid, state.FailureCategory);

        var limited = new CodexNativeLaneRunner.ParseState();
        CodexNativeLaneRunner.HandleEvent((JsonObject)JsonNode.Parse("""{"type":"error","message":"usage limit reached, try again later"}""")!, "t", channel.Writer, [], limited);
        Assert.Equal(LlmFailureCategory.RateLimit, limited.FailureCategory);
    }

    [Fact]
    public void Supports_only_its_provider()
    {
        var runner = new CodexNativeLaneRunner(null!, null!, Microsoft.Extensions.Options.Options.Create(new CodingOptions()), Microsoft.Extensions.Logging.Abstractions.NullLogger<CodexNativeLaneRunner>.Instance);
        Assert.True(runner.Supports(Lane));
        Assert.False(runner.Supports(new CodingLane("codex-tools", "CodexCli", "codex-cli:default")));
        Assert.True(Lane.IsNative);
    }
}
