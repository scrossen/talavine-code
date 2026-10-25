using Bunit;
using SwarmCode.App.Components.Chat;
using SwarmCore.Agent.Continual;
using SwarmCode.Hosting;
using SwarmCore.Coding.Planning;

namespace SwarmCode.App.Tests;

public sealed class SessionActivityTests : BunitContext
{
    private static readonly DateTimeOffset At = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private static ContinualObjectiveSnapshot Goal(ContinualObjectiveStatus status) => new()
    {
        Id = Guid.NewGuid(), Surface = "coding-goal", PartitionKey = "test", ConversationId = "chat", ExecutorKey = "test",
        IdempotencyKey = "test", ObjectiveText = "Full objective stays out of the activity panel", ObjectiveSummary = "Test",
        Authority = ContinualObjectiveAuthority.UserRequested("test"), Status = status, LastProgressAtUtc = At,
    };

    [Theory]
    [InlineData(ContinualObjectiveStatus.Paused, "Paused")]
    [InlineData(ContinualObjectiveStatus.RetryScheduled, "Waiting to retry")]
    [InlineData(ContinualObjectiveStatus.AttentionRequired, "Needs attention")]
    [InlineData(ContinualObjectiveStatus.Uncertain, "Needs attention")]
    [InlineData(ContinualObjectiveStatus.WaitingForInput, "Needs your input")]
    public void Durable_state_wins_over_a_running_goal_loop(ContinualObjectiveStatus status, string expected)
    {
        var activity = SessionActivityView.Create(true, "Running", Goal(status), []);
        Assert.Equal(expected, activity.State);
        Assert.False(activity.Busy);
        Assert.DoesNotContain("Current action", activity.Action);
    }

    [Fact]
    public void Uncertain_reason_is_plain_and_full_diagnostics_are_folded()
    {
        var activity = SessionActivityView.Create(true, "Running", Goal(ContinualObjectiveStatus.Uncertain) with
        { LastFailure = new string('x', 5000), LastOutput = "Last verified file readback" }, []);
        var view = Render<SessionActivity>(p => p.Add(c => c.Activity, activity).Add(c => c.InboxUrl, "/goals/test"));
        Assert.Contains("uncertain outcome", view.Find(".sess-current__reason").TextContent);
        Assert.Equal(5000, view.Find("details pre").TextContent.Length);
        Assert.False(view.Find("details").HasAttribute("open"));
        Assert.DoesNotContain("Full objective", view.Markup);
        Assert.Equal("/goals/test", view.Find("a").GetAttribute("href"));
    }

    [Fact]
    public void Current_action_and_recorded_update_do_not_invent_a_timestamp_or_reuse_old_pending_tools()
    {
        var old = new ToolCallEntry { ToolCallId = "old", ToolName = "old.action", AtUtc = At.UtcDateTime };
        var user = new UserEntry { Text = "New turn", AtUtc = At.AddMinutes(1).UtcDateTime };
        var empty = SessionActivityView.Create(true, "Running", null, [old, user]);
        Assert.Null(empty.RecordedAt);
        Assert.Equal("No progress update recorded yet.", empty.Update);
        Assert.DoesNotContain("old.action", empty.Action);
        var assistant = new AssistantEntry { AtUtc = At.AddMinutes(2).UtcDateTime };
        assistant.AppendText("Checking the saved changes.");
        var current = new ToolCallEntry { ToolCallId = "current", ToolName = "fs.read_file", AtUtc = At.AddMinutes(3).UtcDateTime };
        var activity = SessionActivityView.Create(true, "Running", null, [old, user, assistant, current]);
        Assert.Equal("Current action: fs.read_file", activity.Action);
        Assert.Equal(assistant.AtUtc, activity.RecordedAt!.Value.UtcDateTime);
        Assert.Equal(assistant.Text, activity.Update);
    }

    [Fact]
    public void Retry_date_is_only_the_saved_schedule_and_pending_question_remains_visible()
    {
        var retry = At.AddMinutes(5);
        var waiting = SessionActivityView.Create(true, "Running", Goal(ContinualObjectiveStatus.RetryScheduled) with { NextAttemptAtUtc = retry }, []);
        Assert.Equal(retry, waiting.RetryAt);
        var question = new QuestionEntry { RequestId = "q", Question = "Which folder?" };
        var activity = SessionActivityView.Create(true, "Running", null, [question]);
        Assert.Equal("Needs your input", activity.State);
        Assert.Equal(question.Question, activity.Reason);
        Assert.False(activity.Busy);
    }

    [Theory]
    [InlineData(ContinualObjectiveStatus.Completed)]
    [InlineData(ContinualObjectiveStatus.Cancelled)]
    public void An_ordinary_turn_does_not_inherit_a_previous_goals_terminal_status(ContinualObjectiveStatus status)
    {
        var activity = SessionActivityView.Create(true, "Running", Goal(status), [], useGoalState: false);
        Assert.Equal("Working", activity.State);
        Assert.True(activity.Busy);
    }

    [Fact]
    public void Header_waiting_label_does_not_weaken_running_fork_guard()
    {
        var view = Render<SessionHeader>(p => p.Add(c => c.IsRunning, true).Add(c => c.DisplayStatus, "Waiting to retry")
            .Add(c => c.ActiveAgents, 1).Add(c => c.WaitingAgents, 2));
        Assert.Equal("Waiting to retry", view.Find("[role=status]").TextContent);
        Assert.True(view.FindAll("button").Single(b => b.TextContent == "Fork conversation").HasAttribute("disabled"));
        Assert.Contains("1 agent working", view.Markup);
        Assert.Contains("2 agents waiting", view.Markup);
    }

    [Fact]
    public void An_accepted_continuation_does_not_hide_a_still_running_batch()
    {
        var tool = new ToolCallEntry { ToolCallId = "active", ToolName = "fs.read_file", AtUtc = At.AddMinutes(1).UtcDateTime };
        var correction = new UserEntry { Text = "A new inbox message", IsContinuation = true, AtUtc = At.AddMinutes(2).UtcDateTime };
        var activity = SessionActivityView.Create(true, "Running", null,
            [new UserEntry { Text = "Start", AtUtc = At.UtcDateTime }, tool, correction], phase: "working", jobStartedUtc: At.UtcDateTime);
        Assert.Equal("Current action: fs.read_file", activity.Action);
        Assert.Equal(tool.AtUtc, activity.ActionStartedAt!.Value.UtcDateTime);
    }

    [Fact]
    public void A_previous_job_tool_is_not_presented_as_current_after_resume()
    {
        var old = new ToolCallEntry { ToolCallId = "stale", ToolName = "old.action", AtUtc = At.UtcDateTime };
        var activity = SessionActivityView.Create(true, "Running", Goal(ContinualObjectiveStatus.InProgress) with { LastOutput = "An earlier result" },
            [old], phase: "working", jobStartedUtc: At.AddMinutes(1).UtcDateTime);
        Assert.Equal("No current action reported yet.", activity.Action);
        Assert.Equal("Previous saved result", activity.UpdateLabel);
        Assert.Equal("An earlier result", activity.Update);
        Assert.Null(activity.RecordedAt);
        Assert.Null(activity.ActionStartedAt);
    }

    [Fact]
    public void Plan_content_is_labelled_recorded_and_only_current_job_plan_can_supply_action()
    {
        var plan = new ConversationPlan([new("Inspect files", PlanItemStatus.Completed), new("Repair the Windows build", PlanItemStatus.InProgress)], At.UtcDateTime);
        var current = SessionActivityView.Create(true, "Running", null, [], localStatus: "Working", phase: "working", plan: plan, jobStartedUtc: At.UtcDateTime);
        Assert.Equal("Recorded plan · 1/2 marked done: Repair the Windows build", current.Action);
        Assert.Null(current.PlanStep); // the same plan step is not repeated below the heading
        Assert.Equal(At, current.PlanRecordedAt);
        var previous = SessionActivityView.Create(true, "Running", null, [], phase: "working", plan: plan, jobStartedUtc: At.AddMinutes(1).UtcDateTime);
        Assert.Equal("No current action reported yet.", previous.Action);
        Assert.StartsWith("Saved plan", previous.PlanStep);
        Assert.Null(previous.RetryAt);
    }

    [Fact]
    public void Streaming_commentary_preview_shows_new_tail_and_honest_entry_start_time()
    {
        var assistant = new AssistantEntry { AtUtc = At.UtcDateTime };
        var text = new string('x', 350) + " Now checking the new patch.";
        assistant.AppendText(text);
        var activity = SessionActivityView.Create(true, "Running", null, [assistant], phase: "working");
        Assert.Equal("Receiving agent commentary", activity.Action);
        Assert.Equal("Live commentary", activity.UpdateLabel);
        Assert.Equal("Commentary began", activity.RecordedAtLabel);
        Assert.EndsWith("Now checking the new patch.", activity.Update);
        Assert.StartsWith("…", activity.Update);
        Assert.Equal(At, activity.RecordedAt);
        Assert.Equal(text, assistant.Text); // preview never changes the transcript
    }

    [Fact]
    public void Generic_phase_is_not_repeated_but_real_runtime_wait_stays_visible()
    {
        var empty = SessionActivityView.Create(true, "Running", null, [], localStatus: "Working", phase: "working");
        var view = Render<SessionActivity>(p => p.Add(c => c.Activity, empty));
        Assert.DoesNotContain("WorkingWorking", view.Find(".sess-current__heading").TextContent.Replace(" ", ""));
        var queued = SessionActivityView.Create(true, "Running", null, [], localStatus: "Waiting for the local model slot", phase: "working");
        Assert.Equal("Waiting for the local model slot", queued.Action);
    }
}
