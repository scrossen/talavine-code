using SwarmCode.App.Components.Chat;
using SwarmCore.Agent.Communication;
using SwarmCore.Coding.Files;

namespace SwarmCode.App.Components.Pages;

public partial class Session
{
    private GoalInboxDraft GoalDraft => GoalInboxDraft.For(App.GoalMailbox, "session:" + SessionId);
    private bool UsesGoalInbox => _runner?.HasUnfinishedGoal == true && _sendMode != "steer";
    private GoalMessageKind _goalMessageKind;
    private bool _goalMessagePreparing;

    // Runs before @lane parsing or any session model-selection write. Mailbox submission never
    // cancels a worker, changes the old follow-up queue, or resumes a paused goal.
    private async Task<bool> TrySendGoalMessageAsync(string text, IReadOnlyList<CodingAttachment> attachments)
    {
        if (!text.StartsWith('@') && !UsesGoalInbox)
        {
            if (_runner?.HasUnfinishedGoal == true && !_runner.IsRunning && _sendMode == "steer")
            {
                AddNote("warn", "This goal is paused. Send to its inbox, or explicitly resume it before interrupting active work. Your draft was kept.");
                return true;
            }
            return false;
        }
        if (_goalMessagePreparing || GoalDraft.IsSending) return true;
        _goalMessagePreparing = true;
        var scope = new GoalInboxSendScope(SessionId, App.GoalMailbox, GoalDraft,
            UsesGoalInbox ? _runner?.Goal?.Id : null, UsesGoalInbox, _draft, _goalMessageKind,
            attachments.ToArray(), Lanes.Select(lane => lane.Name).ToArray());
        try
        {
            var result = await scope.SendAsync(text);
            if (!scope.IsCurrent(SessionId, App.GoalMailbox)) return true;
            if (!result.Handled) return false;
            if (result.Saved is not { } saved) AddNote("warn", result.Warning ?? "The message was not saved. Your draft was kept.");
            else
            {
                var remaining = scope.RemainingFiles(_attachments);
                if (_draft == scope.RawText && remaining.Count == 0) { _draft = ""; scope.Draft.SourceText = ""; }
                _attachments = remaining;
                AddNote("info", "Message saved in the agent inbox · " + saved.State,
                    "Open Agent inbox for replies and current status. Paused work needs Resume before the worker can read it.");
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            if (!scope.IsCurrent(SessionId, App.GoalMailbox)) return true;
            AddNote("warn", "The files could not be saved to this goal's inbox. Reattach regular files from this Code profile, or check Settings → Attachments. Your text and files were kept.");
            return true;
        }
        catch (Exception)
        {
            if (!scope.IsCurrent(SessionId, App.GoalMailbox)) return true;
            AddNote("warn", "The agent inbox is unavailable. Your draft was kept and no ordinary turn was started. Try again or open Agent inbox.");
            return true;
        }
        finally { _goalMessagePreparing = false; }
    }

    private async Task RetryGoalMessageAsync()
    {
        var conversation = SessionId;
        var mailbox = App.GoalMailbox;
        var draft = GoalInboxDraft.For(mailbox, "session:" + conversation);
        var original = draft.PendingSourceText;
        var sourceIds = draft.PendingSourceAttachmentIds.ToArray();
        var saved = await draft.RetryAsync(mailbox);
        if (SessionId != conversation || !ReferenceEquals(mailbox, App.GoalMailbox)) return;
        if (saved is null) AddNote("warn", draft.Error ?? "The message was not confirmed. Your draft was kept.");
        else
        {
            var remaining = _attachments.Where(file => !sourceIds.Contains(file.Id, StringComparer.Ordinal)).ToArray();
            if (_draft == original && remaining.Length == 0) { _draft = ""; draft.SourceText = ""; }
            _attachments = remaining;
            AddNote("info", "Message saved in the agent inbox · " + saved.State);
        }
    }
}
