using SwarmCode.Hosting.Goals;
using SwarmCore.Agent.Communication;
using SwarmCore.Coding.Files;

namespace SwarmCode.App.Components.Chat;

/// <summary>Capture before any await: navigation must never change a send's mailbox, recipient or draft.</summary>
public sealed record GoalInboxSendScope(string ConversationId, IGoalMailbox Mailbox, GoalInboxDraft Draft,
    Guid? FallbackGoalId, bool UsesInbox, string RawText, GoalMessageKind Kind,
    IReadOnlyList<CodingAttachment> Files, IReadOnlyList<string> Lanes)
{
    public bool IsCurrent(string conversationId, IGoalMailbox mailbox)
        => ConversationId == conversationId && ReferenceEquals(Mailbox, mailbox);

    public async Task<GoalInboxSendResult> SendAsync(string text)
    {
        var contacts = await Task.Run(() => Mailbox.ListAsync());
        var route = GoalMessageRouting.Resolve(text, contacts, Lanes, Files.Count > 0);
        if (route.Error is not null) return new(true, null, route.Error);
        if (route.Contact is null && text.StartsWith('@') && UsesInbox)
            return new(true, null, "Choose a provider with /lane, or address a goal with @goal:handle. This message was not sent and your model choice is unchanged.");
        var target = route.Contact?.GoalId ?? FallbackGoalId;
        if (target is null) return new(false, null, null);
        var body = Files.Count > 0 && string.IsNullOrWhiteSpace(RawText) ? GoalMessaging.AttachmentOnlyText : route.Text;
        var ids = Files.Select(f => f.Id).ToArray();
        if (Draft.Pending is not null && (Draft.PendingGoalId != target || Draft.Pending.Text != body.Trim()
            || Draft.Pending.Kind != Kind || !Draft.PendingSourceAttachmentIds.SequenceEqual(ids)))
            return new(true, null, "Retry the pending message first. Your newer draft and files were kept separately.");
        var references = Draft.Pending?.Attachments ?? (Files.Count == 0 ? []
            : Mailbox is IGoalAttachmentMailbox local ? await Task.Run(() => local.PrepareAttachmentsAsync(target.Value, Files))
            : throw new InvalidOperationException("This inbox does not support local attachment references."));
        Draft.SourceText = RawText; Draft.Text = body; Draft.Kind = Kind;
        Draft.Attachments = references; Draft.SourceAttachmentIds = ids;
        var saved = await Draft.SubmitAsync(Mailbox, target.Value);
        return new(true, saved, saved is null ? Draft.Error ?? "The message is being saved. Your draft was kept." : null);
    }

    public IReadOnlyList<CodingAttachment> RemainingFiles(IReadOnlyList<CodingAttachment> current)
        => current.Where(file => !Files.Any(sent => sent.Id == file.Id)).ToArray();
}

public sealed record GoalInboxSendResult(bool Handled, GoalInboxMessage? Saved, string? Warning);
