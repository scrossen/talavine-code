using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Routing;
using Xunit;

namespace SwarmCore.Coding.Tests;

public sealed class RepeatedToolCallGuardTests
{
    private sealed class Input { public string Command { get; set; } = ""; public int? TimeoutSeconds { get; set; } }

    private static RepeatedToolCallGuard Guard(int limit = 3) => new(Options.Create(new CodingOptions { RepeatedCallLimit = limit }));

    private static List<ChatMessage> TranscriptWith(int failures, string command = "dotnet build", bool asErrors = true)
    {
        var messages = new List<ChatMessage> { ChatMessage.Text(ChatRole.User, "fix the build") };
        for (var i = 0; i < failures; i++)
        {
            var id = $"call-{i}";
            messages.Add(new ChatMessage(ChatRole.Assistant, [new ToolUsePart(id, "shell.exec", new JsonObject { ["command"] = command, ["timeoutSeconds"] = null })]));
            messages.Add(new ChatMessage(ChatRole.Tool, [new ToolResultPart(id, asErrors ? "{\"exitCode\":1,\"stdout\":\"\",\"stderr\":\"error CS0001\"}" : "{\"exitCode\":0,\"stdout\":\"ok\"}", IsError: asErrors)]));
        }
        return messages;
    }

    private static ToolInvocationRequest Request(IReadOnlyList<ChatMessage> transcript, string command = "dotnet build")
        => new("shell.exec", ToolSideEffect.Exec, new Input { Command = command }, "c", transcript);

    [Fact]
    public async Task Allows_until_the_identical_call_has_failed_limit_times()
    {
        var guard = Guard(limit: 3);
        Assert.True((await guard.InspectAsync(Request(TranscriptWith(2)))).Allowed);
        var blocked = await guard.InspectAsync(Request(TranscriptWith(3)));
        Assert.False(blocked.Allowed);
        Assert.Contains(RepeatedToolCallGuard.BlockMarker, blocked.BlockReason);
        Assert.Contains("3 times", blocked.BlockReason);
        Assert.False(blocked.RequiresApproval); // advice to the model, never a user gate
        Assert.True(blocked.IsRecoveryRedirect);
    }

    [Fact]
    public async Task Successes_do_not_count_but_changing_arguments_without_progress_does()
    {
        var guard = Guard(limit: 2);
        Assert.True((await guard.InspectAsync(Request(TranscriptWith(5, asErrors: false)))).Allowed);
        Assert.False((await guard.InspectAsync(Request(TranscriptWith(5), command: "dotnet test"))).Allowed);
        Assert.True((await guard.InspectAsync(Request([]))).Allowed);
    }

    [Fact]
    public void Counts_failures_by_normalized_input_regardless_of_property_order()
    {
        var transcript = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new ToolUsePart("a", "shell.exec", new JsonObject { ["timeoutSeconds"] = 5, ["command"] = "x" })]),
            new(ChatRole.Tool, [new ToolResultPart("a", "boom", IsError: true)]),
            new(ChatRole.Assistant, [new ToolUsePart("b", "shell.exec", new JsonObject { ["command"] = "x", ["timeoutSeconds"] = 5 })]),
            new(ChatRole.Tool, [new ToolResultPart("b", "{\"succeeded\":false}", IsError: false)]),
        };
        Assert.Equal(2, RepeatedToolCallGuard.CountIdenticalFailures(transcript, "shell.exec", "{\"command\":\"x\",\"timeoutSeconds\":5}"));
    }

    [Fact]
    public async Task Changed_arguments_do_not_evade_recovery_and_progress_or_a_new_turn_resets_it()
    {
        var transcript = TranscriptWith(3);
        for (var i = 0; i < 3; i++)
        {
            transcript[1 + i * 2] = new(ChatRole.Assistant, [new ToolUsePart($"call-{i}", "shell.exec", new JsonObject { ["command"] = $"different-{i}" })]);
            transcript[2 + i * 2] = new(ChatRole.Tool, [new ToolResultPart($"call-{i}", "ArgumentException: choose one mode", true)]);
        }
        var blocked = await Guard().InspectAsync(Request(transcript, "another-guess"));
        Assert.False(blocked.Allowed);
        Assert.Contains(RepeatedToolCallGuard.RecoveryMarker, blocked.BlockReason);
        Assert.True(RepeatedToolCallGuard.IsRecoveryBlock(blocked.BlockReason));
        Assert.True((await Guard().InspectAsync(new("fs.list_files", ToolSideEffect.ReadOnly, new { path = "." }, "c", transcript))).Allowed);
        var fresh = transcript.Append(ChatMessage.Text(ChatRole.User, "try again after repair")).ToList();
        Assert.True((await Guard().InspectAsync(Request(fresh))).Allowed);
        transcript.Add(new(ChatRole.Assistant, [new ToolUsePart("inspect", "fs.list_files", new JsonObject())]));
        transcript.Add(new(ChatRole.Tool, [new ToolResultPart("inspect", "[]", false)]));
        Assert.True((await Guard().InspectAsync(Request(transcript))).Allowed);
    }

    [Theory]
    [InlineData("{\"ExitCode\": 1, \"Stdout\": \"\"}", false)]
    [InlineData("{\"Ok\": false, \"Error\": \"bad input\"}", false)]
    [InlineData("{\"ExitCode\": 0, \"Stdout\": \"success\"}", true)]
    [InlineData("{\"Ok\": true, \"Error\": \"\"}", true)]
    public async Task Structured_results_are_parsed_instead_of_matching_json_substrings(string result, bool allowed)
    {
        var transcript = TranscriptWith(3);
        for (var i = 0; i < 3; i++) transcript[2 + i * 2] = new(ChatRole.Tool, [new ToolResultPart($"call-{i}", result, false)]);
        Assert.Equal(allowed, (await Guard().InspectAsync(Request(transcript))).Allowed);
    }

    [Fact]
    public async Task A_successful_edit_allows_the_same_build_command_to_be_retried()
    {
        var transcript = TranscriptWith(3);
        transcript.Add(new(ChatRole.Assistant, [new ToolUsePart("edit", "code.edit_file", new JsonObject())]));
        transcript.Add(new(ChatRole.Tool, [new ToolResultPart("edit", "{\"Succeeded\":true}", false)]));
        Assert.True((await Guard().InspectAsync(Request(transcript))).Allowed);
    }
}
