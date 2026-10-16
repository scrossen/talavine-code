using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmCore.Agent.Runtime;
using SwarmCore.Agent.ToolUseEngine;
using SwarmCore.Coding;
using SwarmCore.Coding.Routing;

namespace SwarmCode.Hosting.Tests;

/// <summary>/compact and @file mentions through a real <see cref="ConversationRunner"/>.</summary>
public class ConversationRunnerParityTests : TempDirTest
{
    private static readonly CodingLane Lane = new("claude", "ClaudeCode", "claude-code:default");

    private sealed class RecordingOrchestrator(Func<CodingTurnRequest, IAsyncEnumerable<TurnEvent>> script) : ICodingTurnOrchestrator
    {
        public List<CodingTurnRequest> Requests { get; } = [];
        public IReadOnlyList<CodingLane> Lanes { get; } = [Lane];
        public CodingLane? ResolveLane(string? name) => Lane;
        public IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return script(request);
        }
    }

    private (ServiceProvider Provider, ConversationRunner Runner, RecordingOrchestrator Orchestrator) Build(string repositoryRoot, Func<CodingTurnRequest, IAsyncEnumerable<TurnEvent>> script)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConversationStore>(new FileConversationStore(Paths));
        services.AddSingleton(new FileConversationJobStore(Paths));
        services.AddSingleton<IPermissionBroker>(new UiPermissionBroker());
        services.AddSingleton(Options.Create(new CodingOptions { RepositoryRoot = repositoryRoot }));
        var orchestrator = new RecordingOrchestrator(script);
        services.AddSingleton<ICodingTurnOrchestrator>(orchestrator);
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        return (provider, new ConversationRunner(provider, "conv"), orchestrator);
    }

    private static Task<TurnResult> NextFinish(ConversationRunner runner)
    {
        var tcs = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.TurnFinished += r => tcs.TrySetResult(r);
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static async IAsyncEnumerable<TurnEvent> Completed(params TurnEvent[] events)
    {
        foreach (var e in events) { await Task.Yield(); yield return e; }
        yield return new TurnCompleted("t", "ok", TimeSpan.FromMilliseconds(5));
    }

    [Fact]
    public async Task Compact_marks_only_the_next_turn_and_reports_the_outcome()
    {
        var (provider, runner, orchestrator) = Build(Paths.RootDirectory, _ => Completed(
            new WorkingContextCompactionStarted("t", WorkingContextCompactionReason.UserRequested, 40_000),
            new WorkingContextCompactionCompleted("t", WorkingContextCompactionReason.UserRequested, 40_000, 3_000, 4, false, true)));
        using (provider)
        using (runner)
        {
            Assert.True(runner.RequestCompaction());
            Assert.True(runner.CompactNextTurn);

            var finished = NextFinish(runner);
            Assert.True(runner.TrySend("go on", "claude"));
            Assert.False(runner.RequestCompaction()); // refused while running
            await finished;

            Assert.True(orchestrator.Requests[0].CompactWorkingContextFirst);
            Assert.False(runner.CompactNextTurn);
            Assert.Contains(runner.Entries.OfType<NoticeEntry>(), n => n.Kind == NoticeKind.Compaction && n.Text.Contains("40,000"));

            var second = NextFinish(runner);
            Assert.True(runner.TrySend("and again", "claude"));
            await second;
            Assert.False(orchestrator.Requests[1].CompactWorkingContextFirst);
        }
    }

    [Fact]
    public async Task File_mentions_expand_for_the_lane_but_the_transcript_and_original_keep_the_typed_text()
    {
        var repo = Path.Combine(Paths.RootDirectory, "repo");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "notes.md"), "remember this\n");
        var (provider, runner, orchestrator) = Build(repo, _ => Completed());
        using (provider)
        using (runner)
        {
            var finished = NextFinish(runner);
            Assert.True(runner.TrySend("Read @notes.md and @nothing.txt", "claude"));
            await finished;

            var request = Assert.Single(orchestrator.Requests);
            Assert.StartsWith("Read @notes.md and @nothing.txt", request.UserMessage);
            Assert.Contains("```markdown notes.md", request.UserMessage);
            Assert.Contains("remember this", request.UserMessage);
            Assert.Equal("Read @notes.md and @nothing.txt", request.AuthenticatedOriginalUserMessage);
            Assert.Equal("Read @notes.md and @nothing.txt", runner.Entries.OfType<UserEntry>().Single().Text);
        }
    }

    [Fact]
    public async Task A_message_without_mentions_keeps_no_separate_original()
    {
        var (provider, runner, orchestrator) = Build(Paths.RootDirectory, _ => Completed());
        using (provider)
        using (runner)
        {
            var finished = NextFinish(runner);
            Assert.True(runner.TrySend("plain request", "claude"));
            await finished;
            var request = Assert.Single(orchestrator.Requests);
            Assert.Equal("plain request", request.UserMessage);
            Assert.Null(request.AuthenticatedOriginalUserMessage);
        }
    }
}
