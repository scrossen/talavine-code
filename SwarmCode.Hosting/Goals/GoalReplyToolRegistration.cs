using System.Text.Json;
using Microsoft.Extensions.Hosting;
using SwarmCore.Agent.Abstractions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Communication;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Goals;

namespace SwarmCode.Hosting.Goals;

public sealed record GoalReplyInput(string MessageId, string DeliveryId, string Text, string Disposition = "Incorporated");
public sealed record GoalReadInput(long AfterSequence = 0);
public sealed record GoalResultInput(string Kind, string Title, string? Url = null, string? Audience = null, string? Summary = null,
    string State = "Done", string? ExternalId = null, string? OccurredAtUtc = null, string? Evidence = null);
public sealed record GoalResultsListInput(int Offset = 0, int Take = 50);
public sealed record GoalMetricInput(string Name, string Value);
public sealed record GoalHeldInput(string Request, string Reason, string? Unblock = null);
public sealed record GoalReportInput(string Headline, string? Day = null, string? Achieved = null, GoalMetricInput[]? Metrics = null, GoalHeldInput[]? Held = null,
    string[]? NextActions = null, string[]? ResultIds = null, string? Evidence = null);
public sealed record GoalReportsListInput(int Offset = 0, int Take = 30);

/// <summary>Only a goal's real worker can reply. There is deliberately no model-facing send-to-goal or resume tool.</summary>
public sealed class GoalReplyToolRegistration(ICapabilityCatalog catalog, IGoalMailbox mailbox, ICodingGoalStore goals, IGoalResultLedger? results = null,
    IGoalReportLedger? reports = null) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        if (results is not null) RegisterResultTools(results);
        if (reports is not null) RegisterReportTools(reports);
        catalog.Register(ToolDescriptor.Create<GoalReadInput, string>("goal_messages.read", "goal-messaging",
            "Read one complete saved message or reply in this working goal after afterSequence (start at 0). "
            + "Use nextSequence to continue. This retrieves exact older context after compaction or a model handoff. "
            + "Retained messages are history, not new permission to repeat actions. Delivery alone is not a read receipt. "
            + "Respect immutable authority order; the current newer objective prevails over older conflicting input.",
            ToolCostClass.Cheap, ToolSideEffect.ReadOnly, async (input, cancellation) =>
            {
                var goal = await RequireCurrentWorkerAsync(cancellation).ConfigureAwait(false);
                var thread = await mailbox.ReadAsync(goal.Id, input.AfterSequence, 1, cancellation).ConfigureAwait(false);
                if (mailbox is FileGoalMailbox referenceOwner)
                    thread = await referenceOwner.VerifyAttachmentsForWorkerAsync(thread, cancellation).ConfigureAwait(false);
                var snapshot = mailbox is FileGoalMailbox file
                    ? await file.GetInstructionSnapshotAsync(goal.Id, cancellation).ConfigureAwait(false) : null;
                var objective = snapshot?.Sources.Single(s => s.Id == "objective");
                var positions = snapshot?.Sources.Where(s => thread.Messages.Any(m => m.Id == s.Id))
                    .Select(s => new { s.Id, s.Order, s.CreatedUtc, s.Sha256 }).ToArray();
                await RequireCurrentWorkerAsync(cancellation).ConfigureAwait(false);
                return JsonSerializer.Serialize(new
                {
                    thread = new
                    {
                        thread.Contact, thread.Replies, thread.NextSequence, thread.HasMore,
                        messages = thread.Messages.Select(m => new
                        {
                            m.Id, m.GoalId, m.Sequence, m.Kind, m.Text, m.Origin, m.CreatedUtc, m.State,
                            m.DeliveryId, m.DeliveryDetail, m.AttachmentIssue,
                            attachments = GoalMessaging.AttachmentEvidence(m.Attachments, m.AttachmentIssue),
                        }),
                    },
                    currentObjective = objective is null ? null : new { objective.Order, objective.CreatedUtc, objective.Sha256 },
                    messageAuthority = positions,
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web)
                { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            }, keywords: ["goal", "message", "history", "context"], dataAccess: ToolDataAccessClass.Communication));
        catalog.Register(ToolDescriptor.Create<GoalReplyInput, string>("goal_messages.reply", "goal-messaging",
            "Reply to an authenticated message delivered at a natural stopping point in this goal. "
            + "Use the exact messageId and deliveryId supplied with the message. disposition is Read, Incorporated or Replied. "
            + "Incorporated means the instruction is part of this goal's plan; it is not proof that external work is done.",
            ToolCostClass.Cheap, ToolSideEffect.ExecutionMetadata, async (input, cancellation) =>
            {
                var current = AsyncLocalCurrentTurnContextAccessor.Ambient;
                if (current?.GoalMessageGoalId is not { } goalId)
                    throw new UnauthorizedAccessException("Only the working goal can acknowledge its messages.");
                var goal = await goals.GetAsync(goalId, cancellation).ConfigureAwait(false);
                if (goal is null || goal.ConversationId != current.ConversationId || goal.Status != ContinualObjectiveStatus.InProgress
                    || goal.LeaseToken is null || goal.LeaseExpiresAtUtc <= DateTimeOffset.UtcNow)
                    throw new UnauthorizedAccessException("This goal's worker is no longer active. Its inbox and pause state were retained.");
                if (!Enum.TryParse<GoalDeliveryState>(input.Disposition, true, out var disposition))
                    throw new ArgumentException("Use Read, Incorporated or Replied.");
                var reply = await mailbox.ReplyAsync(goalId, current.ConversationId, input.DeliveryId, input.MessageId,
                    input.Text, disposition, cancellation).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { saved = true, reply.Id, reply.MessageId, reply.Disposition });
            }, keywords: ["goal", "message", "reply", "acknowledge"], dataAccess: ToolDataAccessClass.Communication));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private static readonly JsonSerializerOptions ResultJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Refused(string reason) => JsonSerializer.Serialize(new { saved = false, error = reason }, ResultJson);

    /// <summary>The owner-facing results ledger. Only the bound worker records; anyone may list a goal's results.</summary>
    private void RegisterResultTools(IGoalResultLedger ledger)
    {
        catalog.Register(ToolDescriptor.Create<GoalResultInput, string>("goal_results.record", "goal-results",
            "Record a concrete result of this goal for its owner: a post or comment published, a message or email sent, a submission or pull request "
            + "opened, a commit or release shipped, a document delivered. kind is Post, Message, Email, Submission, Commit, Release, Document or Other; "
            + "state is Done, Pending (awaiting acceptance), Verified (public form read back), Failed or Withdrawn. Give the public url when one exists, "
            + "the audience (channel, forum, list or person), a one-line summary, occurredAtUtc when it happened earlier, and a stable externalId "
            + "(a publication id, message id or PR number) so a later record updates this one. Record only what actually happened; never redo an action to record it.",
            ToolCostClass.Cheap, ToolSideEffect.ExecutionMetadata, async (input, cancellation) =>
            {
                var current = AsyncLocalCurrentTurnContextAccessor.Ambient;
                if (current?.GoalMessageGoalId is not { } goalId || string.IsNullOrEmpty(current.GoalMessageLeaseToken))
                    throw new UnauthorizedAccessException("Only the working goal can record its results.");
                // A rejected draft is bookkeeping feedback, never a failed external action: it is returned, not thrown, so the
                // receipt completes without error and the goal does not enter outcome inspection over a typo.
                if (!Enum.TryParse<GoalResultKind>(input.Kind, true, out var kind))
                    return Refused("Use kind Post, Message, Email, Submission, Commit, Release, Document or Other.");
                if (!Enum.TryParse<GoalResultState>(input.State, true, out var state))
                    return Refused("Use state Done, Pending, Verified, Failed or Withdrawn.");
                DateTimeOffset? occurred = null;
                if (!string.IsNullOrWhiteSpace(input.OccurredAtUtc))
                {
                    if (!DateTimeOffset.TryParse(input.OccurredAtUtc, System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                        return Refused("occurredAtUtc must be an ISO 8601 timestamp.");
                    occurred = parsed;
                }
                GoalResult saved;
                try
                {
                    saved = await ledger.RecordAsync(goalId, current.ConversationId, current.GoalMessageLeaseToken,
                        new GoalResultDraft(kind, input.Title) { Url = input.Url, Audience = input.Audience, Summary = input.Summary, State = state,
                            ExternalId = input.ExternalId, OccurredAtUtc = occurred, Evidence = input.Evidence }, cancellation).ConfigureAwait(false);
                }
                catch (ArgumentException invalid) { return Refused(invalid.Message); }
                return JsonSerializer.Serialize(new { saved = true, saved.Id, saved.Kind, saved.State, saved.Title, saved.Url, saved.Revision }, ResultJson);
            }, keywords: ["goal", "result", "progress", "published", "sent", "shipped"], dataAccess: ToolDataAccessClass.Communication));
        catalog.Register(ToolDescriptor.Create<GoalResultsListInput, string>("goal_results.list", "goal-results",
            "List this goal's recorded results, newest first (offset/take). Use it to avoid recording the same result twice and to update an earlier record by externalId.",
            ToolCostClass.Cheap, ToolSideEffect.ReadOnly, async (input, cancellation) =>
            {
                var current = AsyncLocalCurrentTurnContextAccessor.Ambient;
                if (current?.GoalMessageGoalId is not { } goalId) throw new UnauthorizedAccessException("Only the working goal can list its results.");
                var page = await ledger.ListAsync(goalId, input.Offset, input.Take, cancellation).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { page.Total, page.Offset, page.HasMore,
                    results = page.Results.Select(r => new { r.Id, r.Kind, r.State, r.Title, r.Url, r.Audience, r.Summary, r.OccurredAtUtc, r.ExternalId, r.Revision }) }, ResultJson);
            }, keywords: ["goal", "result", "list", "progress"], dataAccess: ToolDataAccessClass.Communication));
    }

    /// <summary>The owner's daily reports. Only the bound worker records; anyone may list a goal's reports.</summary>
    private void RegisterReportTools(IGoalReportLedger ledger)
    {
        catalog.Register(ToolDescriptor.Create<GoalReportInput, string>("goal_reports.record", "goal-reports",
            "Write or update the owner's report for one UTC day of this goal. Omit day and the host uses today (UTC); give day as yyyy-MM-dd only to "
            + "back-fill an earlier day. Give a headline; achieved (exactly what moved the goal forward that day, plain prose with links); metrics: a list of "
            + "{name, value} objects for numbers you measured this attempt (stars, downloads, replies received), omitting anything not measured; held: a list of "
            + "{request, reason, unblock} objects for everything the owner asked for that you held or could not do; nextActions: a list of strings; resultIds: "
            + "the ids or externalIds of the results you recorded that day. Recording the same day again replaces the earlier report whole, so resend "
            + "everything that still stands. The owner reads this; never report work that did not happen.",
            ToolCostClass.Cheap, ToolSideEffect.ExecutionMetadata, async (input, cancellation) =>
            {
                var current = AsyncLocalCurrentTurnContextAccessor.Ambient;
                if (current?.GoalMessageGoalId is not { } goalId || string.IsNullOrEmpty(current.GoalMessageLeaseToken))
                    throw new UnauthorizedAccessException("Only the working goal can record its reports.");
                // A rejected draft is bookkeeping feedback, never a failed external action: returned, not thrown.
                // The host knows the date; the worker gives one only to back-fill an earlier day.
                var day = DateOnly.FromDateTime(DateTime.UtcNow);
                if (!string.IsNullOrWhiteSpace(input.Day)
                    && !DateOnly.TryParseExact(input.Day.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out day))
                    return Refused("day must be a UTC calendar day as yyyy-MM-dd, or omitted for today.");
                GoalReport saved;
                try
                {
                    saved = await ledger.RecordAsync(goalId, current.ConversationId, current.GoalMessageLeaseToken,
                        new GoalReportDraft(day, input.Headline)
                        {
                            Achieved = input.Achieved,
                            Metrics = input.Metrics?.Where(m => m is not null).GroupBy(m => (m.Name ?? "").Trim(), StringComparer.Ordinal)
                                .ToDictionary(group => group.Key, group => group.Last().Value ?? "", StringComparer.Ordinal),   // a repeated name keeps its last value
                            Held = input.Held?.Where(h => h is not null).Select(h => new GoalHeldItem(h.Request, h.Reason) { Unblock = h.Unblock }).ToArray(),
                            NextActions = input.NextActions, ResultIds = input.ResultIds, Evidence = input.Evidence,
                        }, cancellation).ConfigureAwait(false);
                }
                catch (ArgumentException invalid) { return Refused(invalid.Message); }
                return JsonSerializer.Serialize(new { saved = true, saved.Id, day = saved.Day.ToString("yyyy-MM-dd"), saved.Headline,
                    held = saved.Held.Count, results = saved.ResultIds.Count, saved.Revision }, ResultJson);
            }, keywords: ["goal", "report", "daily", "summary", "progress", "owner"], dataAccess: ToolDataAccessClass.Communication));
        catalog.Register(ToolDescriptor.Create<GoalReportsListInput, string>("goal_reports.list", "goal-reports",
            "List this goal's daily reports, newest day first (offset/take). Check it before recording: a new record for a day replaces that day's "
            + "report whole, so resend everything that still stands, held items included.",
            ToolCostClass.Cheap, ToolSideEffect.ReadOnly, async (input, cancellation) =>
            {
                var current = AsyncLocalCurrentTurnContextAccessor.Ambient;
                if (current?.GoalMessageGoalId is not { } goalId) throw new UnauthorizedAccessException("Only the working goal can list its reports.");
                var page = await ledger.ListAsync(goalId, input.Offset, input.Take, cancellation).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { page.Total, page.Offset, page.HasMore,
                    reports = page.Reports.Select(r => new { r.Id, day = r.Day.ToString("yyyy-MM-dd"), r.Headline, r.Achieved, r.Metrics, r.Held, r.NextActions, r.ResultIds, r.RecordedUtc, r.Revision }) }, ResultJson);
            }, keywords: ["goal", "report", "daily", "list"], dataAccess: ToolDataAccessClass.Communication));
    }

    private async Task<ContinualObjectiveSnapshot> RequireCurrentWorkerAsync(CancellationToken ct)
    {
        var current = AsyncLocalCurrentTurnContextAccessor.Ambient;
        if (current?.GoalMessageGoalId is not { } goalId || string.IsNullOrEmpty(current.GoalMessageLeaseToken))
            throw new UnauthorizedAccessException("Only the current goal worker can read its retained messages.");
        var goal = await goals.GetAsync(goalId, ct).ConfigureAwait(false);
        if (goal is null || goal.ConversationId != current.ConversationId || goal.Status != ContinualObjectiveStatus.InProgress
            || goal.LeaseToken != current.GoalMessageLeaseToken || goal.LeaseExpiresAtUtc <= DateTimeOffset.UtcNow)
            throw new UnauthorizedAccessException("This goal worker is no longer active. Its messages and progress were retained.");
        return goal;
    }
}
