using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Runtime;
using SwarmCore.Agent.ToolUseEngine;
using SwarmCore.Coding.Routing;

namespace SwarmCode.Hosting.Tests;

/// <summary>
/// The presentation journal: entries round-trip through the append-only file, a reopened
/// session replays tool cards and notices, sessions created before the journal are rebuilt
/// from the stores and migrated, and a crash mid-turn still surfaces what ran.
/// </summary>
public class TranscriptJournalTests : TempDirTest
{
    private static readonly CodingLane Lane = new("claude", "ClaudeCode", "claude-code:default");

    private sealed class FakeOrchestrator(Func<CodingTurnRequest, CancellationToken, IAsyncEnumerable<TurnEvent>> script) : ICodingTurnOrchestrator
    {
        public IReadOnlyList<CodingLane> Lanes { get; } = [Lane];
        public CodingLane? ResolveLane(string? name) => Lane;
        public IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, CancellationToken ct = default) => script(request, ct);
    }

    private sealed record Harness(ServiceProvider Provider, FileConversationStore Store, FileAgentSessionStore Transcripts, FileTranscriptJournal Journal) : IDisposable
    {
        public ConversationRunner Open(string conversationId) => new(Provider, conversationId);
        public void Dispose() => Provider.Dispose();
    }

    private Harness Build(Func<CodingTurnRequest, CancellationToken, IAsyncEnumerable<TurnEvent>>? script = null, IAgentSessionStore? transcriptStore = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var store = new FileConversationStore(Paths);
        var transcripts = new FileAgentSessionStore(Paths);
        var journal = new FileTranscriptJournal(Paths);
        services.AddSingleton<IConversationStore>(store);
        services.AddSingleton<IAgentSessionStore>(transcriptStore ?? transcripts);
        services.AddSingleton(journal);
        services.AddSingleton<IPermissionBroker>(new UiPermissionBroker());
        services.AddSingleton<ICodingTurnOrchestrator>(new FakeOrchestrator(script ?? ((_, _) => Yield())));
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        return new Harness(provider, store, transcripts, journal);
    }

    private static async IAsyncEnumerable<TurnEvent> Yield(params TurnEvent[] events)
    {
        foreach (var e in events) { await Task.Yield(); yield return e; }
    }

    private static Task<TurnResult> NextFinish(ConversationRunner runner)
    {
        var tcs = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.TurnFinished += r => tcs.TrySetResult(r);
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void Entries_round_trip_in_order_and_later_lines_replace_earlier_ones()
    {
        var journal = new FileTranscriptJournal(Paths);
        var tool = new ToolCallEntry { ToolCallId = "c1", ToolName = "shell.exec", InputJson = "{\"command\":\"ls\"}" };
        var assistant = new AssistantEntry { LaneName = "claude", Duration = TimeSpan.FromSeconds(2), IsStreaming = false };
        assistant.AppendText("done");
        assistant.AppendReasoning("thinking");
        journal.Append("conv", new UserEntry { Text = "hi" });
        journal.Append("conv", new NoticeEntry { Kind = NoticeKind.LaneSelected, Text = "claude (x)" });
        journal.Append("conv", tool); // running
        tool.Result = "ok"; tool.State = ToolCallState.Completed; tool.Elapsed = TimeSpan.FromMilliseconds(5);
        journal.Append("conv", tool); // completed: replaces the running line
        journal.Append("conv", assistant);
        journal.Append("conv", new PermissionEntry { RequestId = "r1", Title = "t", Permissions = ["fs"], Approved = true });

        var loaded = new FileTranscriptJournal(Paths).Load("conv").Entries;
        Assert.Collection(loaded,
            e => Assert.Equal("hi", Assert.IsType<UserEntry>(e).Text),
            e => Assert.Equal(NoticeKind.LaneSelected, Assert.IsType<NoticeEntry>(e).Kind),
            e =>
            {
                var t = Assert.IsType<ToolCallEntry>(e);
                Assert.Equal(("c1", "shell.exec", "ok", ToolCallState.Completed), (t.ToolCallId, t.ToolName, t.Result, t.State));
                Assert.Equal(TimeSpan.FromMilliseconds(5), t.Elapsed);
                Assert.Equal(tool.Id, t.Id);
            },
            e =>
            {
                var a = Assert.IsType<AssistantEntry>(e);
                Assert.Equal(("done", "thinking", "claude"), (a.Text, a.Reasoning, a.LaneName));
                Assert.False(a.IsStreaming);
                Assert.Equal(TimeSpan.FromSeconds(2), a.Duration);
            },
            e => Assert.True(Assert.IsType<PermissionEntry>(e).Approved));
        Assert.Equal(6, File.ReadAllLines(Path.Combine(Paths.SessionsDirectory, "presentation", "conv.jsonl")).Length);
    }

    [Fact]
    public void Clear_line_discards_everything_before_it_records_the_watermark_and_corrupt_lines_are_skipped()
    {
        var journal = new FileTranscriptJournal(Paths);
        journal.Append("conv", new UserEntry { Text = "old" });
        journal.AppendClear("conv", transcriptMessageCount: 7);
        var path = Path.Combine(Paths.SessionsDirectory, "presentation", "conv.jsonl");
        Assert.Single(File.ReadAllLines(path)); // the clear truncates the file to the marker
        File.AppendAllText(path, "garbage\n");
        journal.Append("conv", new UserEntry { Text = "new" });
        var loaded = journal.Load("conv");
        Assert.Equal("new", Assert.IsType<UserEntry>(Assert.Single(loaded.Entries)).Text);
        Assert.Equal(7, loaded.ReconcileFromMessage);
        Assert.True(journal.Exists("conv"));
    }

    [Fact]
    public async Task Clearing_a_session_with_tool_history_does_not_resurrect_its_cards_on_reopen()
    {
        using var h = Build((_, _) => Yield(
            new ToolCallStarted("t", "tc1", "shell.exec", new JsonObject { ["command"] = "ls" }),
            new ToolCallCompleted("t", "tc1", "shell.exec", "files", false, TimeSpan.FromMilliseconds(3)),
            new TurnCompleted("t", "listed", TimeSpan.Zero)));
        // The provider transcript holds the same call, as the real orchestrator would persist it.
        await h.Transcripts.AppendRangeAsync("conv",
        [
            ChatMessage.Text(ChatRole.User, "list files"),
            new ChatMessage(ChatRole.Assistant, [new ToolUsePart("tc1", "shell.exec", new JsonObject { ["command"] = "ls" })]),
            new ChatMessage(ChatRole.Tool, [new ToolResultPart("tc1", "files")]),
            ChatMessage.Text(ChatRole.Assistant, "listed"),
        ]);
        var runner = h.Open("conv");
        var finished = NextFinish(runner);
        Assert.True(runner.TrySend("list files", "claude"));
        await finished;
        runner.ClearTranscript();
        await runner.FlushJournalAsync();
        Assert.Empty(runner.Entries);

        var again = h.Open("conv");
        await again.LoadHistoryAsync();
        Assert.Empty(again.Entries);
    }

    [Fact]
    public async Task Legacy_rebuild_aligns_tool_cards_by_user_text_when_an_earlier_turn_never_reached_the_provider()
    {
        using var h = Build();
        // Turn one died on a dead lane: a store row exists, but nothing was persisted to the transcript.
        var dead = await h.Store.AppendUserTurnAsync("legacy", "first ask");
        _ = dead;
        var live = await h.Store.AppendUserTurnAsync("legacy", "second ask");
        await h.Store.CompleteAssistantTurnAsync(live.Id, "done", null, TimeSpan.Zero);
        await h.Transcripts.AppendRangeAsync("legacy",
        [
            ChatMessage.Text(ChatRole.User, "second ask"),
            new ChatMessage(ChatRole.Assistant, [new ToolUsePart("c2", "shell.exec", new JsonObject { ["command"] = "go" })]),
            new ChatMessage(ChatRole.Tool, [new ToolResultPart("c2", "ok")]),
            ChatMessage.Text(ChatRole.Assistant, "done"),
        ]);

        var runner = h.Open("legacy");
        await runner.LoadHistoryAsync();
        Assert.Collection(runner.Entries,
            e => Assert.Equal("first ask", Assert.IsType<UserEntry>(e).Text),
            e => Assert.Equal("second ask", Assert.IsType<UserEntry>(e).Text),
            e => Assert.Equal("c2", Assert.IsType<ToolCallEntry>(e).ToolCallId),
            e => Assert.Equal("done", Assert.IsType<AssistantEntry>(e).Text));
    }

    [Fact]
    public async Task A_reopened_session_replays_tool_cards_notices_and_the_answer()
    {
        using var h = Build((_, _) => Yield(
            new CodingLaneSelected("t", Lane, 1),
            new TextChunk("t", "Working"),
            new ToolCallStarted("t", "tc1", "code.edit_file", new JsonObject { ["path"] = "a.cs" }),
            new ToolCallCompleted("t", "tc1", "code.edit_file", "edited", false, TimeSpan.FromMilliseconds(42)),
            new CodingLaneHandOff("t", Lane, new CodingLane("codex", "CodexCli", "codex-cli:default"), "stopped early"),
            new TurnCompleted("t", "All done", TimeSpan.FromSeconds(1))));
        var runner = h.Open("conv");
        var finished = NextFinish(runner);
        Assert.True(runner.TrySend("do it", "claude"));
        await finished;
        await runner.FlushJournalAsync();
        runner.Dispose();

        var reopened = h.Open("conv");
        await reopened.LoadHistoryAsync();
        var entries = reopened.Entries;
        Assert.Equal("do it", Assert.IsType<UserEntry>(entries[0]).Text);
        Assert.Equal(NoticeKind.LaneSelected, Assert.IsType<NoticeEntry>(entries[1]).Kind);
        var working = Assert.IsType<AssistantEntry>(entries[2]);
        Assert.Equal("Working", working.Text);
        Assert.False(working.IsStreaming);
        var card = Assert.IsType<ToolCallEntry>(entries[3]);
        Assert.Equal(("tc1", "code.edit_file", "edited", ToolCallState.Completed), (card.ToolCallId, card.ToolName, card.Result, card.State));
        Assert.Contains("a.cs", card.InputJson);
        Assert.Equal(NoticeKind.HandOff, Assert.IsType<NoticeEntry>(entries[4]).Kind);
        var answer = Assert.IsType<AssistantEntry>(entries[5]);
        Assert.Equal("All done", answer.Text);
        Assert.Equal("claude", answer.LaneName);
        Assert.Equal(6, entries.Count);
    }

    [Fact]
    public async Task A_session_from_before_the_journal_is_rebuilt_from_the_stores_and_migrated()
    {
        using var h = Build();
        var turn = await h.Store.AppendUserTurnAsync("legacy", "fix the build");
        await h.Store.CompleteAssistantTurnAsync(turn.Id, "Fixed.", null, TimeSpan.FromSeconds(3), reasoningContent: "looked at it");
        await h.Transcripts.AppendRangeAsync("legacy",
        [
            ChatMessage.Text(ChatRole.User, "fix the build"),
            new ChatMessage(ChatRole.Assistant, [new ToolUsePart("c1", "shell.exec", new JsonObject { ["command"] = "dotnet build" })]),
            new ChatMessage(ChatRole.Tool, [new ToolResultPart("c1", "{\"exitCode\":0}")]),
            ChatMessage.Text(ChatRole.User, "(Automatic notice) keep going"),
            ChatMessage.Text(ChatRole.Assistant, "Fixed."),
        ]);
        Assert.False(h.Journal.Exists("legacy"));

        var runner = h.Open("legacy");
        await runner.LoadHistoryAsync();
        var entries = runner.Entries;
        Assert.Collection(entries,
            e => Assert.Equal("fix the build", Assert.IsType<UserEntry>(e).Text),
            e =>
            {
                var t = Assert.IsType<ToolCallEntry>(e);
                Assert.Equal(("c1", "shell.exec", ToolCallState.Completed), (t.ToolCallId, t.ToolName, t.State));
                Assert.Equal("{\"exitCode\":0}", t.Result);
            },
            e =>
            {
                var a = Assert.IsType<AssistantEntry>(e);
                Assert.Equal(("Fixed.", "looked at it"), (a.Text, a.Reasoning));
            });
        // Migrated: the journal now exists and a second open replays it identically.
        Assert.True(h.Journal.Exists("legacy"));
        var again = h.Open("legacy");
        await again.LoadHistoryAsync();
        Assert.Equal(entries.Select(e => e.Id), again.Entries.Select(e => e.Id));
    }

    [Fact]
    public async Task A_turn_cut_off_by_a_crash_still_shows_the_call_that_ran()
    {
        using var h = Build();
        // The journal saw the user message and nothing else; the transcript (made durable
        // before dispatch) carries the tool request without a result.
        h.Journal.Append("crashed", new UserEntry { Text = "deploy it" });
        await h.Transcripts.AppendRangeAsync("crashed",
        [
            ChatMessage.Text(ChatRole.User, "deploy it"),
            new ChatMessage(ChatRole.Assistant, [new ToolUsePart("c9", "shell.exec", new JsonObject { ["command"] = "deploy.ps1" })]),
        ]);

        var runner = h.Open("crashed");
        await runner.LoadHistoryAsync();
        Assert.Collection(runner.Entries,
            e => Assert.IsType<UserEntry>(e),
            e =>
            {
                var t = Assert.IsType<ToolCallEntry>(e);
                Assert.Equal(("c9", ToolCallState.Unknown, TranscriptHistory.InterruptedToolResult), (t.ToolCallId, t.State, t.Result));
            },
            e => Assert.Equal(NoticeKind.Info, Assert.IsType<NoticeEntry>(e).Kind));
        // The reconciliation is journalled, so a second open does not duplicate it.
        var again = h.Open("crashed");
        await again.LoadHistoryAsync();
        Assert.Equal(3, again.Entries.Count);
    }

    [Fact]
    public async Task Clearing_while_a_turn_is_running_does_not_journal_its_in_flight_entries()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<TurnEvent> Script(CodingTurnRequest _, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return new TextChunk("t", "working...");
            yield return new ToolCallStarted("t", "tc1", "shell.exec", new JsonObject { ["command"] = "ls" });
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            yield return new ToolCallCompleted("t", "tc1", "shell.exec", "files", false, TimeSpan.FromMilliseconds(3));
            yield return new TurnCompleted("t", "listed", TimeSpan.Zero);
        }
        using var h = Build(Script);
        var runner = h.Open("conv");
        var finished = NextFinish(runner);
        Assert.True(runner.TrySend("list files", "claude"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        runner.ClearTranscript(); // the bubble and the tool card are still in flight
        release.TrySetResult();
        await finished;
        await runner.FlushJournalAsync();

        // The cleared bubble and tool card stay gone; only the answer that arrived after the
        // clear is visible, and a reopen shows exactly the same thing.
        var after = runner.Entries;
        var answer = Assert.IsType<AssistantEntry>(Assert.Single(after));
        Assert.Equal("listed", answer.Text);
        var again = h.Open("conv");
        await again.LoadHistoryAsync();
        Assert.Equal(after.Select(e => e.Id), again.Entries.Select(e => e.Id));
    }

    [Fact]
    public async Task The_clear_marker_is_written_even_when_the_transcript_store_fails()
    {
        using var h = Build((_, _) => Yield(new TurnCompleted("t", "ok", TimeSpan.Zero)), new ThrowingSessionStore());
        var runner = h.Open("conv");
        var finished = NextFinish(runner);
        Assert.True(runner.TrySend("hello", "claude"));
        await finished;
        runner.ClearTranscript();
        await runner.FlushJournalAsync();
        var contents = h.Journal.Load("conv");
        Assert.Empty(contents.Entries);
        Assert.Equal(0, contents.ReconcileFromMessage);
    }

    private sealed class ThrowingSessionStore : IAgentSessionStore
    {
        public Task<List<ChatMessage>> LoadAsync(string conversationId, CancellationToken ct = default) => throw new IOException("disk gone");
        public Task AppendAsync(string conversationId, ChatMessage message, CancellationToken ct = default) => Task.CompletedTask;
        public Task AppendRangeAsync(string conversationId, IReadOnlyList<ChatMessage> messages, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task Clearing_the_transcript_survives_a_reopen()
    {
        using var h = Build((_, _) => Yield(new TurnCompleted("t", "ok", TimeSpan.Zero)));
        var runner = h.Open("conv");
        var finished = NextFinish(runner);
        Assert.True(runner.TrySend("hello", "claude"));
        await finished;
        runner.ClearTranscript();
        await runner.FlushJournalAsync();
        Assert.Empty(runner.Entries);
        var again = h.Open("conv");
        await again.LoadHistoryAsync();
        Assert.Empty(again.Entries);
    }
}
