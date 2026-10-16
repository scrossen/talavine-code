using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SwarmCore.Agent.Runtime;
using SwarmCore.Agent.ToolUseEngine;
using SwarmCore.Coding.Interaction;
using SwarmCore.Coding.Routing;

namespace SwarmCode.Hosting.Tests;

/// <summary>
/// <c>user.ask</c> in the SwarmCode hosts: the desktop broker round trip and its short-circuits, the runner's question
/// card (answer, skip, cancel, journal replay), the console broker's line parsing, and the native bridge serving it.
/// </summary>
public class UserQuestionHostingTests : TempDirTest
{
    private static readonly CodingLane Lane = new("claude", "ClaudeCode", "claude-code:default");

    private static UserQuestionRequest Question(string conversation = "conv", bool multiple = false, bool freeText = true, params string[] options)
        => new(conversation, "Which one?", options.Select(o => new UserQuestionOption(o, o + " description")).ToList(), multiple, freeText, "background");

    // ---- UiUserQuestionBroker ----------------------------------------------------------------------------------

    [Fact]
    public async Task Broker_publishes_to_subscribers_and_answer_resolves()
    {
        var broker = new UiUserQuestionBroker();
        PendingUserQuestion? seen = null;
        using var sub = broker.Subscribe(q => seen = q);
        var task = broker.AskAsync(Question(options: ["A", "B"]), default);
        Assert.False(task.IsCompleted);
        Assert.NotNull(seen);
        Assert.Equal("conv", seen!.ConversationId);
        Assert.Equal("Which one?", seen.Request.Question);
        Assert.Single(broker.PendingRequestIds);

        Assert.True(broker.Answer(seen.RequestId, " note ", ["B"]));
        var answer = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(answer.Answered);
        Assert.Equal("note", answer.Text);
        Assert.Equal(["B"], answer.Selected.ToArray());
        Assert.Empty(broker.PendingRequestIds);
        Assert.False(broker.Answer(seen.RequestId, "again", null)); // no longer pending
    }

    [Fact]
    public async Task Broker_decline_tells_the_model_to_decide()
    {
        var broker = new UiUserQuestionBroker();
        using var sub = broker.Subscribe(q => broker.Decline(q.RequestId));
        var answer = await broker.AskAsync(Question(), default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(answer.Answered);
        Assert.Equal(UserAnswer.DeclinedMessage, answer.Message);
    }

    [Fact]
    public async Task Broker_cancellation_drops_the_pending_question()
    {
        var broker = new UiUserQuestionBroker();
        using var sub = broker.Subscribe(_ => { });
        using var cts = new CancellationTokenSource();
        var task = broker.AskAsync(Question(), cts.Token);
        Assert.Single(broker.PendingRequestIds);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(broker.PendingRequestIds);
    }

    [Fact]
    public async Task Broker_without_subscribers_answers_no_one_at_once()
    {
        var broker = new UiUserQuestionBroker();
        var task = broker.AskAsync(Question(), default);
        Assert.True(task.IsCompleted);
        Assert.Equal(UserAnswer.NoOneMessage, (await task).Message);
        Assert.Empty(broker.PendingRequestIds);
    }

    [Fact]
    public async Task Broker_short_circuits_an_agents_session_and_an_unattended_conversation()
    {
        var sessions = new WorkspaceSessionCatalog(Paths);
        var parent = await sessions.CreateAsync(Tmp, "parent");
        var child = await sessions.CreateAsync(Tmp, "agent", parentSessionId: parent.Id);
        var broker = new UiUserQuestionBroker(sessions);
        var published = 0;
        using var sub = broker.Subscribe(_ => published++);

        var agent = await broker.AskAsync(Question(child.Id), default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(agent.Answered);
        Assert.Equal(UserAnswer.NoOneMessage, agent.Message);
        Assert.Equal(0, published);

        using (broker.Unattended(parent.Id))
        {
            Assert.True(broker.IsUnattended(parent.Id));
            var scheduled = await broker.AskAsync(Question(parent.Id), default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(UserAnswer.NoOneMessage, scheduled.Message);
            Assert.Equal(0, published);
        }
        Assert.False(broker.IsUnattended(parent.Id));

        var attended = broker.AskAsync(Question(parent.Id), default);
        // The session lookup is file I/O: the card is published once it has decided someone may be watching.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (published == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.False(attended.IsCompleted);
        Assert.Equal(1, published);
        Assert.Single(broker.PendingRequestIds);
        broker.ClearConversation(parent.Id);
        Assert.False((await attended.WaitAsync(TimeSpan.FromSeconds(5))).Answered);
    }

    // ---- ConversationRunner ------------------------------------------------------------------------------------

    private sealed class FakeOrchestrator(Func<CodingTurnRequest, CancellationToken, IAsyncEnumerable<TurnEvent>> script) : ICodingTurnOrchestrator
    {
        public IReadOnlyList<CodingLane> Lanes { get; } = [Lane];
        public CodingLane? ResolveLane(string? name) => Lane;
        public IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, CancellationToken ct = default) => script(request, ct);
    }

    private sealed record Harness(ServiceProvider Provider, UiUserQuestionBroker Broker, FileTranscriptJournal Journal) : IDisposable
    {
        public ConversationRunner Open(string conversationId) => new(Provider, conversationId);
        public void Dispose() => Provider.Dispose();
    }

    private Harness Build(Func<IServiceProvider, Func<CodingTurnRequest, CancellationToken, IAsyncEnumerable<TurnEvent>>> script)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var journal = new FileTranscriptJournal(Paths);
        var broker = new UiUserQuestionBroker(new WorkspaceSessionCatalog(Paths));
        services.AddSingleton(journal);
        services.AddSingleton<IConversationStore>(new FileConversationStore(Paths));
        services.AddSingleton<IAgentSessionStore>(new FileAgentSessionStore(Paths));
        services.AddSingleton(new FileConversationJobStore(Paths));
        services.AddSingleton<IPermissionBroker>(new UiPermissionBroker());
        services.AddSingleton<IUserQuestionBroker>(broker);
        services.AddSingleton<ICodingTurnOrchestrator>(sp => new FakeOrchestrator(script(sp)));
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        return new Harness(provider, broker, journal);
    }

    private static Task<TurnResult> NextFinish(ConversationRunner runner)
    {
        var tcs = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.TurnFinished += r => tcs.TrySetResult(r);
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static async Task<QuestionEntry> WaitForQuestion(ConversationRunner runner)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (runner.Entries.OfType<QuestionEntry>().FirstOrDefault() is { } entry) return entry;
            await Task.Delay(10);
        }
        throw new TimeoutException("No question card appeared.");
    }

    /// <summary>A turn whose lane asks the question through the tool, as an engine lane would.</summary>
    private static Func<IServiceProvider, Func<CodingTurnRequest, CancellationToken, IAsyncEnumerable<TurnEvent>>> AskingTurn(UserAskInput input, Action<UserAskResult> observe)
        => sp => (request, ct) => Script(sp, request, input, observe, ct);

    private static async IAsyncEnumerable<TurnEvent> Script(IServiceProvider sp, CodingTurnRequest request, UserAskInput input, Action<UserAskResult> observe, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new CodingLaneSelected("t", Lane, 1);
        var tool = UserQuestionTools.Create(sp.GetRequiredService<IUserQuestionBroker>(), () => request.ConversationId);
        observe((UserAskResult)(await tool.Invoke(input, ct))!);
        yield return new TurnCompleted("t", "ok", TimeSpan.Zero);
    }

    [Fact]
    public async Task Runner_shows_a_question_card_and_the_answer_reaches_the_tool_and_the_journal()
    {
        UserAskResult? result = null;
        using var h = Build(AskingTurn(new UserAskInput("Tabs or spaces?", [new UserQuestionOption("Tabs", "wide"), new UserQuestionOption("Spaces")], Context: "The repo mixes both."), r => result = r));
        var other = h.Open("other-session"); // another open session of the same workspace never shows this card
        using var runner = h.Open("conv");
        var finished = NextFinish(runner);
        Assert.True(runner.TrySend("format the repo", "claude"));

        var entry = await WaitForQuestion(runner);
        Assert.True(entry.IsPending);
        Assert.Equal("Tabs or spaces?", entry.Question);
        Assert.Equal("The repo mixes both.", entry.Context);
        Assert.Equal(["Tabs", "Spaces"], entry.Options.Select(o => o.Label).ToArray());
        Assert.Equal("wide", entry.Options[0].Description);
        Assert.False(entry.AllowMultiple);
        Assert.True(entry.AllowFreeText);
        Assert.DoesNotContain(other.Entries, e => e is QuestionEntry);
        other.Dispose();

        Assert.True(runner.AnswerQuestion(entry.RequestId, "but keep the Makefile", ["Spaces"]));
        var turn = await finished;
        Assert.True(turn.Completed);
        Assert.True(result!.Answered);
        Assert.Equal("but keep the Makefile", result.Text);
        Assert.Equal(["Spaces"], result.Selected.ToArray());
        Assert.True(entry.Answered);
        Assert.Equal(["Spaces"], entry.Selected.ToArray());
        Assert.Equal("but keep the Makefile", entry.Answer);

        await runner.FlushJournalAsync();
        using var reopened = h.Open("conv");
        await reopened.LoadHistoryAsync();
        var replayed = Assert.Single(reopened.Entries.OfType<QuestionEntry>());
        Assert.Equal(entry.Id, replayed.Id);
        Assert.True(replayed.Answered);
        Assert.Equal("Tabs or spaces?", replayed.Question);
        Assert.Equal("The repo mixes both.", replayed.Context);
        Assert.Equal(["Tabs", "Spaces"], replayed.Options.Select(o => o.Label).ToArray());
        Assert.Equal(["Spaces"], replayed.Selected.ToArray());
        Assert.Equal("but keep the Makefile", replayed.Answer);
    }

    [Fact]
    public async Task Runner_skip_declines_and_cancel_closes_a_pending_card()
    {
        UserAskResult? result = null;
        using var h = Build(AskingTurn(new UserAskInput("Proceed?"), r => result = r));
        using var runner = h.Open("conv");
        var finished = NextFinish(runner);
        Assert.True(runner.TrySend("go", "claude"));
        var entry = await WaitForQuestion(runner);
        Assert.True(runner.DeclineQuestion(entry.RequestId));
        await finished;
        Assert.False(result!.Answered);
        Assert.Equal(UserAnswer.DeclinedMessage, result.Message);
        Assert.False(entry.Answered);
        Assert.False(entry.IsPending);

        // A stopped turn can never take an answer: its card closes as skipped and the broker holds nothing.
        using var h2 = Build(AskingTurn(new UserAskInput("Still there?"), _ => { }));
        using var stopped = h2.Open("conv2");
        var stoppedFinish = NextFinish(stopped);
        Assert.True(stopped.TrySend("go", "claude"));
        var pending = await WaitForQuestion(stopped);
        Assert.Single(h2.Broker.PendingRequestIds);
        stopped.Cancel();
        var outcome = await stoppedFinish;
        Assert.True(outcome.Cancelled);
        Assert.False(pending.IsPending);
        Assert.False(pending.Answered);
        Assert.Empty(h2.Broker.PendingRequestIds);
    }

    [Fact]
    public async Task Runner_clear_declines_a_pending_question()
    {
        UserAskResult? result = null;
        using var h = Build(AskingTurn(new UserAskInput("Which?", [new UserQuestionOption("A"), new UserQuestionOption("B")]), r => result = r));
        using var runner = h.Open("conv");
        var finished = NextFinish(runner);
        Assert.True(runner.TrySend("go", "claude"));
        await WaitForQuestion(runner);
        runner.ClearTranscript();
        await finished;
        Assert.False(result!.Answered);
        Assert.Empty(h.Broker.PendingRequestIds);
    }

    [Fact]
    public async Task A_scheduled_turn_gets_no_one_at_once_and_shows_no_card()
    {
        UserAskResult? result = null;
        using var h = Build(AskingTurn(new UserAskInput("Deploy?"), r => result = r));
        using var runner = h.Open("conv");
        var finished = NextFinish(runner);
        Assert.True(runner.TrySend("nightly", "claude", origin: new TurnOrigin(TurnOrigin.ScheduleAuthority, Unattended: false)));
        var turn = await finished;
        Assert.True(turn.Completed);
        Assert.False(result!.Answered);
        Assert.Equal(UserAnswer.NoOneMessage, result.Message);
        Assert.DoesNotContain(runner.Entries, e => e is QuestionEntry);
        Assert.False(h.Broker.IsUnattended("conv"));
    }

    [Fact]
    public void Journal_round_trips_a_question_entry_and_replays_a_pending_one_as_skipped()
    {
        var journal = new FileTranscriptJournal(Paths);
        var answered = new QuestionEntry
        {
            RequestId = "r1", Question = "Q?", Context = "ctx", Options = [new QuestionOption("A", "a"), new QuestionOption("B")],
            AllowMultiple = true, AllowFreeText = false, Answered = true, Selected = ["A", "B"], Answer = null,
        };
        var pending = new QuestionEntry { RequestId = "r2", Question = "Later?" };
        journal.AppendRange("conv", [answered, pending]);
        var loaded = journal.Load("conv").Entries.OfType<QuestionEntry>().ToList();
        Assert.Equal(2, loaded.Count);
        Assert.True(loaded[0].Answered);
        Assert.Equal("ctx", loaded[0].Context);
        Assert.Equal(["A", "B"], loaded[0].Selected.ToArray());
        Assert.Equal("a", loaded[0].Options[0].Description);
        Assert.True(loaded[0].AllowMultiple);
        Assert.False(loaded[0].AllowFreeText);
        Assert.False(loaded[1].Answered);
        Assert.False(loaded[1].IsPending);
        Assert.True(loaded[1].AllowFreeText);
    }

    // ---- ConsoleUserQuestionBroker -----------------------------------------------------------------------------

    private static async Task<UserAnswer> Console(string typed, UserQuestionRequest request, bool nonInteractive = false, bool redirected = false, StringWriter? output = null)
        => await new ConsoleUserQuestionBroker(nonInteractive, promptToStderr: false, new StringReader(typed + "\n"), output ?? new StringWriter(), redirected).AskAsync(request, default);

    [Fact]
    public async Task Console_numbers_select_options()
    {
        var single = Question(options: ["Alpha", "Beta", "Gamma"]);
        var one = await Console("2", single);
        Assert.True(one.Answered);
        Assert.Equal(["Beta"], one.Selected.ToArray());
        Assert.Null(one.Text);

        var onlyFirst = await Console("1, 3", single); // several numbers on a single-choice question: the first counts
        Assert.Equal(["Alpha"], onlyFirst.Selected.ToArray());

        var multi = await Console("3,1, 3", Question(multiple: true, options: ["Alpha", "Beta", "Gamma"]));
        Assert.Equal(["Gamma", "Alpha"], multi.Selected.ToArray());

        var byLabel = await Console("gamma", single);
        Assert.Equal(["Gamma"], byLabel.Selected.ToArray());
    }

    [Fact]
    public async Task Console_free_text_empty_line_and_disallowed_text()
    {
        var withOptions = Question(options: ["Alpha", "Beta"]);
        var text = await Console("something else entirely", withOptions);
        Assert.True(text.Answered);
        Assert.Equal("something else entirely", text.Text);
        Assert.Empty(text.Selected);

        var nine = await Console("9", withOptions); // not an option number: free text
        Assert.Equal("9", nine.Text);

        var skipped = await Console("   ", withOptions);
        Assert.False(skipped.Answered);
        Assert.Equal(UserAnswer.DeclinedMessage, skipped.Message);

        var noText = await Console("maybe", Question(freeText: false, options: ["Alpha", "Beta"]));
        Assert.False(noText.Answered);
        Assert.Contains("did not match an option", noText.Message);
    }

    [Fact]
    public async Task Console_prints_the_question_and_short_circuits_when_nobody_can_type()
    {
        var output = new StringWriter();
        var request = Question(multiple: true, options: ["Alpha", "Beta"]);
        await Console("1", request, output: output);
        var printed = output.ToString();
        Assert.Contains("Which one?", printed);
        Assert.Contains("background", printed);
        Assert.Contains("1) Alpha - Alpha description", printed);
        Assert.Contains("2) Beta", printed);
        Assert.Contains("numbers separated by commas", printed);
        Assert.Contains("empty to skip", printed);

        var quiet = new StringWriter();
        var prompt = await Console("1", request, nonInteractive: true, output: quiet);
        Assert.Equal(UserAnswer.NoOneMessage, prompt.Message);
        Assert.Equal("", quiet.ToString());

        var redirected = await Console("1", request, redirected: true, output: quiet);
        Assert.Equal(UserAnswer.NoOneMessage, redirected.Message);
        Assert.Equal("", quiet.ToString());
    }

    // ---- native bridge -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Bridge_serves_user_ask_to_native_lanes_including_plan_turns()
    {
        await using var host = await BridgedWorkspace.StartAsync(Paths, Path.Combine(Tmp, "repo"));
        Assert.IsType<UiUserQuestionBroker>(host.Services.GetRequiredService<IUserQuestionBroker>());
        var acting = await host.Turn("session-1").ListAsync(default);
        var tool = Assert.Single(acting, t => t.Name == "user_ask");
        Assert.True(tool.ReadOnly);
        Assert.Contains(await host.Turn("session-2", planMode: true).ListAsync(default), t => t.Name == "user_ask");

        // Nobody subscribed in this workspace: the native lane is told to decide, and the call never blocks.
        var result = await host.Turn("session-3").CallAsync("user_ask", BridgedWorkspace.Args(new { question = "Ship?", options = new[] { new { label = "yes" }, new { label = "no" } } }), default)
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(result.IsError, result.Text);
        using var json = BridgedWorkspace.Json(result);
        Assert.False(json.RootElement.GetProperty("Answered").GetBoolean());
        Assert.Contains("Decide yourself", json.RootElement.GetProperty("Message").GetString());
    }
}
