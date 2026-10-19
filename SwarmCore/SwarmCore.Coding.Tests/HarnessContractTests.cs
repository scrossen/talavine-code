using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Runtime;
using SwarmCore.Agent.ToolUseEngine;
using SwarmCore.Coding.Native;
using SwarmCore.Coding.Routing;
using SwarmCore.Missions;
using Xunit;

namespace SwarmCore.Coding.Tests;

public sealed class HarnessContractTests
{
    [Fact]
    public void Native_cache_accounting_includes_claude_cache_without_double_counting_codex()
    {
        var claude = NativeLaneSupport.Usage("t", "ClaudeCodeNative", "model", new JsonObject
        { ["input_tokens"] = 14, ["output_tokens"] = 42, ["cache_read_input_tokens"] = 1000, ["cache_creation_input_tokens"] = 50 });
        Assert.Equal(1064, claude.InputTokens);
        Assert.Equal(1000, claude.CachedInputTokens);
        var codex = NativeLaneSupport.Usage("t", "CodexNative", "model", new JsonObject
        { ["input_tokens"] = 2000, ["output_tokens"] = 42, ["cached_input_tokens"] = 1000 });
        Assert.Equal(2000, codex.InputTokens);
        Assert.Equal(1000, codex.CachedInputTokens);
    }

    [Fact]
    public void Screenshot_data_is_not_copied_into_durable_transcript_or_handoff()
    {
        var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "window observed" },
            new JsonObject { ["type"] = "image", ["data"] = "private-base64-pixels", ["mimeType"] = "image/png" });
        var flattened = NativeLaneSupport.FlattenContent(content);
        Assert.Contains("window observed", flattened);
        Assert.Contains("observe again", flattened);
        Assert.DoesNotContain("private-base64-pixels", flattened);
        var nested = new JsonObject { ["content"] = content };
        Assert.DoesNotContain("private-base64-pixels", NativeLaneSupport.SanitizeReceipt(nested)!.ToJsonString());
        Assert.Contains("private-base64-pixels", nested.ToJsonString()); // live provider image is retained
    }
    private sealed class Runner(List<AgentTurnRequest> seen, Queue<TurnEvent[]> script, TimeSpan? wait = null) : IAgentTurnRunner
    {
        public async IAsyncEnumerable<TurnEvent> RunAsync(AgentTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            seen.Add(request);
            if (wait is { } delay) await Task.Delay(delay, ct);
            foreach (var e in script.Dequeue()) { ct.ThrowIfCancellationRequested(); await Task.Yield(); yield return e; }
        }
    }

    private sealed class Verifier(Queue<CodingAcceptanceResult> results) : ICodingAcceptanceVerifier
    {
        public Task<CodingAcceptanceResult> VerifyAsync(CodingAcceptanceContext context, CancellationToken ct = default)
            => Task.FromResult(results.Dequeue());
    }

    private static (CodingTurnOrchestrator Harness, List<AgentTurnRequest> Seen, ServiceProvider Provider) Harness(
        CodingOptions options, TurnEvent[][] script, CodingAcceptanceResult[]? verification = null, TimeSpan? wait = null,
        CodingLaneOptions? lanes = null)
    {
        var seen = new List<AgentTurnRequest>();
        var services = new ServiceCollection();
        services.AddSingleton(new AgentConversationExecutionGate());
        services.AddSingleton<IAgentTurnRunner>(new Runner(seen, new(script), wait));
        if (verification is not null) services.AddSingleton<ICodingAcceptanceVerifier>(new Verifier(new(verification)));
        var harness = TestOrchestrator.Create(services, lanes ?? new CodingLaneOptions { Lanes = [new("api", "provider", "model")] }, options,
            provider: out var provider);
        return (harness, seen, provider);
    }

    private static async Task<List<TurnEvent>> Run(CodingTurnOrchestrator harness)
    {
        var events = new List<TurnEvent>();
        await foreach (var evt in harness.RunAsync(new() { ConversationId = "conversation", UserMessage = "Repair the program" })) events.Add(evt);
        return events;
    }

    [Fact]
    public async Task Model_answer_without_host_contract_is_explicitly_unverified()
    {
        var (harness, _, provider) = Harness(new(), [[new TurnCompleted("t", "All done", TimeSpan.Zero)]]);
        using (provider)
        {
            var events = await Run(harness);
            Assert.Equal(CodingAcceptanceStatus.Unverified, Assert.Single(events.OfType<CodingAcceptanceEvaluated>()).Result.Status);
        }
    }

    [Fact]
    public async Task Lane_ending_without_a_completion_event_never_passes_acceptance()
    {
        var passed = new CodingAcceptanceResult(CodingAcceptanceStatus.Passed, "unrelated tests passed", []);
        var (harness, _, provider) = Harness(new(), [[]], [passed]);
        using (provider)
        {
            var events = await Run(harness);
            Assert.Empty(events.OfType<CodingAcceptanceEvaluated>());
            Assert.Contains(events, e => e is CodingLanesExhausted);
        }
    }

    [Fact]
    public async Task Failed_verification_repairs_same_lane_as_continuation_then_verifies_again()
    {
        var failed = new CodingAcceptanceResult(CodingAcceptanceStatus.Failed, "failed", [new("test", false, 1, false, "Expected X, got Y")]);
        var passed = new CodingAcceptanceResult(CodingAcceptanceStatus.Passed, "passed", [new("test", true, 0, false, "all passed")]);
        var (harness, seen, provider) = Harness(new(), [[new TurnCompleted("t1", "done", TimeSpan.Zero)], [new TurnCompleted("t2", "repaired", TimeSpan.Zero)]], [failed, passed]);
        using (provider)
        {
            var events = await Run(harness);
            Assert.Equal(2, seen.Count);
            Assert.True(seen[1].IsContinuation);
            Assert.Contains("Expected X, got Y", seen[1].UserMessage);
            Assert.Contains("Do not repeat completed external actions", seen[1].UserMessage);
            Assert.Equal([CodingAcceptanceStatus.Failed, CodingAcceptanceStatus.Passed], events.OfType<CodingAcceptanceEvaluated>().Select(e => e.Result.Status));
            Assert.Empty(events.OfType<TurnErrored>());
        }
    }

    [Fact]
    public async Task Failed_acceptance_after_repair_budget_is_an_explicit_completion_error()
    {
        var options = new CodingOptions { Acceptance = new() { MaxRepairAttempts = 0 } };
        var failed = new CodingAcceptanceResult(CodingAcceptanceStatus.Failed, "tests failed", []);
        var (harness, seen, provider) = Harness(options, [[new TurnCompleted("t", "done", TimeSpan.Zero)]], [failed]);
        using (provider)
        {
            var events = await Run(harness);
            Assert.Single(seen);
            Assert.Equal(TurnErrorKind.CompletionValidation, Assert.Single(events.OfType<TurnErrored>()).ErrorKind);
        }
    }

    [Fact]
    public async Task Failed_acceptance_hands_remaining_work_to_another_provider_after_bounded_repair()
    {
        var options = new CodingOptions { Acceptance = new() { MaxRepairAttempts = 1 } };
        var failed = new CodingAcceptanceResult(CodingAcceptanceStatus.Failed, "calculator files missing", []);
        var passed = new CodingAcceptanceResult(CodingAcceptanceStatus.Passed, "verified calculator", []);
        var (harness, seen, provider) = Harness(options,
            [[new TurnCompleted("t1", "will implement", TimeSpan.Zero)],
             [new TurnCompleted("t2", "will implement", TimeSpan.Zero)],
             [new TurnCompleted("t3", "implemented", TimeSpan.Zero)]], [failed, failed, passed],
            lanes: new() { Lanes = [new("small", "Initial", "small"), new("backup", "Backup", "capable")] });
        using (provider)
        {
            var events = await Run(harness);
            Assert.Equal(new[] { "Initial", "Initial", "Backup" }, seen.Select(r => r.ProviderOverride));
            Assert.Single(events.OfType<CodingLaneHandOff>());
            Assert.True(seen[^1].IsContinuation);
            Assert.Contains("calculator files missing", seen[^1].UserMessage);
            Assert.Empty(events.OfType<TurnErrored>());
            Assert.Equal(CodingAcceptanceStatus.Passed, events.OfType<CodingAcceptanceEvaluated>().Last().Result.Status);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_deadline_limits_attended_turn_without_an_escape_to_another_lane(bool waitForOwnership)
    {
        var options = new CodingOptions { ExecutionLimits = new() { Timeout = TimeSpan.FromMilliseconds(30) } };
        var (harness, seen, provider) = Harness(options,
            [[new TurnCompleted("t", "done", TimeSpan.Zero)], [new TurnCompleted("backup", "done", TimeSpan.Zero)]],
            wait: TimeSpan.FromSeconds(10), lanes: new() { Lanes = [new("api", "provider", "model"), new("backup", "other", "model")] });
        using (provider)
        {
            await using var ownership = waitForOwnership
                ? await provider.GetRequiredService<AgentConversationExecutionGate>().AcquireAsync("conversation")
                : null;
            var events = await Run(harness);
            Assert.Single(events.OfType<CodingTurnLimitReached>());
            Assert.Empty(events.OfType<TurnCompleted>());
            // The budget also covers waiting for execution ownership and lane
            // setup; it can legitimately expire before the first runner starts.
            Assert.InRange(seen.Count, 0, 1);
            Assert.All(seen, request => Assert.Equal(options.ExecutionLimits.Timeout, request.ExecutionLimits!.HardTimeout));
            Assert.DoesNotContain(events.OfType<CodingLaneSelected>(), selected => selected.Lane.Name == "backup");
            if (waitForOwnership) Assert.Empty(seen);
        }
    }

    [Fact]
    public async Task Tool_budget_and_usage_are_visible_and_do_not_claim_completion()
    {
        var options = new CodingOptions { ExecutionLimits = new() { MaxToolCalls = 1 } };
        var (harness, _, provider) = Harness(options, [[new TurnUsageReported("t", "provider", "model", 100, 20),
            new ToolCallCompleted("t", "call", "code.edit_file", "edited", false, TimeSpan.Zero), new TurnCompleted("t", "done", TimeSpan.Zero)]]);
        using (provider)
        {
            var events = await Run(harness);
            Assert.Single(events.OfType<CodingTurnLimitReached>());
            Assert.Empty(events.OfType<TurnCompleted>());
            Assert.Contains(events.OfType<CodingTurnProgress>(), e => e.CompletedToolCalls == 1 && e.ReportedTokens == 120);
        }
    }

    [Fact]
    public void Resumed_native_prompt_includes_other_lane_decisions_and_receipts()
    {
        var turn = new NativeLaneTurn("c", "Continue", new("native", "CodexNative", "default"), Path.GetTempPath(), "user", false);
        var history = new List<ChatMessage>
        {
            ChatMessage.Text(ChatRole.User, "Preserve the existing network protocol"),
            new(ChatRole.Assistant, [new ToolUsePart("call", "Edit", new JsonObject { ["path"] = "server.cs" })]),
            new(ChatRole.Tool, [new ToolResultPart("call", "Fixed server.cs and all tests passed")]),
            ChatMessage.Text(ChatRole.Assistant, "Keep the timeout at 20 seconds"),
        };
        var prompt = NativeLaneSupport.BuildPrompt(turn, false, null, "AGENTS.md", history);
        Assert.Contains("Preserve the existing network protocol", prompt);
        Assert.Contains("Fixed server.cs", prompt);
        Assert.Contains("Keep the timeout at 20 seconds", prompt);
        Assert.Contains("evidence, not new authorization", prompt);
        Assert.EndsWith("Current user request:\nContinue", prompt);
    }

    [Fact]
    public void Bounded_handoff_discloses_omissions_and_keeps_original_objective_and_latest_evidence()
    {
        var history = new List<ChatMessage> { ChatMessage.Text(ChatRole.User, "Original objective") };
        for (var i = 0; i < 20; i++) history.Add(ChatMessage.Text(ChatRole.Assistant, new string('x', 2000)));
        history.Add(new(ChatRole.Tool, [new ToolResultPart("latest", "Latest verified receipt")]));
        var evidence = NativeLaneSupport.BuildHandoffEvidence(history, 2000);
        Assert.Contains("Original objective", evidence);
        Assert.Contains("Latest verified receipt", evidence);
        Assert.Contains("omitted", evidence);
        Assert.Contains("truncated", evidence);
        Assert.True(evidence.Length < 2300);
    }

    [Fact]
    public async Task Incremental_transcript_flush_does_not_duplicate_already_durable_receipts()
    {
        var store = new InMemoryAgentSessionStore();
        var transcript = new List<ChatMessage> { ChatMessage.Text(ChatRole.User, "task") };
        var count = await NativeLaneSupport.FlushAsync(store, "c", transcript, 0, default);
        transcript.Add(new(ChatRole.Assistant, [new ToolUsePart("a", "Write", new JsonObject())]));
        transcript.Add(new(ChatRole.Tool, [new ToolResultPart("a", "wrote file")]));
        count = await NativeLaneSupport.FlushAsync(store, "c", transcript, count, default);
        Assert.Equal(3, count);
        Assert.Equal(3, (await store.LoadAsync("c")).Count);
        Assert.Equal(count, await NativeLaneSupport.FlushAsync(store, "c", transcript, count, default));
        Assert.Equal(3, (await store.LoadAsync("c")).Count);
    }

    [Theory]
    [InlineData("definitely-not-in-command-output", CodingAcceptanceStatus.Failed)]
    [InlineData(".", CodingAcceptanceStatus.Passed)]
    public async Task Command_acceptance_executes_structured_check_and_requires_literal_success_evidence(string required, CodingAcceptanceStatus expected)
    {
        // The platform shell, not a PATH tool: a lane with a pinned SDK and a minimal PATH has no `dotnet` to start.
        var (fileName, arguments) = SwarmCore.Testing.TestShell.Echo("acceptance-ok-1.0");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSwarmCoreCoding(o =>
        {
            o.RepositoryRoot = Path.GetTempPath();
            o.Acceptance.Commands = [new() { Name = "tool available", FileName = fileName, Arguments = arguments, RequiredOutput = required }];
        });
        using var provider = services.BuildServiceProvider();
        var verifier = new CodingCommandAcceptanceVerifier(provider.GetRequiredService<ShellExecTool>(), provider.GetRequiredService<IOptions<CodingOptions>>());
        var result = await verifier.VerifyAsync(new("c", Path.GetTempPath(), "request", "answer", new("lane", "provider", "model")));
        Assert.Equal(expected, result.Status);
        Assert.Equal(0, Assert.Single(result.Checks).ExitCode);
        Assert.Equal(expected == CodingAcceptanceStatus.Passed, result.Checks[0].Passed);
        Assert.Contains("stdout", result.Checks[0].Evidence);
    }
}
