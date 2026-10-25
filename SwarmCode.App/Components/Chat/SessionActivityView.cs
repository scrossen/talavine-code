using SwarmCode.Hosting;
using SwarmCore.Agent.Continual;
using SwarmCore.Coding.Planning;

namespace SwarmCode.App.Components.Chat;

/// <summary>Read-only presentation of recorded state. A running goal loop can be waiting or need inspection.</summary>
public sealed record SessionActivityView(string State, bool Busy, string Action, string Update,
    DateTimeOffset? RecordedAt, string? Reason, string? Diagnostics, DateTimeOffset? RetryAt)
{
    public string UpdateLabel { get; init; } = "Last recorded commentary";
    public string RecordedAtLabel { get; init; } = "Entry recorded";
    public DateTimeOffset? ActionStartedAt { get; init; }
    public string? PlanStep { get; init; }
    public DateTimeOffset? PlanRecordedAt { get; init; }

    public static SessionActivityView Create(bool running, string jobState, ContinualObjectiveSnapshot? goal,
        IReadOnlyList<TranscriptEntry> entries, string? localStatus = null, string? phase = null, bool useGoalState = true,
        ConversationPlan? plan = null, DateTime? jobStartedUtc = null)
    {
        if (!useGoalState) goal = null;
        var state = goal?.Status switch
        {
            ContinualObjectiveStatus.Paused => "Paused",
            ContinualObjectiveStatus.WaitingForInput => "Needs your input",
            ContinualObjectiveStatus.RetryScheduled => "Waiting to retry",
            ContinualObjectiveStatus.AttentionRequired => goal.NextAttemptAtUtc is not null ? "Waiting to retry" : "Needs attention",
            ContinualObjectiveStatus.Uncertain or ContinualObjectiveStatus.Failed or ContinualObjectiveStatus.DeliveryBlocked => "Needs attention",
            ContinualObjectiveStatus.VerificationPending => running ? "Checking results" : "Verification pending",
            ContinualObjectiveStatus.Completed => "Verified complete",
            ContinualObjectiveStatus.Cancelled => "Cancelled",
            ContinualObjectiveStatus.Idle => "Not running",
            ContinualObjectiveStatus.InProgress => running ? "Working" : "Not running",
            _ => running ? "Working" : jobState switch { "Failed" or "Interrupted" or "Incomplete" => "Needs attention", "Completed" => "Answer ready", _ => "Ready" },
        };
        var busy = running && state is "Working" or "Checking results";
        // Accepted continuations can arrive while a tool batch is still running. They do not start another job.
        var lastUser = entries.Select((entry, index) => (entry, index)).LastOrDefault(x => x.entry is UserEntry { IsContinuation: false }).index;
        var currentEntries = entries.Skip(lastUser).Where(e => jobStartedUtc is null || e.AtUtc >= jobStartedUtc.Value).ToArray();
        var pending = currentEntries.LastOrDefault(e => e is PermissionEntry { IsPending: true } or QuestionEntry { IsPending: true });
        if (busy && pending is not null) { state = "Needs your input"; busy = false; }
        var tool = currentEntries.OfType<ToolCallEntry>().LastOrDefault(e => e.State == ToolCallState.Running);
        var latest = currentEntries.OfType<AssistantEntry>().LastOrDefault(e => !string.IsNullOrWhiteSpace(e.Text));
        var liveCommentary = busy && latest?.IsStreaming == true;
        var currentPlan = plan?.Current;
        var planIsCurrent = currentPlan is not null && (jobStartedUtc is null || plan!.UpdatedUtc >= jobStartedUtc.Value);
        var runtimePhase = UsefulPhase(localStatus, state);
        var runPhase = UsefulPhase(phase, state);
        var planSuppliesAction = busy && tool is null && runtimePhase is null && !liveCommentary && runPhase is null && planIsCurrent;
        var action = busy ? tool is not null ? "Current action: " + tool.ToolName
            : runtimePhase ?? (liveCommentary ? "Receiving agent commentary" : null)
                ?? runPhase ?? (planIsCurrent ? $"Recorded plan · {plan!.CompletedCount}/{plan.Total} marked done: {Brief(currentPlan!.Content)}" : "No current action reported yet.")
            : state switch
            {
                "Paused" => "Work is paused. Inbox messages remain saved.",
                "Needs your input" => "A response is needed before work can continue.",
                "Waiting to retry" => "The saved work is waiting for its next attempt.",
                "Needs attention" => "Inspect the saved result before continuing.",
                "Not running" or "Verification pending" => "Saved work is not currently running.",
                _ => "No action is running.",
            };
        var update = latest?.Text ?? goal?.LastOutput;
        DateTimeOffset? at = latest is not null ? new DateTimeOffset(DateTime.SpecifyKind(latest.AtUtc, DateTimeKind.Utc)) : null;
        var failure = goal?.LastFailure;
        if (failure is null && state == "Needs attention") failure = entries.OfType<NoticeEntry>().LastOrDefault(e => e.Kind == NoticeKind.Error)?.Text;
        var reason = goal?.Status == ContinualObjectiveStatus.Uncertain
            ? "An earlier action has an uncertain outcome. Its saved result needs inspection before work can continue."
            : state is "Needs attention" or "Needs your input" ? Brief(failure ?? (pending switch
        { PermissionEntry p => p.Title, QuestionEntry q => q.Question, _ => "Saved actions need inspection before work can continue." })) : null;
        return new(state, busy, action, string.IsNullOrWhiteSpace(update) ? "No progress update recorded yet." : liveCommentary ? Tail(update) : Brief(update), at,
            reason, failure, state == "Waiting to retry" ? goal?.NextAttemptAtUtc : null)
        {
            UpdateLabel = latest is null ? "Previous saved result" : liveCommentary ? "Live commentary" : "Last recorded commentary",
            RecordedAtLabel = liveCommentary ? "Commentary began" : "Entry recorded",
            ActionStartedAt = busy && tool is not null ? Utc(tool.AtUtc) : null,
            PlanStep = currentPlan is not null && !planSuppliesAction ? $"Saved plan · {plan!.CompletedCount}/{plan.Total} marked done: {Brief(currentPlan.Content)}" : null,
            PlanRecordedAt = currentPlan is not null && plan!.UpdatedUtc != default ? Utc(plan.UpdatedUtc) : null,
        };
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static string? UsefulPhase(string? value, string state)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) || text.Equals(state, StringComparison.OrdinalIgnoreCase)
            || text.Equals("working", StringComparison.OrdinalIgnoreCase) || text.Equals("running", StringComparison.OrdinalIgnoreCase)
            ? null : text;
    }

    private static string Tail(string value) => value.Length <= 280 ? value : "…" + value[^280..];

    private static string Brief(string value)
    {
        var text = value.Trim();
        return text.Length <= 280 ? text : text[..280] + "…";
    }
}
