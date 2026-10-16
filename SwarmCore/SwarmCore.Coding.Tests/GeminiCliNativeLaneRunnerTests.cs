using System.Text.Json.Nodes;
using System.Threading.Channels;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Runtime;
using SwarmCore.Agent.ToolUseEngine;
using SwarmCore.Coding.Native;
using SwarmCore.Coding.Routing;
using Xunit;

namespace SwarmCore.Coding.Tests;

public sealed class GeminiCliNativeLaneRunnerTests
{
    private static readonly CodingLane Lane = new("gemini-cli", GeminiCliNativeLaneRunner.ProviderId, "gemini-cli:pro");

    private static NativeLaneTurn Turn(string model = "gemini-cli:pro")
        => new("conv", "do X", Lane with { Model = model }, Path.GetTempPath(), "swarmcode-cli", false);

    [Theory]
    [InlineData("gemini-cli:default", null)]
    [InlineData("gemini-cli:pro", "pro")]
    [InlineData("gemini-cli:flash", "flash")]
    [InlineData("gemini-cli:gemini-3-pro", "gemini-3-pro")]
    public void Model_tag_maps_to_the_cli_model_argument(string model, string? expected)
        => Assert.Equal(expected, GeminiCliNativeLaneRunner.NormalizeModel(model));

    [Fact]
    public void Start_info_runs_headless_stream_json_with_auto_approval_and_a_chosen_session()
    {
        var args = GeminiCliNativeLaneRunner.BuildStartInfo(Path.Combine(Path.GetTempPath(), "gemini.exe"), Turn(), null, "sess-1", new NativeAgentOptions()).ArgumentList.ToList();
        Assert.Contains("stream-json", args);
        Assert.Equal("yolo", args[args.IndexOf("--approval-mode") + 1]);
        Assert.Equal("pro", args[args.IndexOf("-m") + 1]);
        Assert.Equal("sess-1", args[args.IndexOf("--session-id") + 1]);
        Assert.DoesNotContain("--resume", args);

        var resumed = GeminiCliNativeLaneRunner.BuildStartInfo(Path.Combine(Path.GetTempPath(), "gemini.exe"), Turn(), "sess-1", null, new NativeAgentOptions()).ArgumentList.ToList();
        Assert.Equal("sess-1", resumed[resumed.IndexOf("--resume") + 1]);
        Assert.DoesNotContain("--session-id", resumed);
    }

    [Fact]
    public void Stream_json_events_become_turn_events_and_a_neutral_transcript()
    {
        string[] lines =
        [
            """{"type":"init","session_id":"s","model":"gemini-3-pro"}""",
            """{"type":"message","role":"user","content":"do X"}""",
            """{"type":"message","role":"assistant","content":"Creating the file.","delta":true}""",
            """{"type":"tool_use","tool_name":"write_file","tool_id":"t1","parameters":{"file_path":"a.txt","content":"hi"}}""",
            """{"type":"tool_result","tool_id":"t1","status":"success","output":"ok"}""",
            """{"type":"tool_use","tool_name":"run_shell_command","tool_id":"t2","parameters":{"command":"false"}}""",
            """{"type":"tool_result","tool_id":"t2","status":"error","error":{"type":"EXEC","message":"exit 1"}}""",
            """{"type":"message","role":"assistant","content":"Done.","delta":true}""",
            """{"type":"result","status":"success","stats":{"total_tokens":30,"input_tokens":20,"output_tokens":10,"cached":5}}""",
        ];
        var channel = Channel.CreateUnbounded<TurnEvent>();
        var transcript = new List<ChatMessage>();
        var state = new GeminiCliNativeLaneRunner.ParseState();
        foreach (var line in lines) GeminiCliNativeLaneRunner.HandleEvent((JsonObject)JsonNode.Parse(line)!, "t", channel.Writer, transcript, state);
        channel.Writer.Complete();
        var events = new List<TurnEvent>();
        while (channel.Reader.TryRead(out var e)) events.Add(e);

        Assert.True(state.TurnCompleted);
        Assert.Null(state.Error);
        Assert.Equal("gemini-3-pro", state.Model);
        Assert.Equal(["write_file", "run_shell_command"], events.OfType<ToolCallStarted>().Select(s => s.ToolName));
        var completed = events.OfType<ToolCallCompleted>().ToList();
        Assert.False(completed[0].IsError);
        Assert.True(completed[1].IsError);
        Assert.Equal("exit 1", completed[1].Result);
        var usage = Assert.Single(events.OfType<TurnUsageReported>());
        Assert.Equal(20, usage.InputTokens);
        Assert.Equal("Done.", NativeLaneSupport.LastAssistantText(transcript));
        Assert.All(GeminiCliNativeLaneRunner.MutatingToolNames, n => Assert.Contains(n, CodingTurnOrchestrator.MutatingToolIds));
    }

    [Fact]
    public void Failed_result_is_an_error_and_warnings_are_not()
    {
        var channel = Channel.CreateUnbounded<TurnEvent>();
        var transcript = new List<ChatMessage>();
        var state = new GeminiCliNativeLaneRunner.ParseState();
        GeminiCliNativeLaneRunner.HandleEvent((JsonObject)JsonNode.Parse("""{"type":"error","severity":"warning","message":"Loop detected"}""")!, "t", channel.Writer, transcript, state);
        Assert.Null(state.Error);
        GeminiCliNativeLaneRunner.HandleEvent((JsonObject)JsonNode.Parse("""{"type":"result","status":"error","error":{"type":"Auth","message":"401 Unauthorized"}}""")!, "t", channel.Writer, transcript, state);
        Assert.False(state.TurnCompleted);
        Assert.Contains("401", state.Error);
        Assert.Equal(SwarmCore.Abstractions.LlmRouting.LlmFailureCategory.AuthInvalid, state.FailureCategory);
    }
}
