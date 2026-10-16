using System.Text.Json.Nodes;
using System.Threading.Channels;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Native;
using Xunit;

namespace SwarmCore.Coding.Tests;

public sealed class NativeToolReceiptSideEffectTests
{
    [Theory]
    [InlineData("Edit", ToolSideEffect.WritesLocal)]
    [InlineData("Write", ToolSideEffect.WritesLocal)]
    [InlineData("MultiEdit", ToolSideEffect.WritesLocal)]
    [InlineData("NotebookEdit", ToolSideEffect.WritesLocal)]
    [InlineData("Read", ToolSideEffect.ReadOnly)]
    [InlineData("Glob", ToolSideEffect.ReadOnly)]
    [InlineData("Grep", ToolSideEffect.ReadOnly)]
    [InlineData("Bash", ToolSideEffect.Exec)]
    [InlineData("PowerShell", ToolSideEffect.Exec)]
    [InlineData("mcp__swarmcode__notebook_edit", ToolSideEffect.WritesLocal)]
    [InlineData("mcp__swarmcode__notebook_read", ToolSideEffect.ReadOnly)]
    [InlineData("mcp__swarmcode__shell_background_start", ToolSideEffect.Exec)]
    [InlineData("mcp__swarmcode__git_push", ToolSideEffect.ExternalSend)]
    public void Claude_parser_reports_effect_on_start_and_completion(string name, ToolSideEffect expected)
    {
        var events = Channel.CreateUnbounded<TurnEvent>();
        List<ChatMessage> transcript = [];
        Dictionary<string, (string Name, DateTime Started)> names = [];
        var assistant = new JsonObject { ["message"] = new JsonObject { ["content"] = new JsonArray(
            new JsonObject { ["type"] = "tool_use", ["id"] = "call", ["name"] = name, ["input"] = new JsonObject() }) } };
        ClaudeCodeNativeLaneRunner.HandleAssistant(assistant, "t", events.Writer, transcript, names);
        var result = new JsonObject { ["message"] = new JsonObject { ["content"] = new JsonArray(
            new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = "call", ["content"] = "done" }) } };
        ClaudeCodeNativeLaneRunner.HandleToolResults(result, "t", events.Writer, transcript, names);
        Assert.True(events.Reader.TryRead(out var started));
        Assert.Equal(expected, Assert.IsType<ToolCallStarted>(started).SideEffect);
        Assert.True(events.Reader.TryRead(out var completed));
        Assert.Equal(expected, Assert.IsType<ToolCallCompleted>(completed).SideEffect);
    }

    [Theory]
    [InlineData("file_change", ToolSideEffect.WritesLocal)]
    [InlineData("command_execution", ToolSideEffect.Exec)]
    [InlineData("web_search", ToolSideEffect.ReadOnly)]
    public void Codex_completion_only_items_synthesize_classified_start(string type, ToolSideEffect expected)
    {
        var events = Channel.CreateUnbounded<TurnEvent>();
        CodexNativeLaneRunner.HandleEvent(new JsonObject { ["type"] = "item.completed", ["item"] = new JsonObject {
            ["id"] = "call", ["type"] = type, ["status"] = "completed", ["exit_code"] = 0 } },
            "t", events.Writer, [], new CodexNativeLaneRunner.ParseState());
        Assert.True(events.Reader.TryRead(out var started));
        Assert.Equal(expected, Assert.IsType<ToolCallStarted>(started).SideEffect);
        Assert.True(events.Reader.TryRead(out var completed));
        Assert.Equal(expected, Assert.IsType<ToolCallCompleted>(completed).SideEffect);
    }

    [Theory]
    [InlineData("write_file", ToolSideEffect.WritesLocal)]
    [InlineData("replace", ToolSideEffect.WritesLocal)]
    [InlineData("read_file", ToolSideEffect.ReadOnly)]
    [InlineData("read_many_files", ToolSideEffect.ReadOnly)]
    [InlineData("list_directory", ToolSideEffect.ReadOnly)]
    [InlineData("glob", ToolSideEffect.ReadOnly)]
    [InlineData("search_file_content", ToolSideEffect.ReadOnly)]
    [InlineData("run_shell_command", ToolSideEffect.Exec)]
    public void Gemini_parser_preserves_classification_and_failure(string name, ToolSideEffect expected)
    {
        var events = Channel.CreateUnbounded<TurnEvent>();
        var state = new GeminiCliNativeLaneRunner.ParseState();
        List<ChatMessage> transcript = [];
        GeminiCliNativeLaneRunner.HandleEvent(new JsonObject { ["type"] = "tool_use", ["tool_id"] = "call", ["tool_name"] = name },
            "t", events.Writer, transcript, state);
        GeminiCliNativeLaneRunner.HandleEvent(new JsonObject { ["type"] = "tool_result", ["tool_id"] = "call", ["status"] = "error" },
            "t", events.Writer, transcript, state);
        Assert.True(events.Reader.TryRead(out var started));
        Assert.Equal(expected, Assert.IsType<ToolCallStarted>(started).SideEffect);
        Assert.True(events.Reader.TryRead(out var completed));
        var receipt = Assert.IsType<ToolCallCompleted>(completed);
        Assert.Equal(expected, receipt.SideEffect);
        Assert.True(receipt.IsError);
    }

    [Theory]
    [InlineData("file_change", "failed", 0, ToolSideEffect.WritesLocal)]
    [InlineData("command_execution", "completed", 1, ToolSideEffect.Exec)]
    [InlineData("mcp_tool_call", "completed", 0, null)]
    public void Codex_started_items_keep_failure_and_unknown_effect(string type, string status, int exit, ToolSideEffect? expected)
    {
        var events = Channel.CreateUnbounded<TurnEvent>();
        var state = new CodexNativeLaneRunner.ParseState();
        List<ChatMessage> transcript = [];
        var item = new JsonObject { ["id"] = "call", ["type"] = type, ["status"] = status, ["exit_code"] = exit };
        CodexNativeLaneRunner.HandleEvent(new JsonObject { ["type"] = "item.started", ["item"] = item.DeepClone() }, "t", events.Writer, transcript, state);
        CodexNativeLaneRunner.HandleEvent(new JsonObject { ["type"] = "item.completed", ["item"] = item }, "t", events.Writer, transcript, state);
        Assert.True(events.Reader.TryRead(out var started));
        Assert.Equal(expected, Assert.IsType<ToolCallStarted>(started).SideEffect);
        Assert.True(events.Reader.TryRead(out var completed));
        var receipt = Assert.IsType<ToolCallCompleted>(completed);
        Assert.Equal(expected, receipt.SideEffect);
        Assert.Equal(type != "mcp_tool_call", receipt.IsError);
        Assert.False(events.Reader.TryRead(out _));
    }

    [Fact]
    public void Unknown_native_and_Mcp_tools_stay_unclassified()
    {
        Assert.Null(NativeToolSideEffects.Claude("mcp__server__tool"));
        Assert.Null(NativeToolSideEffects.Codex(CodexNativeLaneRunner.McpToolName));
        Assert.Null(NativeToolSideEffects.Gemini("mcp_tool"));
        Assert.Null(NativeToolSideEffects.Claude("FutureTool"));
        Assert.Null(NativeToolSideEffects.Gemini("FutureTool"));
    }

    [Fact]
    public void Only_the_host_bridge_server_is_classified_and_its_notebook_edit_counts_as_a_workspace_change()
    {
        Assert.Null(NativeToolSideEffects.Claude("mcp__other__notebook_edit"));
        Assert.Null(NativeToolSideEffects.Claude("mcp__swarmcode__future_tool"));
        var bridge = new JsonObject { ["server"] = "swarmcode", ["tool"] = "notebook_edit" };
        var other = new JsonObject { ["server"] = "other", ["tool"] = "notebook_edit" };
        Assert.Equal(ToolSideEffect.WritesLocal, NativeToolSideEffects.Codex(CodexNativeLaneRunner.McpToolName, bridge));
        Assert.Null(NativeToolSideEffects.Codex(CodexNativeLaneRunner.McpToolName, other));
        Assert.True(NativeToolSideEffects.IsCodexBridgeFileChange(CodexNativeLaneRunner.McpToolName, bridge));
        Assert.False(NativeToolSideEffects.IsCodexBridgeFileChange(CodexNativeLaneRunner.McpToolName, other));
        Assert.Contains("mcp__swarmcode__notebook_edit", Routing.CodingTurnOrchestrator.MutatingToolIds);
        Assert.Contains(Tools.NotebookEditTool.ToolId, Routing.CodingTurnOrchestrator.MutatingToolIds);
        Assert.DoesNotContain("mcp__swarmcode__notebook_read", Routing.CodingTurnOrchestrator.MutatingToolIds);
    }
}
