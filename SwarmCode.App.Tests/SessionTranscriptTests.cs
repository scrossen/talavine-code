using Bunit;
using Microsoft.AspNetCore.Components;
using SwarmCode.App.Components.Chat;
using SwarmCode.Hosting;
using SwarmCore.Chat.Components;

namespace SwarmCode.App.Tests;

public sealed class SessionTranscriptTests : BunitContext
{
    private static readonly DateTime TodayUtc = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Local).ToUniversalTime();
    private int _cancelled;
    private int _opened;

    [Fact]
    public void Completed_activity_collapses_without_hiding_the_surrounding_narration()
    {
        TranscriptEntry[] entries = [Assistant("before", "Checking the evidence."),
            .. Enumerable.Range(1, 20).Select(i => Tool($"t{i}")), Assistant("after", "The checks passed.")];
        var view = RenderTranscript(entries);

        var summary = view.Find(".sess-activity__toggle");
        Assert.Equal("false", summary.GetAttribute("aria-expanded"));
        Assert.Contains("20 tool calls", summary.TextContent);
        Assert.Contains("Completed", summary.TextContent);
        Assert.Empty(view.FindComponents<ToolCallCard>());
        Assert.Equal(["before", "after"], VisibleEntries(view));
        Assert.Contains("Checking the evidence.", view.Markup);
        Assert.Contains("The checks passed.", view.Markup);
        var details = view.Find(".sess-activity__details");
        Assert.True(details.HasAttribute("hidden"));
        Assert.Equal(details.Id, summary.GetAttribute("aria-controls"));
    }

    [Fact]
    public void Expanding_preserves_actual_tool_details_and_actions_then_collapses_them()
    {
        var view = RenderTranscript([Tool("read"), Tool("check")]);
        view.Find(".sess-activity__toggle").Click();

        Assert.Equal("true", view.Find(".sess-activity__toggle").GetAttribute("aria-expanded"));
        Assert.Equal(2, view.FindComponents<ToolCallCard>().Count);
        Assert.Equal(["read", "check"], VisibleEntries(view));
        view.FindAll(".tool-call__toggle")[0].Click();
        Assert.Contains("receipt-read", view.Find(".tool-call__body").TextContent);
        Assert.Contains("input-read", view.Find(".tool-call__body").TextContent);
        view.FindAll(".open-file")[0].Click();
        Assert.Equal(1, _opened);

        view.Find(".sess-activity__toggle").Click();
        Assert.Empty(view.FindComponents<ToolCallCard>());
        Assert.True(view.Find(".sess-activity__details").HasAttribute("hidden"));
    }

    [Fact]
    public void Finished_attempts_fold_while_active_work_unknown_outcomes_and_attention_stay_visible()
    {
        var running = Tool("running", ToolCallState.Running);
        var failed = Tool("failed", ToolCallState.Failed);
        failed.Result = "{\"error\":\"Cannot verify the receipt\"}";
        var unknown = Tool("unknown", ToolCallState.Unknown);
        unknown.Result = "Inspect the workspace before retrying.";
        TranscriptEntry[] entries = [Tool("one"), Assistant("progress", "Now checking the next file."),
            Tool("two"), running, Tool("three"), failed, Tool("four"), unknown,
            Tool("five"), new NoticeEntry { Id = "notice", AtUtc = TodayUtc, Kind = NoticeKind.Error, Text = "Needs attention" },
            Tool("six"), new PermissionEntry { Id = "permission", AtUtc = TodayUtc, RequestId = "p", Title = "Approve access", Permissions = ["read"] },
            Tool("seven"), new QuestionEntry { Id = "question", AtUtc = TodayUtc, RequestId = "q", Question = "Which file?" }, Tool("eight")];
        var view = RenderTranscript(entries);

        Assert.Equal(7, view.FindAll(".sess-activity").Count);
        Assert.Equal(["progress", "running", "unknown", "notice", "permission", "question"], VisibleEntries(view));
        Assert.Contains("Reading the next file", view.Find(".tool-call__progress").TextContent);
        Assert.Contains("1 failed attempt", view.FindAll(".sess-activity__toggle")[2].TextContent);
        Assert.Empty(view.FindAll(".tool-call--error"));
        Assert.Contains("Outcome unknown", view.Find(".tool-call--unknown").TextContent);
        Assert.Empty(view.FindAll(".tool-call__body"));
        Assert.Equal("false", view.Find(".tool-call--unknown .tool-call__toggle").GetAttribute("aria-expanded"));
        view.FindAll(".sess-activity__toggle")[2].Click();
        Assert.Equal("false", view.Find(".tool-call--error .tool-call__toggle").GetAttribute("aria-expanded"));
        view.Find(".tool-call--error .tool-call__toggle").Click();
        Assert.Equal("Cannot verify the receipt", view.Find("[role='alert']").TextContent);
        view.Find(".tool-call--unknown .tool-call__toggle").Click();
        Assert.Contains("Inspect the workspace before retrying.", view.Find(".tool-call--unknown").TextContent);
        view.Find(".tool-call__cancel").Click();
        Assert.Equal(1, _cancelled);
        for (var index = 0; index < 7; index++)
            if (index != 2) view.FindAll(".sess-activity__toggle")[index].Click();
        Assert.Equal(entries.Select(x => x.Id), VisibleEntries(view));
    }

    [Fact]
    public void Expanded_group_stays_open_as_active_work_completes_and_new_work_arrives()
    {
        var running = Tool("running", ToolCallState.Running);
        TranscriptEntry[] entries = [Tool("first"), running];
        var view = RenderTranscript(entries);
        view.Find(".sess-activity__toggle").Click();
        Assert.Contains("running", VisibleEntries(view));

        running.State = ToolCallState.Completed;
        view.Render(p => p.Add(x => x.Entries, entries));
        Assert.Equal("true", view.Find(".sess-activity__toggle").GetAttribute("aria-expanded"));
        Assert.Contains("2 tool calls", view.Find(".sess-activity__toggle").TextContent);
        Assert.Equal(["first", "running"], VisibleEntries(view));

        view.Render(p => p.Add(x => x.Entries, [.. entries, Tool("next", ToolCallState.Running)]));
        Assert.Equal(["first", "running", "next"], VisibleEntries(view));
        Assert.Single(view.FindAll(".tool-call__progress"));
    }

    [Fact]
    public void Tool_that_fails_after_running_folds_without_claiming_completion_or_recovery()
    {
        var running = Tool("running", ToolCallState.Running);
        TranscriptEntry[] entries = [Tool("first"), running];
        var view = RenderTranscript(entries);
        running.State = ToolCallState.Failed;
        running.Result = "{\"error\":\"Fresh failure\"}";
        view.Render(p => p.Add(x => x.Entries, entries));

        Assert.Contains("2 tool calls", view.Find(".sess-activity__toggle").TextContent);
        Assert.Contains("1 failed attempt", view.Find(".sess-activity__toggle").TextContent);
        Assert.DoesNotContain("Completed", view.Find(".sess-activity__toggle").TextContent);
        Assert.Empty(VisibleEntries(view));
        view.Find(".sess-activity__toggle").Click();
        Assert.Equal(["first", "running"], VisibleEntries(view));
        Assert.Contains("Failed", view.Find(".tool-call--error").TextContent);
        Assert.Empty(view.FindAll(".tool-call__body"));
        view.Find(".tool-call--error .tool-call__toggle").Click();
        Assert.Equal("Fresh failure", view.Find("[role='alert']").TextContent);
    }

    [Fact]
    public void Success_after_failures_preserves_each_failure_without_inventing_recovery()
    {
        var one = Tool("failed-one", ToolCallState.Failed);
        var two = Tool("failed-two", ToolCallState.Failed);
        var view = RenderTranscript([one, two, Tool("success"), Assistant("after", "Continuing.")]);

        Assert.Single(view.FindAll(".sess-activity"));
        Assert.Contains("3 tool calls", view.Find(".sess-activity__toggle").TextContent);
        Assert.Contains("2 failed attempts", view.Find(".sess-activity__toggle").TextContent);
        Assert.DoesNotContain("Recovered", view.Markup);
        Assert.Equal(["after"], VisibleEntries(view));
        view.Find(".sess-activity__toggle").Click();
        Assert.Equal(["failed-one", "failed-two", "success", "after"], VisibleEntries(view));
        Assert.Equal(2, view.FindAll(".tool-call--error").Count);
        Assert.Equal(ToolCallState.Failed, one.State);
        Assert.Equal(ToolCallState.Failed, two.State);
    }

    [Fact]
    public void Local_day_boundaries_and_untrusted_names_are_preserved()
    {
        var earlier = Tool("earlier");
        var later = new ToolCallEntry { Id = "later", AtUtc = TodayUtc.AddDays(1), ToolCallId = "later", ToolName = "<script>unsafe</script>", State = ToolCallState.Completed };
        var view = RenderTranscript([earlier, later]);

        Assert.Equal(2, view.FindAll(".sess-activity").Count);
        Assert.Equal(2, view.FindAll(".sess-day[role='separator']").Count);
        Assert.Contains("&lt;script&gt;", view.Markup);
        Assert.Empty(view.FindAll("script"));
    }

    private IRenderedComponent<SessionTranscript> RenderTranscript(IReadOnlyList<TranscriptEntry> entries) =>
        Render<SessionTranscript>(p => p.Add(x => x.Entries, entries).Add(x => x.EntryContent, Content));

    // Use the same shared card contract as Session so grouping also proves action and error visibility.
    private RenderFragment<TranscriptEntry> Content => entry => builder =>
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "data-entry-id", entry.Id);
        if (entry is ToolCallEntry tool)
        {
            builder.OpenComponent<ToolCallCard>(2);
            builder.AddAttribute(3, "ToolName", tool.ToolName);
            builder.AddAttribute(4, "InputJson", tool.InputJson);
            builder.AddAttribute(5, "Result", tool.Result);
            builder.AddAttribute(6, "State", tool.State switch {
                ToolCallState.Completed => ToolCallCardState.Completed, ToolCallState.Failed => ToolCallCardState.Failed,
                ToolCallState.Unknown => ToolCallCardState.Unknown, _ => ToolCallCardState.Running });
            builder.AddAttribute(7, "AutoExpandErrors", false);
            builder.AddAttribute(8, "ProgressStatus", tool.State == ToolCallState.Running ? "Reading the next file" : null);
            builder.AddAttribute(9, "OnCancel", EventCallback.Factory.Create(this, () => _cancelled++));
            builder.AddAttribute(10, "HeaderActions", (RenderFragment)(action => {
                action.OpenElement(0, "button");
                action.AddAttribute(1, "class", "open-file");
                action.AddAttribute(2, "onclick", EventCallback.Factory.Create(this, () => _opened++));
                action.AddContent(3, "Open file");
                action.CloseElement();
            }));
            builder.CloseComponent();
        }
        else builder.AddContent(11, entry switch {
            AssistantEntry assistant => assistant.Text, NoticeEntry notice => notice.Text,
            PermissionEntry permission => permission.Title, QuestionEntry question => question.Question, _ => entry.Id });
        builder.CloseElement();
    };

    private static string[] VisibleEntries(IRenderedComponent<SessionTranscript> view) =>
        view.FindAll("[data-entry-id]").Select(x => x.GetAttribute("data-entry-id")!).ToArray();

    private static ToolCallEntry Tool(string id, ToolCallState state = ToolCallState.Completed) =>
        new() { Id = id, AtUtc = TodayUtc, ToolCallId = id, ToolName = "fs.read_file", State = state,
            InputJson = "{\"path\":\"input-" + id + "\"}", Result = "receipt-" + id };

    private static AssistantEntry Assistant(string id, string text)
    {
        var entry = new AssistantEntry { Id = id, AtUtc = TodayUtc, IsStreaming = false };
        entry.AppendText(text);
        return entry;
    }
}
