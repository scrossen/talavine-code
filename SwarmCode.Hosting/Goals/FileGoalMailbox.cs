using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SwarmCore.Agent.Communication;
using SwarmCore.Agent.Continual;
using SwarmCore.Coding.Goals;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding.Files;

namespace SwarmCode.Hosting.Goals;

/// <summary>
/// Profile-owned inbox. Every mutation reloads while holding the owning goal inventory's OS lock.
/// It owns only its own contacts/messages/replies; it never replaces a goal, revokes its lease or resumes work.
/// </summary>
public sealed class FileGoalMailbox(SwarmCodeDataPaths paths, FileCodingGoalStore goals, TimeProvider? time = null)
    : IGoalMailbox, IGoalWorkerReplySink, IGoalWorkerDeliverySink, IGoalWorkerHeldInputReader, IGoalWorkerAuthority, IGoalAttachmentMailbox
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string AttachmentUnavailable = "A file is missing, changed, linked, outside this conversation or exceeds current attachment limits. Reattach the original file and send a new message; the saved text remains available in Agent inbox.";
    private static readonly Regex Handle = new("^[a-z][a-z0-9_-]{0,39}$", RegexOptions.CultureInvariant);
    private readonly string _path = Path.Combine(paths.RootDirectory, "goals", "messages.json");
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public Task<GoalMailboxIdentity> GetIdentityAsync(CancellationToken ct = default)
        => Access((document, _) => new GoalMailboxIdentity(document.InstanceId, "Talavine Code"), true, ct);

    public Task<IReadOnlyList<GoalContact>> ListAsync(CancellationToken ct = default)
        => Access<IReadOnlyList<GoalContact>>((document, rows) => rows
            .Where(row => row.PartitionKey == CodingGoalOrchestrator.Partition && row.Mission is null)
            .Select(row => Contact(document, row)).OrderBy(contact => contact.Name, StringComparer.OrdinalIgnoreCase).ToArray(), false, ct);

    public Task<GoalContact> RenameAsync(Guid goalId, string name, string handle, long expectedRevision,
        CancellationToken ct = default)
    {
        name = name.Trim(); handle = handle.Trim().TrimStart('@').ToLowerInvariant();
        name = new CredentialScrubbingContinualObjectiveTextSanitizer().Sanitize(name, int.MaxValue) ?? "";
        if (name.Length is < 1 or > 100 || name.Any(char.IsControl) || !Handle.IsMatch(handle))
            throw new ArgumentException("Use a name of 1–100 characters and a handle beginning with a letter, followed by up to 39 letters, digits, underscores or hyphens.");
        return Access((document, rows) =>
        {
            var row = RequireGoal(rows, goalId);
            var old = Contact(document, row);
            if (old.Revision != expectedRevision) throw new InvalidOperationException("The goal name changed elsewhere. Reload it before renaming.");
            if (rows.Where(other => other.Id != goalId && other.PartitionKey == CodingGoalOrchestrator.Partition && other.Mission is null)
                .Any(other => string.Equals(Contact(document, other).Handle, handle, StringComparison.OrdinalIgnoreCase))
                || document.RetiredHandles.Any(pair => pair.Key == handle && pair.Value != goalId))
                throw new InvalidOperationException("Another goal already uses that handle.");
            if (old.Handle != handle) document.RetiredHandles[old.Handle] = goalId;
            var updated = new ContactName(goalId, name, handle, old.Revision + 1);
            document.Names.RemoveAll(value => value.GoalId == goalId);
            document.Names.Add(updated);
            return Contact(document, row);
        }, true, ct);
    }

    public Task<GoalMessageThread> ReadAsync(Guid goalId, long afterSequence = 0, int take = 200, CancellationToken ct = default)
        => Access((document, rows) =>
        {
            var row = RequireGoal(rows, goalId);
            if (afterSequence < 0 || take is < 1 or > 500) throw new ArgumentException("Invalid message cursor or page size.");
            RecordHeldStatus(document, row);
            var sequences = document.Messages.Where(message => message.GoalId == goalId && message.Sequence > afterSequence).Select(message => message.Sequence)
                .Concat(document.Replies.Where(reply => reply.GoalId == goalId && reply.Sequence > afterSequence).Select(reply => reply.Sequence))
                .Concat(document.StatusNotices.Where(reply => reply.GoalId == goalId && reply.Sequence > afterSequence).Select(reply => reply.Sequence))
                .Order().Take(take + 1).ToArray();
            var page = sequences.Take(take).ToHashSet();
            return new GoalMessageThread(Contact(document, row),
                document.Messages.Where(message => message.GoalId == goalId && page.Contains(message.Sequence)).OrderBy(message => message.Sequence).ToArray(),
                document.Replies.Concat(document.StatusNotices).Where(reply => reply.GoalId == goalId && page.Contains(reply.Sequence)).OrderBy(reply => reply.Sequence).ToArray(),
                page.Count == 0 ? afterSequence : page.Max(), sequences.Length > take);
        }, true, ct, onlyIfSequenceChanged: true);

    public async Task<IReadOnlyList<GoalMessageAttachment>> PrepareAttachmentsAsync(Guid goalId,
        IReadOnlyList<CodingAttachment> files, CancellationToken ct = default)
    {
        var conversation = await Access((_, rows) => RequireGoal(rows, goalId).ConversationId, false, ct).ConfigureAwait(false);
        return await new GoalAttachmentReferences(paths).PrepareAsync(conversation, files, ct).ConfigureAwait(false);
    }

    internal async Task<GoalMessageThread> VerifyAttachmentsForWorkerAsync(GoalMessageThread thread, CancellationToken ct)
    {
        var references = new GoalAttachmentReferences(paths);
        var messages = new List<GoalInboxMessage>();
        foreach (var message in thread.Messages)
        {
            var issue = message.AttachmentIssue;
            if (issue is null && message.Attachments is { Count: > 0 })
            {
                try { await references.VerifyAsync(message.Attachments, thread.Contact.ConversationId, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
                { issue = AttachmentUnavailable; }
            }
            messages.Add(message with { AttachmentIssue = issue });
        }
        return thread with { Messages = messages.ToArray() };
    }

    public async Task<GoalInboxMessage> SendAsync(Guid goalId, GoalMessageCommand message, CancellationToken ct = default)
    {
        if (message is not null && string.IsNullOrWhiteSpace(message.Text) && message.Attachments is { Count: > 0 })
            message = message with { Text = GoalMessaging.AttachmentOnlyText };
        Validate(message!);
        // Capture immutable arrays before awaiting; callers cannot edit the command during verification.
        message = message with { Attachments = message.Attachments is { Count: > 0 } files ? files.ToArray() : null };
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { goalId, message }, Json))).ToLowerInvariant();
        message = message with { Text = new CredentialScrubbingContinualObjectiveTextSanitizer().Sanitize(message.Text, int.MaxValue)! };
        GoalInboxMessage? Existing(Document document)
        {
            if (document.Commands.FirstOrDefault(command => command.Key == message.IdempotencyKey) is not { } prior) return null;
            if (prior.GoalId != goalId || JsonSerializer.Serialize(prior.Command, Json) != JsonSerializer.Serialize(message, Json) || prior.Fingerprint != fingerprint)
                throw new InvalidOperationException("That message ID already belongs to a different recipient or message. The original message was retained.");
            return document.Messages.Single(value => value.Id == prior.MessageId);
        }
        var baseline = await Access((document, rows) =>
        {
            var goal = RequireGoal(rows, goalId);
            var existing = Existing(document);
            if (existing is null && ContinualObjectiveStateMachine.IsTerminal(goal.Status)) throw new GoalMailboxClosedException();
            return (goal.ConversationId, existing);
        }, false, ct).ConfigureAwait(false);
        // A confirmed idempotent receipt remains valid even if its file was subsequently removed.
        if (baseline.existing is not null) return baseline.existing;
        var references = new GoalAttachmentReferences(paths);
        GoalAttachmentReferences.Verified[] verified;
        try { verified = await references.VerifyAsync(message.Attachments, baseline.ConversationId, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { throw new GoalMessageRejectedException("The attachment was not saved. Reattach regular, unchanged files in this goal's Code conversation and check Settings → Attachments.", ex); }
        return await Access((document, rows) =>
        {
            var goal = RequireGoal(rows, goalId);
            if (Existing(document) is { } existing) return existing;
            if (ContinualObjectiveStateMachine.IsTerminal(goal.Status)) throw new GoalMailboxClosedException();
            if (goal.ConversationId != baseline.ConversationId) throw new InvalidOperationException("The goal recipient changed. Reload before sending.");
            try { references.Recheck(verified, goal.ConversationId); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            { throw new GoalMessageRejectedException("The attachment changed before admission. Reattach the original file; your message was not saved.", ex); }
            var saved = new GoalInboxMessage(Guid.NewGuid().ToString("N"), goalId, ++document.Sequence,
                message.Kind, message.Text, message.Origin, _time.GetUtcNow(), GoalDeliveryState.Received) { Attachments = message.Attachments };
            document.Messages.Add(saved);
            // Full references must fit both normal delivery and retained-input inspection, without truncation.
            var probe = saved with { DeliveryId = new string('d', 32) };
            if (FormatContext(document, goal, probe.DeliveryId, [probe], false).Length > 16000
                || FormatContext(document, goal, probe.DeliveryId, [probe], true).Length > 16000
                || probe.Attachments is { Count: > 0 } && FormatContext(document, goal, probe.DeliveryId,
                    [probe with { AttachmentIssue = AttachmentUnavailable }], true).Length > 16000)
                throw new GoalMessageRejectedException("The complete message and file references exceed the worker's context budget. Send fewer attachments in separate messages; your draft was kept.");
            document.Commands.Add(new SavedCommand(message.IdempotencyKey, goalId, saved.Id, message, fingerprint));
            RecordHeldStatus(document, goal);
            return saved;
        }, true, ct).ConfigureAwait(false);
    }

    public Task<GoalDelivery?> BeginDeliveryAsync(Guid goalId, string conversationId, string workerId,
        int maxCharacters = 16000, CancellationToken ct = default)
    {
        var current = AsyncLocalCurrentTurnContextAccessor.Ambient;
        if (current?.GoalMessageGoalId != goalId || current.ConversationId != conversationId
            || string.IsNullOrEmpty(current.GoalMessageLeaseToken))
            throw new UnauthorizedAccessException("Only the current goal worker can claim its saved messages.");
        return BeginDeliveryForWorkerAsync(goalId, conversationId, workerId, current.GoalMessageLeaseToken, maxCharacters, ct);
    }

    public async Task<GoalDelivery?> BeginDeliveryForWorkerAsync(Guid goalId, string conversationId, string workerId,
        string expectedLeaseToken,
        int maxCharacters = 16000, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workerId) || workerId.Length > 512 || maxCharacters < 1000
            || string.IsNullOrEmpty(expectedLeaseToken))
            throw new ArgumentException("An exact worker identity and adequate context budget are required.");
        // Empty-inbox polling never deserializes the large physical tool-receipt inventory.
        if (!ReadDocument(_path).Messages.Any(message => message.GoalId == goalId && message.State == GoalDeliveryState.Received)) return null;
        var pending = await Access((document, rows) =>
        {
            var goal = RequireGoal(rows, goalId);
            if (goal.ConversationId != conversationId) throw new UnauthorizedAccessException("The message belongs to another goal conversation.");
            if (goal.Status != ContinualObjectiveStatus.InProgress || goal.LeaseToken != expectedLeaseToken
                || goal.LeaseExpiresAtUtc <= _time.GetUtcNow()) return Array.Empty<GoalInboxMessage>();
            var candidates = new List<GoalInboxMessage>();
            foreach (var message in document.Messages.Where(m => m.GoalId == goalId && m.State == GoalDeliveryState.Received).OrderBy(m => m.Sequence))
            {
                var worstCase = message.Attachments is { Count: > 0 } ? message with { AttachmentIssue = AttachmentUnavailable } : message;
                if (Math.Max(FormatContext(document, goal, new string('d', 32), [.. candidates, message], false).Length,
                    FormatContext(document, goal, new string('d', 32), [.. candidates, worstCase], false).Length) > maxCharacters)
                {
                    if (candidates.Count == 0)
                    {
                        const string detail = "Delivery held: this complete message exceeds this worker's context budget. Open Agent inbox and send a smaller message; the saved text was kept.";
                        if (message.DeliveryDetail != detail)
                            document.Messages[document.Messages.FindIndex(m => m.Id == message.Id)] = message with { Sequence = ++document.Sequence, DeliveryDetail = detail };
                    }
                    break;
                }
                candidates.Add(message);
            }
            return candidates.ToArray();
        }, true, ct).ConfigureAwait(false);
        if (pending.Length == 0) return null;
        var references = new GoalAttachmentReferences(paths);
        var verified = new Dictionary<string, GoalAttachmentReferences.Verified[]>();
        var failures = new Dictionary<string, string>();
        foreach (var message in pending)
        {
            try { verified[message.Id] = await references.VerifyAsync(message.Attachments, conversationId, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            { failures[message.Id] = AttachmentUnavailable; }
        }
        return await Access<GoalDelivery?>((document, rows) =>
        {
            var goal = RequireGoal(rows, goalId);
            if (goal.ConversationId != conversationId) throw new UnauthorizedAccessException("The message belongs to another goal conversation.");
            if (goal.Status != ContinualObjectiveStatus.InProgress || goal.LeaseToken != expectedLeaseToken
                || goal.LeaseExpiresAtUtc <= _time.GetUtcNow()) return null;
            var id = Guid.NewGuid().ToString("N");
            var batch = new List<GoalInboxMessage>();
            foreach (var message in document.Messages.Where(value => value.GoalId == goalId && value.State == GoalDeliveryState.Received).OrderBy(value => value.Sequence))
            {
                // Only the exact immutable sources verified outside the inventory lock may be claimed.
                var baseline = pending.FirstOrDefault(value => value.Id == message.Id);
                if (baseline is null || JsonSerializer.Serialize(baseline, Json) != JsonSerializer.Serialize(message, Json)) break;
                string? failure = failures.GetValueOrDefault(message.Id);
                if (failure is null)
                {
                    try { references.Recheck(verified[message.Id], conversationId); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
                    { failure = AttachmentUnavailable; }
                }
                var source = message with { AttachmentIssue = failure };
                if (FormatContext(document, goal, id, [.. batch, source], false).Length > maxCharacters) break;
                batch.Add(source);
            }
            if (batch.Count == 0) return null;
            var claimed = new List<GoalInboxMessage>();
            foreach (var message in batch)
            {
                var index = document.Messages.FindIndex(value => value.Id == message.Id);
                var presented = message with { Sequence = ++document.Sequence, State = GoalDeliveryState.Presenting, DeliveryId = id,
                    DeliveryDetail = message.AttachmentIssue ?? "Waiting for the worker's next supported stopping point." };
                document.Messages[index] = presented;
                claimed.Add(presented);
            }
            var delivery = new GoalDelivery(id, goalId, conversationId, workerId, claimed.ToArray(), FormatContext(document, goal, id, claimed, false))
                { Chronology = Chronology(document, goal, claimed) };
            document.Deliveries.Add(delivery);
            return delivery;
        }, true, ct).ConfigureAwait(false);
    }

    public Task<GoalHeldInputBatch?> GetHeldInputAsync(Guid goalId, string conversationId, string expectedLeaseToken,
        int maxCharacters = 16000, CancellationToken ct = default)
        => Access<GoalHeldInputBatch?>((document, rows) =>
        {
            var goal = RequireGoal(rows, goalId);
            if (goal.ConversationId != conversationId || string.IsNullOrEmpty(expectedLeaseToken)
                || goal.Status != ContinualObjectiveStatus.InProgress || goal.LeaseToken != expectedLeaseToken
                || goal.LeaseExpiresAtUtc <= _time.GetUtcNow())
                throw new UnauthorizedAccessException("Only the current worker may inspect this goal's retained input.");
            if (maxCharacters < 1000) throw new ArgumentException("An adequate inspection context budget is required.");
            var batch = new List<GoalInboxMessage>();
            foreach (var message in document.Messages.Where(m => m.GoalId == goalId && m.State != GoalDeliveryState.Received
                && IsPending(document, m)))
            {
                if (string.IsNullOrEmpty(message.DeliveryId)) throw new InvalidDataException("Retained input has no exact delivery identity. Inspect the goal inbox before continuing.");
                if (FormatContext(document, goal, null, [.. batch, message], true).Length > maxCharacters)
                {
                    if (batch.Count == 0) throw new InvalidDataException("The full retained input exceeds this inspection budget. Inspect the goal inbox before continuing.");
                    break;
                }
                batch.Add(message);
            }
            return batch.Count == 0 ? null : new(goalId, batch.ToArray(), FormatContext(document, goal, null, batch, true));
        }, false, ct);

    public Task<bool> ToolsWithheldAsync(Guid goalId, string conversationId, string expectedLeaseToken, CancellationToken ct = default)
        => Access((document, rows) =>
        {
            var goal = RequireGoal(rows, goalId);
            if (goal.ConversationId != conversationId || string.IsNullOrEmpty(expectedLeaseToken)
                || goal.Status != ContinualObjectiveStatus.InProgress || goal.LeaseToken != expectedLeaseToken
                || goal.LeaseExpiresAtUtc <= _time.GetUtcNow())
                throw new UnauthorizedAccessException("Only the current worker may resolve this goal's authority.");
            var snapshot = Instructions(document, goal);
            var objective = snapshot.Sources.Single(s => s.Id == "objective");
            // Ordinary input may narrow the current authority, but never silently widen it.
            // Explicit objective replacement has a captured position and becomes the new baseline.
            return snapshot.Sources.Where(s => s.Order >= objective.Order)
                .Any(s => UserToolUseRestriction.WithholdsAuthenticatedOriginal(s.Text));
        }, false, ct);

    private static string FormatContext(Document document, ContinualObjectiveSnapshot goal, string? deliveryId,
        IReadOnlyList<GoalInboxMessage> messages, bool inspection)
    {
        var chronology = Chronology(document, goal, messages);
        if (!inspection) return GoalMessaging.FormatDelivery(deliveryId!, messages, chronology);
        return "INSPECTION OF PREVIOUSLY CLAIMED INPUT. These exact messages may already have reached a prior worker. This is not fresh permission to execute or repeat an action. Do not write, send, run commands, or replay physical work. Read the retained bodies, keep current authority, then acknowledge each using the explicit final-output reply envelope. Incorporated only records the current plan; it is not completion proof.\n"
            + string.Join("\n\n", messages.Select(m => GoalMessaging.FormatDelivery(m.DeliveryId!, [m], Chronology(document, goal, [m]))));
    }

    private static GoalInputChronology Chronology(Document document, ContinualObjectiveSnapshot goal,
        IReadOnlyList<GoalInboxMessage> messages)
    {
        var snapshot = Instructions(document, goal);
        var objective = snapshot.Sources.Single(s => s.Id == "objective");
        var sources = snapshot.Sources.ToDictionary(s => s.Id, StringComparer.Ordinal);
        return new(objective.Order, objective.CreatedUtc, objective.Sha256,
            messages.Select(m => new GoalInputAuthority(m.Id,
                sources.TryGetValue(m.Id, out var source) ? source.Order : null, m.CreatedUtc)).ToArray());
    }

    public Task RecordDeliveryAsync(string deliveryId, GoalDeliveryState state, string? detail = null, CancellationToken ct = default)
        => Access((document, rows) =>
        {
            if (state is not (GoalDeliveryState.Delivered or GoalDeliveryState.Uncertain or GoalDeliveryState.Received))
                throw new ArgumentException("A transport cannot establish that an agent read or incorporated a message.");
            var delivery = document.Deliveries.SingleOrDefault(value => value.Id == deliveryId)
                ?? throw new InvalidOperationException("The exact message delivery is missing.");
            RequireGoal(rows, delivery.GoalId);
            foreach (var message in delivery.Messages)
            {
                var index = document.Messages.FindIndex(value => value.Id == message.Id);
                var current = document.Messages[index];
                // An actual worker reply may race the provider ACK; preserve the stronger factual receipt.
                if (current.DeliveryId != deliveryId || current.State is GoalDeliveryState.Read or GoalDeliveryState.Incorporated or GoalDeliveryState.Replied) continue;
                if (current.State == GoalDeliveryState.Delivered && state is GoalDeliveryState.Delivered or GoalDeliveryState.Uncertain) continue;
                if (state == GoalDeliveryState.Received && current.State != GoalDeliveryState.Presenting)
                    throw new InvalidOperationException("Accepted or uncertain input cannot be automatically replayed.");
                if (current.State == GoalDeliveryState.Uncertain && state == GoalDeliveryState.Delivered)
                    throw new InvalidOperationException("Uncertain input requires exact transport reconciliation.");
                document.Messages[index] = current with { Sequence = ++document.Sequence, State = state, DeliveryId = state == GoalDeliveryState.Received ? null : deliveryId,
                    DeliveryDetail = new CredentialScrubbingContinualObjectiveTextSanitizer().Sanitize(detail, 2000) };
            }
            return true;
        }, true, ct);

    public Task<GoalReply> ReplyAsync(Guid goalId, string conversationId, string deliveryId, string messageId,
        string text, GoalDeliveryState disposition, CancellationToken ct = default)
    {
        var current = AsyncLocalCurrentTurnContextAccessor.Ambient;
        if (current?.GoalMessageGoalId != goalId || current.ConversationId != conversationId || string.IsNullOrWhiteSpace(current.GoalMessageLeaseToken))
            throw new UnauthorizedAccessException("A reply requires the exact host-bound goal worker.");
        return ReplyForWorkerAsync(goalId, conversationId, deliveryId, messageId, text, disposition, current.GoalMessageLeaseToken, ct);
    }

    /// <summary>Host authorization adapter; the lease comes from trusted worker context, never a model argument.</summary>
    public Task<GoalReply> ReplyForWorkerAsync(Guid goalId, string conversationId, string deliveryId, string messageId,
        string text, GoalDeliveryState disposition, string expectedLeaseToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > GoalMessaging.MaximumReplyCharacters
            || disposition is not (GoalDeliveryState.Read or GoalDeliveryState.Incorporated or GoalDeliveryState.Replied))
            throw new ArgumentException("A useful reply and a semantic message disposition are required.");
        text = new CredentialScrubbingContinualObjectiveTextSanitizer().Sanitize(text, int.MaxValue)!;
        return Access((document, rows) =>
        {
            var goal = RequireGoal(rows, goalId);
            if (goal.Status != ContinualObjectiveStatus.InProgress || goal.LeaseToken is null || goal.LeaseToken != expectedLeaseToken
                || goal.LeaseExpiresAtUtc <= _time.GetUtcNow())
                throw new UnauthorizedAccessException("This worker is no longer active; the goal's explicit pause and inbox were retained.");
            var delivery = document.Deliveries.SingleOrDefault(value => value.Id == deliveryId);
            var index = document.Messages.FindIndex(value => value.GoalId == goalId && value.Id == messageId);
            if (goal.ConversationId != conversationId || delivery?.GoalId != goalId || delivery.ConversationId != conversationId
                || index < 0 || document.Messages[index].DeliveryId != deliveryId || !delivery.Messages.Any(value => value.Id == messageId))
                throw new UnauthorizedAccessException("The reply is not bound to this goal's actual message delivery.");
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deliveryId + "\n" + messageId + "\n" + disposition + "\n" + text))).ToLowerInvariant();
            if (document.Replies.FirstOrDefault(value => value.Id == id) is { } prior) return prior;
            var current = document.Messages[index];
            if (current.State is not (GoalDeliveryState.Incorporated or GoalDeliveryState.Replied) || disposition != GoalDeliveryState.Read)
                document.Messages[index] = current with { Sequence = ++document.Sequence, State = disposition, DeliveryDetail = "The working agent replied to this message." };
            var reply = new GoalReply(id, goalId, messageId, ++document.Sequence, text, disposition, _time.GetUtcNow());
            document.Replies.Add(reply);
            return reply;
        }, true, ct);
    }

    /// <summary>Called only while the owning goal inventory lock is held.</summary>
    internal static bool HasPending(SwarmCodeDataPaths paths, Guid goalId)
    {
        var document = ReadDocument(Path.Combine(paths.RootDirectory, "goals", "messages.json"));
        return Pending(document, goalId);
    }

    public Task<bool> HasPendingAsync(Guid goalId, CancellationToken ct = default)
        => Access((document, rows) => { RequireGoal(rows, goalId); return Pending(document, goalId); }, false, ct);

    private static bool Pending(Document document, Guid goalId)
        => document.Messages.Any(message => message.GoalId == goalId && IsPending(document, message));

    private static bool IsPending(Document document, GoalInboxMessage message)
        => message.Kind == GoalMessageKind.Question ? !document.Replies.Any(reply => reply.MessageId == message.Id && GoalMailboxStatus.IsAgentReply(reply))
            : message.State is not (GoalDeliveryState.Incorporated or GoalDeliveryState.Replied)
                && !(message.State == GoalDeliveryState.Read && GoalMessaging.IsAttachmentOnlyReference(message.Text, message.Attachments));

    public Task<GoalInstructionSnapshot> GetInstructionSnapshotAsync(Guid goalId, CancellationToken ct = default)
        => Access((document, rows) => Instructions(document, RequireGoal(rows, goalId)), false, ct);

    private static GoalInstructionSnapshot Instructions(Document document, ContinualObjectiveSnapshot goal)
    {
        var payload = CodingGoalPayload.Read(goal);
        var messages = document.Messages.Where(m => m.GoalId == goal.Id && m.Kind != GoalMessageKind.Question).ToArray();
        var after = payload.AuthorityAfterMessageCount ?? 0;
        if (after < 0 || after > messages.Length) throw new InvalidDataException("The goal's instruction chronology is incomplete.");
        var sources = new List<GoalInstructionSource>();
        // Sequence is a change cursor and increases when a worker replies. List insertion order is
        // the immutable user-instruction chronology. An explicit Steer replaces the current goal
        // authority at its actual position, so an older inbox message cannot override that new edit.
        for (var index = 0; index <= messages.Length; index++)
        {
            if (index == after) sources.Add(new("objective", sources.Count, "Objective", goal.ObjectiveText, payload.AuthorityUpdatedAtUtc ?? goal.CreatedAtUtc));
            if (index < messages.Length)
            {
                var message = messages[index];
                sources.Add(new(message.Id, sources.Count, message.Kind.ToString(), message.Text, message.CreatedUtc) { Attachments = message.Attachments });
            }
        }
        return new(goal.Id, sources.ToArray());
    }

    /// <summary>Called only under the owning goal inventory lock when an explicit Steer commits.</summary>
    internal static int InstructionCount(SwarmCodeDataPaths paths, Guid goalId)
        => ReadDocument(Path.Combine(paths.RootDirectory, "goals", "messages.json")).Messages
            .Count(m => m.GoalId == goalId && m.Kind != GoalMessageKind.Question);

    public static string AcceptanceEvidenceDigest(ContinualObjectiveSnapshot goal)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var receipt in goal.Receipts.OrderBy(r => r.Attempt).ThenBy(r => r.Sequence).ThenBy(r => r.ToolCallId, StringComparer.Ordinal))
        {
            digest.AppendData(Encoding.ASCII.GetBytes(ContinualReceiptResolutionPolicy.Fingerprint(receipt)));
            digest.AppendData(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(receipt.HostResolution, Json)));
        }
        return Convert.ToHexString(digest.GetHashAndReset());
    }

    /// <summary>Host-only audit commit; validates current authority/evidence inside the goal inventory lock.</summary>
    public Task<bool> TryRecordAcceptanceAsync(GoalInstructionSnapshot snapshot, string expectedLeaseToken,
        string expectedEvidenceDigest, string outputDigest, CancellationToken ct = default)
        => Access((document, rows) =>
        {
            var goal = RequireGoal(rows, snapshot.GoalId);
            if (goal.Status != ContinualObjectiveStatus.InProgress || goal.LeaseToken != expectedLeaseToken
                || string.IsNullOrEmpty(expectedLeaseToken) || goal.LeaseExpiresAtUtc <= _time.GetUtcNow()
                || Pending(document, goal.Id) || Instructions(document, goal).Revision != snapshot.Revision
                || AcceptanceEvidenceDigest(goal) != expectedEvidenceDigest || outputDigest.Length != 64
                || outputDigest.Any(c => !Uri.IsHexDigit(c))) return false;
            document.AcceptanceProofs[goal.Id] = new(snapshot.Revision, expectedEvidenceDigest, outputDigest,
                goal.AttemptCount, ContinualDeliveryEvidence.OutputDigest(expectedLeaseToken));
            return true;
        }, true, ct);

    /// <summary>Must be called while the owning goal inventory lock is held, immediately before completion.</summary>
    internal static bool CanComplete(SwarmCodeDataPaths paths, ContinualObjectiveSnapshot goal, string? output)
    {
        var document = ReadDocument(Path.Combine(paths.RootDirectory, "goals", "messages.json"));
        if (Pending(document, goal.Id)) return false;
        var snapshot = Instructions(document, goal);
        if (snapshot.Sources.Count == 1) return true;
        return output is not null && document.AcceptanceProofs.TryGetValue(goal.Id, out var proof)
            && proof.InputRevision == snapshot.Revision && proof.EvidenceDigest == AcceptanceEvidenceDigest(goal)
            && proof.OutputDigest == ContinualDeliveryEvidence.OutputDigest(output) && proof.Attempt == goal.AttemptCount
            && goal.LeaseToken is not null && proof.WorkerLeaseDigest == ContinualDeliveryEvidence.OutputDigest(goal.LeaseToken);
    }

    private async Task<T> Access<T>(Func<Document, IReadOnlyList<ContinualObjectiveSnapshot>, T> action, bool write, CancellationToken ct,
        bool onlyIfSequenceChanged = false)
        => await goals.WithGoalsAsync(async rows =>
        {
            var document = ReadDocument(_path);
            var originalSequence = document.Sequence;
            var result = action(document, rows);
            if (write && (!onlyIfSequenceChanged || document.Sequence != originalSequence))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        await JsonSerializer.SerializeAsync(stream, document, Json, ct).ConfigureAwait(false);
                        stream.Flush(flushToDisk: true);
                    }
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    File.Move(temporary, _path, overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            return result;
        }, ct).ConfigureAwait(false);

    // Runs only with a fresh mailbox and goal snapshot under the shared inventory OS lock.
    // This owns mailbox receipts alone: never mutate goal controls, leases, delivery or input state.
    private void RecordHeldStatus(Document document, ContinualObjectiveSnapshot goal)
    {
        if (ContinualObjectiveStateMachine.IsTerminal(goal.Status)
            || GoalMailboxStatus.Detail(goal.Status, goal.NextAttemptAtUtc) is not { } detail) return;
        var blocker = SafeStatusText(goal.LastFailure);
        foreach (var message in document.Messages.Where(message => message.GoalId == goal.Id && message.State == GoalDeliveryState.Received))
        {
            var id = "host-status-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(goal.Id + "\n" + message.Id))).ToLowerInvariant();
            if (document.StatusNotices.Any(reply => reply.Id == id)) continue;
            document.StatusNotices.Add(new(id, goal.Id, message.Id, ++document.Sequence,
                GoalMailboxStatus.Acknowledgment(detail, blocker), GoalDeliveryState.Received, _time.GetUtcNow())
                { Origin = GoalReplyOrigin.HostStatus, ObservedGoalVersion = goal.Version });
        }
    }

    private static string? SafeStatusText(string? value)
        => new CredentialScrubbingContinualObjectiveTextSanitizer().Sanitize(value, 1200);

    private static Document ReadDocument(string path)
    {
        if (!File.Exists(path)) return new Document();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var document = JsonSerializer.Deserialize<Document>(stream, Json) ?? throw new InvalidDataException("The saved goal messages are unreadable.");
        if (document.Schema != 1 || document.InstanceId.Length != 32 || document.InstanceId.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("The saved goal message format or profile identity is unsupported.");
        return document;
    }

    private static ContinualObjectiveSnapshot RequireGoal(IReadOnlyList<ContinualObjectiveSnapshot> rows, Guid id)
        => rows.FirstOrDefault(row => row.Id == id && row.PartitionKey == CodingGoalOrchestrator.Partition && row.Mission is null)
            ?? throw new InvalidOperationException("The goal no longer exists in this Code profile.");

    private static GoalContact Contact(Document document, ContinualObjectiveSnapshot goal)
    {
        var name = document.Names.FirstOrDefault(value => value.GoalId == goal.Id);
        return new GoalContact(goal.Id, goal.ConversationId, name?.Name ?? goal.ObjectiveSummary,
            name?.Handle ?? "goal-" + goal.Id.ToString("N")[..12], CodingGoalPayload.Read(goal).RepositoryRoot,
            CodingGoalStatus.Label(goal.Status), name?.Revision ?? 0)
            { IsClosed = ContinualObjectiveStateMachine.IsTerminal(goal.Status), GoalVersion = goal.Version,
                GoalEventSequence = goal.LastEventSequence,
                StatusDetail = GoalMailboxStatus.Detail(goal.Status, goal.NextAttemptAtUtc),
                Blocker = SafeStatusText(goal.LastFailure), LastRecordedUpdate = SafeStatusText(goal.LastOutput) };
    }

    private static void Validate(GoalMessageCommand message)
    {
        if (message is null || !GoalMessaging.AreValidAttachments(message.Attachments) || string.IsNullOrWhiteSpace(message.Text) || message.Text.Length > GoalMessaging.MaximumMessageCharacters
            || !Enum.IsDefined(message.Kind) || string.IsNullOrWhiteSpace(message.IdempotencyKey) || message.IdempotencyKey.Length > 256
            || message.Origin is null || string.IsNullOrWhiteSpace(message.Origin.Channel) || message.Origin.Channel.Length > 64
            || string.IsNullOrWhiteSpace(message.Origin.AuthorId) || message.Origin.AuthorId.Length > 256
            || string.IsNullOrWhiteSpace(message.Origin.SourceId) || message.Origin.SourceId.Length > 512)
            throw new ArgumentException("A complete authenticated message, source identity and stable message ID are required. Long material can be supplied as a link.");
    }

    private sealed record ContactName(Guid GoalId, string Name, string Handle, long Revision);
    private sealed record SavedCommand(string Key, Guid GoalId, string MessageId, GoalMessageCommand Command, string Fingerprint);
    private sealed record AcceptanceProof(string InputRevision, string EvidenceDigest, string OutputDigest, int Attempt, string WorkerLeaseDigest);
    private sealed class Document
    {
        public int Schema { get; set; } = 1;
        public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");
        public long Sequence { get; set; }
        public List<ContactName> Names { get; set; } = [];
        public List<SavedCommand> Commands { get; set; } = [];
        public List<GoalInboxMessage> Messages { get; set; } = [];
        public List<GoalReply> Replies { get; set; } = [];
        // Separate from semantic replies so an older reader that ignores new fields cannot
        // mistake a host receipt for an answer or completion evidence after a downgrade.
        public List<GoalReply> StatusNotices { get; set; } = [];
        public List<GoalDelivery> Deliveries { get; set; } = [];
        public Dictionary<string, Guid> RetiredHandles { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<Guid, AcceptanceProof> AcceptanceProofs { get; set; } = [];
    }
}
