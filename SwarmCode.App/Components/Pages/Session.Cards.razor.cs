using System.Collections.Concurrent;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using SwarmCode.Hosting;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Runtime;
using SwarmCore.Chat.Components;
using SwarmCore.Skills.Models;

namespace SwarmCode.App.Components.Pages;

/// <summary>
/// Host wiring for the shared transcript cards: live progress and per-step cancel for
/// tool calls, the three-way permission choice in approval mode, and recovery actions
/// on a turn that failed.
/// </summary>
public partial class Session
{
    private sealed class StepProgress
    {
        public string? Status { get; set; }
        public double? Fraction { get; set; }
        public bool CancelRequested { get; set; }
    }

    private readonly ConcurrentDictionary<string, StepProgress> _stepProgress = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _stepStarted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _permissionChangeRequested = new(StringComparer.Ordinal);
    private IAgentToolProgressBroker? _progressBroker;
    private System.Threading.Timer? _stepClock;
    private string? _recoveryNoticeId;

    /// <summary>The persistent choice in SwarmCode lasts for the conversation (the hosting broker keeps it in memory).</summary>
    private const string ConversationGrantLabel = "Allow for this conversation";

    private void AttachCards(WorkspaceHost workspace)
    {
        DetachCards();
        _progressBroker = workspace.Services.GetService<IAgentToolProgressBroker>();
        if (_progressBroker is not null) _progressBroker.Updated += OnStepProgress;
        // Running-step clocks advance once a second while any step runs, coalesced with other renders.
        _stepClock = new System.Threading.Timer(_ =>
        {
            if (!_disposed && _runner?.Entries.Any(e => e is ToolCallEntry { State: ToolCallState.Running }) == true) RequestRender();
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void DetachCards()
    {
        if (_progressBroker is not null) _progressBroker.Updated -= OnStepProgress;
        _progressBroker = null;
        _stepClock?.Dispose();
        _stepClock = null;
        _stepProgress.Clear();
        _stepStarted.Clear();
        lock (_permissionChangeRequested) _permissionChangeRequested.Clear();
    }

    // ---- tool calls ----------------------------------------------------------

    private void OnStepProgress(ToolProgressUpdate update)
    {
        var progress = _stepProgress.GetOrAdd(update.ToolCallId, _ => new StepProgress());
        progress.Status = update.Status;
        progress.Fraction = update.Fraction;
        RequestRender();
    }

    /// <summary>What the running step says it is doing; a running step always gets a status so it can be stopped.</summary>
    private string? StepStatus(ToolCallEntry tool)
    {
        if (tool.State != ToolCallState.Running)
        {
            _stepProgress.TryRemove(tool.ToolCallId, out _);
            _stepStarted.TryRemove(tool.Id, out _);
            return null;
        }

        return _stepProgress.TryGetValue(tool.ToolCallId, out var progress) && !string.IsNullOrWhiteSpace(progress.Status)
            ? progress.Status
            : "Running…";
    }

    private double? StepFraction(ToolCallEntry tool)
        => _stepProgress.TryGetValue(tool.ToolCallId, out var progress) ? progress.Fraction : null;

    private bool StepCancelRequested(ToolCallEntry tool)
        => _stepProgress.TryGetValue(tool.ToolCallId, out var progress) && progress.CancelRequested;

    private DateTime StepStarted(ToolCallEntry tool) => _stepStarted.GetOrAdd(tool.Id, _ => tool.AtUtc);

    /// <summary>
    /// Stops one running step. When the engine is not tracking it (it just finished, it is still
    /// waiting on its approval card, or a native lane runs it), nothing is stopped: the button
    /// resets and the composer's Stop remains the way to end the whole turn.
    /// </summary>
    private void CancelStep(ToolCallEntry tool)
    {
        if (tool.State != ToolCallState.Running) return;
        var progress = _stepProgress.GetOrAdd(tool.ToolCallId, _ => new StepProgress());
        if (progress.CancelRequested) return;
        progress.CancelRequested = true;
        if (_progressBroker?.Cancel(tool.ToolCallId) == true) return;

        progress.CancelRequested = false;
        AddNote("info", "That step could not be stopped on its own (it may have just finished, be waiting for approval, or run inside the provider's session). Use Stop to end the whole turn.");
    }

    // ---- permissions ---------------------------------------------------------

    private static string PermissionTitle(PermissionEntry entry)
        => string.IsNullOrWhiteSpace(entry.SkillName) ? entry.Title : entry.SkillName;

    private static string? PermissionTarget(PermissionEntry entry)
        => string.IsNullOrWhiteSpace(entry.FunctionName) ? null : entry.FunctionName;

    /// <summary>Same rule as Talavine: a lasting grant only when every requested level is below Restricted.</summary>
    private static bool CanGrantForConversation(PermissionEntry entry)
        => entry.Levels.Count > 0 && entry.Levels.All(level => level < PermissionLevel.Restricted);

    private static PermissionCardDecision? PermissionDecision(PermissionEntry entry) => entry.Approved switch
    {
        null => null,
        false => PermissionCardDecision.Deny,
        true when entry.Scope == PermissionGrantScope.Conversation => PermissionCardDecision.AlwaysAllow,
        _ => PermissionCardDecision.ApproveOnce,
    };

    /// <summary>
    /// Answers a question card (user.ask). The runner updates and journals the entry and releases the waiting tool;
    /// its journal write runs on the background chain, so nothing blocks the renderer here.
    /// </summary>
    private void AnswerQuestion(QuestionEntry entry, string? text, IReadOnlyList<string> selected)
        => _runner?.AnswerQuestion(entry.RequestId, text, selected);

    private void SkipQuestion(QuestionEntry entry) => _runner?.DeclineQuestion(entry.RequestId);

    private void DecidePermission(PermissionEntry entry, PermissionCardDecision decision)
    {
        var scope = decision == PermissionCardDecision.AlwaysAllow && CanGrantForConversation(entry)
            ? PermissionGrantScope.Conversation
            : PermissionGrantScope.Once;
        _runner?.Respond(entry.RequestId, decision != PermissionCardDecision.Deny, scope);
    }

    /// <summary>
    /// Change withdraws a conversation grant (any time it is still in force) and, while the turn runs,
    /// stops it so a retry asks again.
    /// </summary>
    private bool CanChangePermission(PermissionEntry entry)
        => entry.Approved is not null
           && (_runner is { IsRunning: true } || (entry.Scope == PermissionGrantScope.Conversation && !entry.GrantRevoked));

    private bool PermissionChangeRequested(PermissionEntry entry)
    {
        lock (_permissionChangeRequested) return _permissionChangeRequested.Contains(entry.RequestId) && _runner is { IsRunning: true };
    }

    private void ChangePermission(PermissionEntry entry)
    {
        if (entry.Scope == PermissionGrantScope.Conversation && !entry.GrantRevoked)
            _runner?.RevokePermission(entry.RequestId);
        if (_runner is { IsRunning: true })
        {
            lock (_permissionChangeRequested)
            {
                if (!_permissionChangeRequested.Add(entry.RequestId)) return;
            }
            _runner.Cancel();
        }
    }

    private static RenderFragment? PermissionReceiptNote(PermissionEntry entry) => entry.GrantRevoked
        ? builder =>
        {
            builder.OpenElement(0, "span");
            builder.AddAttribute(1, "class", "sess-muted");
            builder.AddContent(2, "Grant withdrawn; the next request asks again.");
            builder.CloseElement();
        }
        : null;

    // ---- recovery ------------------------------------------------------------

    /// <summary>The failure the user can act on now: the last error after the last request, once the turn has stopped.</summary>
    private static string? FindRecoveryNotice(IReadOnlyList<TranscriptEntry> entries, bool running)
    {
        if (running) return null;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            switch (entries[i])
            {
                case NoticeEntry { Kind: NoticeKind.Error or NoticeKind.LanesExhausted } notice:
                    return notice.Id;
                case UserEntry { IsContinuation: false }:
                case AssistantEntry { IsStreaming: false, Text.Length: > 0 }:
                    return null;
            }
        }
        return null;
    }

    private UserEntry? LastRequest()
        => _runner?.Entries.OfType<UserEntry>().LastOrDefault(user => !user.IsContinuation);

    private IReadOnlyList<ChatNoticeAction> RecoveryActions(NoticeEntry notice)
    {
        var actions = new List<ChatNoticeAction>();
        var runner = _runner;
        if (runner is null) return actions;
        if (runner.HasInterruptedWork)
        {
            actions.Add(new ChatNoticeAction("Continue interrupted work", EventCallback.Factory.Create(this, ContinueInterruptedWork))
            {
                Title = "Inspect saved results, then continue the remaining work",
                Style = ChatNoticeActionStyle.Primary,
            });
        }
        if (LastRequest() is { } request)
        {
            actions.Add(new ChatNoticeAction("Retry", EventCallback.Factory.Create(this, () => RetryRequestAsync(request)))
            {
                Title = "Send the same request again",
                Style = actions.Count > 0 ? ChatNoticeActionStyle.Ghost : ChatNoticeActionStyle.Primary,
            });
            actions.Add(new ChatNoticeAction("Edit", EventCallback.Factory.Create(this, () => EditRequest(request)))
            {
                Title = "Put the request back in the composer so you can change it",
                Style = ChatNoticeActionStyle.Ghost,
            });
        }
        if (runner.QueuePaused && runner.QueuedMessages.Count > 0)
        {
            actions.Add(new ChatNoticeAction("Run queued follow-ups", EventCallback.Factory.Create(this, ResumeQueueAsync))
            {
                Style = ChatNoticeActionStyle.Ghost,
            });
        }
        if (notice.Kind == NoticeKind.LanesExhausted)
        {
            actions.Add(new ChatNoticeAction("Lane settings", EventCallback.Factory.Create(this, () => Nav.NavigateTo("/settings")))
            {
                Title = "Check the configured lanes and keys",
                Style = ChatNoticeActionStyle.Ghost,
            });
        }
        return actions;
    }

    private Task RetryRequestAsync(UserEntry request)
        => StartTurnAsync(request.Text, string.IsNullOrEmpty(_lane) ? null : _lane, request.Attachments);

    private void EditRequest(UserEntry request) => _draft = request.Text;

    private static string RecoveryTitle(NoticeEntry notice)
        => notice.Kind == NoticeKind.LanesExhausted ? "No lane could finish this" : "Let’s get this moving again";
}
