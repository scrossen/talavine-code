using System.Text.Json.Nodes;
using System.Threading.Channels;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Native;
using Xunit;

namespace SwarmCore.Coding.Tests;

public sealed class NativeLaneRepairTests
{
    [Fact]
    public void Dangling_tool_use_gets_an_error_result_so_other_lanes_can_continue()
    {
        var transcript = new List<ChatMessage>
        {
            ChatMessage.Text(ChatRole.User, "do X"),
            new(ChatRole.Assistant, [new ToolUsePart("a", "Bash", new JsonObject { ["command"] = "dotnet build" })]),
            new(ChatRole.Tool, [new ToolResultPart("a", "ok")]),
            new(ChatRole.Assistant, [new ToolUsePart("b", "Edit", new JsonObject { ["file_path"] = "x.cs" })]), // never answered
        };
        var appended = NativeLaneSupport.RepairDanglingToolCalls(transcript);
        var repair = Assert.Single(appended);
        var result = Assert.IsType<ToolResultPart>(Assert.Single(repair.Parts));
        Assert.Equal("b", result.ToolUseId);
        Assert.True(result.IsError);
        Assert.Equal(5, transcript.Count);
        Assert.Empty(NativeLaneSupport.RepairDanglingToolCalls(transcript)); // idempotent
    }

    [Fact]
    public void Claude_stream_json_result_error_leaves_a_repairable_transcript()
    {
        var channel = Channel.CreateUnbounded<TurnEvent>();
        var transcript = new List<ChatMessage>();
        var names = new Dictionary<string, (string Name, DateTime Started)>(StringComparer.Ordinal);
        var assistant = (JsonObject)JsonNode.Parse("""{"type":"assistant","message":{"content":[{"type":"text","text":"Editing."},{"type":"tool_use","id":"toolu_1","name":"Edit","input":{"file_path":"a.cs"}}]}}""")!;
        ClaudeCodeNativeLaneRunner.HandleAssistant(assistant, "t", channel.Writer, transcript, names);
        Assert.Contains(transcript, m => m.Parts.OfType<ToolUsePart>().Any(p => p.Id == "toolu_1"));
        channel.Writer.Complete();
        var events = new List<TurnEvent>();
        while (channel.Reader.TryRead(out var e)) events.Add(e);
        Assert.Single(events.OfType<TextChunk>());
        Assert.Single(events.OfType<ToolCallStarted>());

        NativeLaneSupport.RepairDanglingToolCalls(transcript);
        Assert.Contains(transcript, m => m.Role == ChatRole.Tool && m.Parts.OfType<ToolResultPart>().Any(p => p.ToolUseId == "toolu_1" && p.IsError));
    }

    [Fact]
    public void Claude_tool_results_pair_with_their_calls()
    {
        var channel = Channel.CreateUnbounded<TurnEvent>();
        var transcript = new List<ChatMessage>();
        var names = new Dictionary<string, (string Name, DateTime Started)>(StringComparer.Ordinal) { ["toolu_1"] = ("Read", DateTime.UtcNow) };
        var user = (JsonObject)JsonNode.Parse("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_1","content":[{"type":"text","text":"line 1"}],"is_error":false}]}}""")!;
        ClaudeCodeNativeLaneRunner.HandleToolResults(user, "t", channel.Writer, transcript, names);
        var part = Assert.IsType<ToolResultPart>(Assert.Single(Assert.Single(transcript).Parts));
        Assert.Equal("toolu_1", part.ToolUseId);
        Assert.Equal("line 1", part.Content);
        channel.Writer.Complete();
        Assert.True(channel.Reader.TryRead(out var evt));
        var completed = Assert.IsType<ToolCallCompleted>(evt);
        Assert.Equal("Read", completed.ToolName);
        Assert.False(completed.IsError);
    }

    [Theory]
    [InlineData("You are not logged in. Please run /login", LlmFailureCategory.AuthInvalid)]
    [InlineData("Error: 401 unauthorized", LlmFailureCategory.AuthInvalid)]
    [InlineData("usage limit reached, resets at 3pm", LlmFailureCategory.RateLimit)]
    [InlineData("HTTP 429 rate_limit_error", LlmFailureCategory.RateLimit)]
    [InlineData("prompt is too long for the context window", LlmFailureCategory.ContextOverflow)]
    [InlineData("API Error: 401 {\"type\":\"error\"}", LlmFailureCategory.AuthInvalid)]
    [InlineData("HTTP/1.1 401 Unauthorized", LlmFailureCategory.AuthInvalid)]
    [InlineData("{\"status\":429,\"message\":\"slow down\"}", LlmFailureCategory.RateLimit)]
    [InlineData("429 Too Many Requests", LlmFailureCategory.RateLimit)]
    [InlineData("Build failed: Login.razor(12,3): error CS0103", null)]
    [InlineData("edited src/pages/login-form.ts", null)]
    [InlineData("System.UnauthorizedAccessException: Access to the path 'C:\\repo\\bin' is denied.", null)]
    [InlineData("wrote 4012 bytes to out.bin", null)]
    [InlineData("Parser.cs line 429: unexpected token", null)]
    [InlineData("tests failed at line 401 of Login.spec.ts", null)]
    [InlineData("", null)]
    public void Cli_failure_classification_is_anchored(string text, LlmFailureCategory? expected)
        => Assert.Equal(expected, NativeLaneSupport.ClassifyCliFailure(text));
}
