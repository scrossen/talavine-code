using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using SwarmCore.Agent.Communication;

namespace SwarmCode.App.Components.Chat;

/// <summary>UI-only retry state, scoped to the actual profile mailbox and logical composer.</summary>
public sealed class GoalInboxDraft
{
    private static readonly ConditionalWeakTable<IGoalMailbox, ConcurrentDictionary<string, GoalInboxDraft>> Drafts = new();
    public static GoalInboxDraft For(IGoalMailbox mailbox, string scope)
        => Drafts.GetOrCreateValue(mailbox).GetOrAdd(scope, _ => new());

    public string Text { get; set; } = "";
    public string SourceText { get; set; } = "";
    public GoalMessageKind Kind { get; set; }
    public IReadOnlyList<GoalMessageAttachment> Attachments { get; set; } = [];
    public IReadOnlyList<string> SourceAttachmentIds { get; set; } = [];
    public IReadOnlyList<string> PendingSourceAttachmentIds { get; private set; } = [];
    public bool IsSending { get; private set; }
    public string? Error { get; private set; }
    public GoalInboxMessage? Saved { get; private set; }
    public GoalMessageCommand? Pending { get; private set; }
    public Guid? PendingGoalId { get; private set; }
    public string? PendingSourceText { get; private set; }
    private string _capturedDraft = "";
    private bool _earlierOutcomeUnconfirmed;

    public Task<GoalInboxMessage?> SubmitAsync(IGoalMailbox mailbox, Guid goalId)
    {
        if (IsSending) return Task.FromResult<GoalInboxMessage?>(null);
        var body = string.IsNullOrWhiteSpace(Text) && Attachments.Count > 0 ? GoalMessaging.AttachmentOnlyText : Text.Trim();
        if (string.IsNullOrWhiteSpace(body) || body.Length > GoalMessaging.MaximumMessageCharacters)
        {
            Error = $"Enter a message of 1–{GoalMessaging.MaximumMessageCharacters} characters. Your draft was kept.";
            return Task.FromResult<GoalInboxMessage?>(null);
        }
        if (Pending is not null && (PendingGoalId != goalId || Pending.Text != body || Pending.Kind != Kind
            || !(Pending.Attachments ?? []).SequenceEqual(Attachments)
            || !PendingSourceAttachmentIds.SequenceEqual(SourceAttachmentIds)))
        {
            Error = "Retry the pending message first. Your newer draft is kept separately.";
            return Task.FromResult<GoalInboxMessage?>(null);
        }
        if (Pending is null)
        {
            _earlierOutcomeUnconfirmed = false;
            var id = Guid.NewGuid().ToString("N");
            Pending = new(id, body, Kind, new("code-ui", id, "local-user"))
                { Attachments = Attachments.Count == 0 ? null : Attachments.ToArray() };
            PendingSourceAttachmentIds = SourceAttachmentIds.ToArray();
            PendingGoalId = goalId;
            PendingSourceText = SourceText;
            _capturedDraft = Text;
        }
        return RetryAsync(mailbox);
    }

    public async Task<GoalInboxMessage?> RetryAsync(IGoalMailbox mailbox)
    {
        if (IsSending || Pending is not { } command || PendingGoalId is not { } target) return null;
        IsSending = true; Error = null;
        try
        {
            var saved = await Task.Run(() => mailbox.SendAsync(target, command));
            Saved = saved;
            if (Text == _capturedDraft && Kind == command.Kind && Attachments.SequenceEqual(command.Attachments ?? [])
                && SourceAttachmentIds.SequenceEqual(PendingSourceAttachmentIds)) Text = "";
            if (Attachments.SequenceEqual(command.Attachments ?? [])) Attachments = [];
            if (SourceAttachmentIds.SequenceEqual(PendingSourceAttachmentIds)) SourceAttachmentIds = [];
            Pending = null; PendingGoalId = null; PendingSourceText = null; PendingSourceAttachmentIds = [];
            return saved;
        }
        catch (GoalMessageRejectedException)
        {
            if (_earlierOutcomeUnconfirmed)
            {
                Error = "This attempt was rejected, but an earlier delivery is still unconfirmed. The exact pending message is kept; retry it to reconcile its saved receipt before sending changes.";
                return null;
            }
            Error = "The message was not admitted. Use fewer regular, unchanged files, check Settings → Attachments, then send again. Your text and files were kept.";
            Pending = null; PendingGoalId = null; PendingSourceText = null; PendingSourceAttachmentIds = [];
            return null;
        }
        catch (Exception)
        {
            _earlierOutcomeUnconfirmed = true;
            Error = "Delivery was not confirmed. Retry this same message safely; your draft and message identity are kept.";
            return null;
        }
        finally { IsSending = false; }
    }
}

public sealed record GoalMention(GoalContact? Contact, string Text, string? Error = null);

/// <summary>Resolve trusted contact IDs before the existing provider-prefix parser can change selection.</summary>
public static class GoalMessageRouting
{
    public static GoalMention Resolve(string text, IReadOnlyList<GoalContact> contacts, IEnumerable<string> lanes, bool hasAttachments = false)
    {
        if (!text.StartsWith('@')) return new(null, text);
        string name, rest;
        var explicitGoal = text.StartsWith("@goal:", StringComparison.OrdinalIgnoreCase);
        var value = text[(explicitGoal ? 6 : 1)..];
        if (value.StartsWith('"'))
        {
            var close = value.IndexOf('"', 1);
            if (close < 0) return new(null, text, "Close the quoted goal name before sending.");
            name = value[1..close]; rest = value[(close + 1)..].Trim();
        }
        else
        {
            var split = value.IndexOfAny([' ', '\t', '\n', '\r']);
            name = split < 0 ? value : value[..split]; rest = split < 0 ? "" : value[(split + 1)..].Trim();
        }
        var matches = contacts.Where(c => string.Equals(c.Handle, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)).DistinctBy(c => c.GoalId).ToArray();
        var lane = !explicitGoal && lanes.Any(l => string.Equals(l, name, StringComparison.OrdinalIgnoreCase));
        if (matches.Length > 1 || matches.Length > 0 && lane)
            return new(null, text, "That name has more than one recipient. Choose the goal in Agent inbox, or use @goal:handle to distinguish it from a provider.");
        if (matches.Length == 1)
            return matches[0].IsClosed
                ? new(null, text, "This goal is closed. Its history remains in Agent inbox. Start another goal to continue; your draft was kept.")
                : rest.Length == 0 && !hasAttachments ? new(null, text, "Add a message after the goal name.") : new(matches[0], rest.Length == 0 ? GoalMessaging.AttachmentOnlyText : rest);
        if (lane) return new(null, text); // Existing @lane handling retains its behavior.
        return new(null, text, "No goal or provider matches that name. Choose a recipient in Agent inbox or use /lanes.");
    }
}
