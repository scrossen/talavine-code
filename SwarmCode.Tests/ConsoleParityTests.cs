using System.Runtime.CompilerServices;
using SwarmCode.Ui;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Permissions;
using SwarmCore.Coding.Routing;

namespace SwarmCode.Tests;

/// <summary>Console flags and commands added for Claude Code parity: resume, plan, model, permission mode, compact, mentions, usage.</summary>
public sealed class ConsoleParityTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "swarmcode-parity-" + Guid.NewGuid().ToString("N"));

    public ConsoleParityTests()
    {
        Directory.CreateDirectory(_repo);
        File.WriteAllText(Path.Combine(_repo, "README.md"), "# demo\n");
    }

    public void Dispose() { try { Directory.Delete(_repo, true); } catch { } }

    private sealed class RecordingOrchestrator(params TurnEvent[] events) : ICodingTurnOrchestrator
    {
        public List<CodingTurnRequest> Requests { get; } = [];
        public IReadOnlyList<CodingLane> Lanes => [];
        public CodingLane? ResolveLane(string? name) => null;
        public async IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            foreach (var evt in events) { await Task.Yield(); yield return evt; }
            yield return new TurnCompleted("job", "done", TimeSpan.Zero);
        }
    }

    [Fact]
    public void Cli_parses_the_session_and_mode_flags()
    {
        var o = CliOptions.Parse(["-c", "--permission-mode", "ask-risky", "--model", "gpt-5", "--plan", "--max-turns", "7", "--append-system-prompt", "Be terse", "-p", "hi"]);
        Assert.Null(o.Error);
        Assert.True(o.Continue);
        Assert.Equal("ask-risky", o.PermissionMode);
        Assert.Equal("gpt-5", o.Model);
        Assert.True(o.Plan);
        Assert.Equal(7, o.MaxTurns);
        Assert.Equal("Be terse", o.AppendSystemPrompt);
        Assert.Equal("hi", o.Prompt);

        Assert.Equal("abc123", CliOptions.Parse(["-r", "abc123"]).Resume);
        Assert.Contains("not both", CliOptions.Parse(["--continue", "--resume", "x"]).Error);
        Assert.Contains("Unknown permission mode", CliOptions.Parse(["--permission-mode", "yolo"]).Error);
        Assert.Contains("positive number", CliOptions.Parse(["--max-turns", "0"]).Error);
        // -p with no text followed by a known flag still reads stdin.
        Assert.Null(CliOptions.Parse(["-p", "--plan"]).Prompt);
    }

    [Fact]
    public async Task Plan_model_mode_and_compaction_reach_the_request_and_compaction_is_one_shot()
    {
        var renderer = new ConsoleRenderer(new() { Quiet = true }, new StringWriter(), new StringWriter());
        var orchestrator = new RecordingOrchestrator();
        var runner = new TurnRunner(orchestrator, renderer, false)
        {
            PlanMode = true,
            ModelOverride = "deepseek-reasoner",
            PermissionMode = CodingPermissionMode.AskRisky,
            CompactNextTurn = true,
            MaxIterations = 5,
        };

        await runner.RunAsync("c", "first", null, default);
        await runner.RunAsync("c", "second", null, default);

        Assert.Equal(2, orchestrator.Requests.Count);
        var first = orchestrator.Requests[0];
        Assert.True(first.PlanMode);
        Assert.True(first.CompactWorkingContextFirst);
        Assert.Equal("deepseek-reasoner", first.ModelOverride);
        Assert.Equal(5, first.ExecutionLimits!.MaxIterations);
        Assert.Equal(CodingPermissionMode.AskRisky, first.Permissions!.Mode);
        Assert.False(orchestrator.Requests[1].CompactWorkingContextFirst);
        Assert.True(orchestrator.Requests[1].PlanMode);
        Assert.True(runner.PlanAwaitingApproval);
    }

    [Fact]
    public async Task File_mentions_attach_repository_files_to_the_lane_message_only()
    {
        var renderer = new ConsoleRenderer(new() { Quiet = true }, new StringWriter(), new StringWriter());
        var orchestrator = new RecordingOrchestrator();
        var runner = new TurnRunner(orchestrator, renderer, false) { RepositoryRoot = _repo };

        await runner.RunAsync("c", "Summarize @README.md please", null, default);

        var request = Assert.Single(orchestrator.Requests);
        Assert.StartsWith("Summarize @README.md please", request.UserMessage);
        Assert.Contains("```markdown README.md", request.UserMessage);
        Assert.Contains("# demo", request.UserMessage);
        Assert.Equal("Summarize @README.md please", request.AuthenticatedOriginalUserMessage);
    }

    [Fact]
    public async Task Renderer_accumulates_reported_usage_across_turns()
    {
        var renderer = new ConsoleRenderer(new() { Quiet = true }, new StringWriter(), new StringWriter());
        var orchestrator = new RecordingOrchestrator(new TurnUsageReported("job", "DeepSeek", "deepseek-chat", 100, 50, 0, 0.01m));
        var runner = new TurnRunner(orchestrator, renderer, false);

        await runner.RunAsync("c", "one", null, default);
        Assert.Equal(150, renderer.TurnTokens);
        await runner.RunAsync("c", "two", null, default);

        Assert.Equal(150, renderer.TurnTokens);
        Assert.Equal(300, runner.SessionTokens);
        Assert.Equal(0.02m, runner.SessionCostUsd);
    }

    [Fact]
    public void Renderer_reports_a_user_requested_compaction_in_plain_words()
    {
        var diagnostics = new StringWriter();
        var renderer = new ConsoleRenderer(new() { Quiet = false }, new StringWriter(), diagnostics);
        renderer.Render(new WorkingContextCompactionCompleted("job", SwarmCore.Agent.ToolUseEngine.WorkingContextCompactionReason.UserRequested, 50_000, 4_000, 3, true, true));
        renderer.Render(new WorkingContextCompactionCompleted("job", SwarmCore.Agent.ToolUseEngine.WorkingContextCompactionReason.UserRequested, 500, 500, 0, true, false));
        var text = diagnostics.ToString();
        Assert.Contains("context compacted: 50,000", text);
        Assert.Contains("nothing to compact yet", text);
    }
}
