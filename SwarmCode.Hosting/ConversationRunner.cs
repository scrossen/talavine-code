using SwarmCore.Coding.Planning;
using System.Threading.Channels;
using SwarmCore.Coding.Files;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SwarmCode.Hosting.Extensions;
using SwarmCode.Hosting.Memory;
using SwarmCode.Hosting.Secrets;
using SwarmCore.Agent.Runtime;
using SwarmCore.Coding;
using SwarmCore.Coding.Checkpoints;
using SwarmCore.Coding.Interaction;
using SwarmCore.Coding.Routing;
using SwarmCore.Skills.Models;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.ToolUseEngine;
using SwarmCore.Coding.Permissions;
using SwarmCore.Coding.Extensions;

namespace SwarmCode.Hosting;

/// <summary>
/// Drives one session's turns for an interactive host. Turns run on a background task;
/// events flow through a bounded channel and are applied to the transcript model on the
/// consumer side, so a UI only has to re-render on <see cref="Changed"/>. One runner per
/// open session; create it from the workspace host's service provider.
/// </summary>
public sealed class ConversationRunner : IDisposable, IAsyncDisposable
{
    private const string InterruptedNotice = "Work was interrupted. Saved receipts are shown below; actions with no result have an unknown outcome. Continue to inspect the workspace before taking further action.";
    private readonly IServiceProvider _services;
    private readonly ILogger<ConversationRunner> _logger;
    private readonly UiPermissionBroker? _broker;
    private readonly UiUserQuestionBroker? _questions;
    private readonly SecretIntake? _secrets;
    private readonly FileConversationJobStore? _jobs;
    private readonly FileTranscriptJournal? _journal;
    private readonly SemaphoreSlim _journalGate = new(1, 1);
    private readonly List<TranscriptEntry> _entries = [];
    // Reference set of the entries currently shown; maintained beside _entries under _gate so
    // Journal can cheaply tell a cleared entry from a live one.
    private readonly HashSet<TranscriptEntry> _live = new(ReferenceEqualityComparer.Instance);
    // Presentation-journal writes are serialized through one task chain so they happen off the
    // caller's thread (the runner's lock, or the UI dispatcher for /clear and permission answers)
    // and in order. Each entry is serialized at enqueue time.
    private readonly object _journalChainGate = new();
    private Task _journalTail = Task.CompletedTask;
    private readonly object _gate = new();
    private readonly IDisposable? _brokerSubscription;
    private readonly IDisposable? _questionSubscription;
    private CancellationTokenSource? _turnCts;
    private Task? _turnTask;
    private AssistantEntry? _streaming;
    private readonly Dictionary<string, ToolCallEntry> _openTools = new();

    // Clear ordering: the producer numbers every lane event as it takes it from the orchestrator, and
    // /clear records the last number produced. The consumer applies events at or below that mark
    // without making them visible, so an event already produced when the user cleared (but not yet
    // displayed) cannot reappear after the clear or be journaled past the clear marker.
    private long _producedEvents;
    private long _clearedThroughEvent;
    private bool _applyingClearedEvent;
    private readonly List<QueuedTurn> _queue = [];
    private string _jobState = "Idle";
    private string? _jobRequest;
    private CodingContinuationContext? _jobContinuation;
    private IReadOnlyList<CodingAttachment> _jobAttachments = [];
    private DateTime? _jobStarted;
    private bool _queuePaused;
    private TimeSpan _elapsed;
    private int _completedToolCalls;
    private long? _reportedTokens;
    private decimal? _reportedCost;
    private CodingAcceptanceResult? _acceptance;
    private string? _phase;
    private int _queueMutations;
    private bool _resumeSteering;
    private int _stopGeneration;
    private long _turnSequence;
    private readonly SemaphoreSlim _queueControlGate = new(1, 1);
    private bool _undoing;
    private string? _checkpointNotice;
    private readonly IConversationPlanStore? _plans;
    private ConversationPlan _plan = ConversationPlan.Empty;
    private bool _planMode;
    private bool _planProposed;

    public sealed record QueuedTurn(string Text, string? Lane, bool RequireApproval, bool? AllowHandOff)
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N");
        public string? ModelOverride { get; init; }
        public bool IsSteering { get; init; }
        public string? OriginalTask { get; init; }
        public CodingContinuationContext? Continuation { get; init; }
        public IReadOnlyList<CodingAttachment> Attachments { get; init; } = [];
        /// <summary>The permission mode the follow-up runs with (auto, ask-risky, ask). Jobs saved before modes carry only <see cref="RequireApproval"/>.</summary>
        public string? PermissionMode { get; init; }
        [System.Text.Json.Serialization.JsonIgnore]
        public CodingPermissionMode Mode => CodingPermissionPolicy.TryParse(PermissionMode, out var mode) ? mode
            : RequireApproval ? CodingPermissionMode.Ask : CodingPermissionMode.Auto;
    }
    public IReadOnlyList<QueuedTurn> QueuedMessages { get { lock (_gate) return _queue.ToList(); } }

    public ConversationRunner(IServiceProvider services, string conversationId)
    {
        _services = services;
        ConversationId = conversationId;
        _logger = services.GetRequiredService<ILogger<ConversationRunner>>();
        _broker = services.GetService<IPermissionBroker>() as UiPermissionBroker;
        _secrets = services.GetService<SecretIntake>();
        _jobs = services.GetService<FileConversationJobStore>();
        _journal = services.GetService<FileTranscriptJournal>();
        if (_broker is not null) _brokerSubscription = _broker.Subscribe(OnPermissionRequested);
        _questions = services.GetService<IUserQuestionBroker>() as UiUserQuestionBroker;
        if (_questions is not null) _questionSubscription = _questions.Subscribe(OnQuestionRequested);
        _plans = services.GetService<IConversationPlanStore>();
        if (_plans is not null) _plans.Changed += OnPlanChanged;
    }

    /// <summary>The session's task list (plan.update or a native agent's own todo list), updated live.</summary>
    public ConversationPlan Plan { get { lock (_gate) return _plan; } }

    /// <summary>
    /// Plan mode for this session's next turns (the person's toggle; in memory, off after a restart): the agent
    /// investigates read-only and ends with a proposed plan. Host state only; the harness enforces it per lane.
    /// </summary>
    public bool PlanMode { get { lock (_gate) return _planMode; } }

    /// <summary>True after a plan-mode turn delivered a plan and plan mode is still on: the person can approve it.</summary>
    public bool PlanAwaitingApproval { get { lock (_gate) return _planMode && _planProposed && !_running; } }

    /// <summary>
    /// The person asked (<c>/compact</c>) for the working context to be compacted at the start of the next turn. Engine
    /// lanes summarize their provider-facing history before the first model call; native CLI lanes manage their own
    /// context. Cleared once a turn starts.
    /// </summary>
    public bool CompactNextTurn { get { lock (_gate) return _compactNextTurn; } }

    private bool _compactNextTurn;

    /// <summary>Marks the next turn to compact first; returns false when a turn is running (ask again after it ends).</summary>
    public bool RequestCompaction()
    {
        lock (_gate)
        {
            if (_running) return false;
            _compactNextTurn = true;
        }
        Changed?.Invoke();
        return true;
    }

    public void SetPlanMode(bool enabled)
    {
        lock (_gate)
        {
            if (_planMode == enabled) return;
            _planMode = enabled;
            _planProposed = false;
        }
        Changed?.Invoke();
    }

    /// <summary>Leaves plan mode and asks the agent to implement the approved plan (queued when a turn is running).</summary>
    public async Task<bool> ApprovePlanAsync(string? laneName, CodingPermissionMode permissionMode = CodingPermissionMode.Auto, bool? allowHandOff = null, string? modelOverride = null)
    {
        lock (_gate) { _planMode = false; _planProposed = false; }
        Changed?.Invoke();
        if (TrySend(CodingPlanMode.ApprovalMessage, laneName, permissionMode, allowHandOff, modelOverride)) return true;
        return await EnqueueAsync(CodingPlanMode.ApprovalMessage, laneName, permissionMode, allowHandOff, modelOverride).ConfigureAwait(false);
    }

    private void OnPlanChanged(string conversationId, ConversationPlan plan)
    {
        if (!string.Equals(conversationId, ConversationId, StringComparison.Ordinal)) return;
        lock (_gate) _plan = plan;
        Changed?.Invoke();
    }

    public string ConversationId { get; }

    private volatile bool _running;
    private bool _disposed;

    /// <summary>True from <see cref="TrySend"/> until just before <see cref="TurnFinished"/> is raised.</summary>
    public bool IsRunning => _running;

    /// <summary>Lane the current or last turn ran on.</summary>
    public string? ActiveLane { get; private set; }
    public string? ActiveModel { get; private set; }
    public string JobState { get { lock (_gate) return _jobState; } }
    public DateTime? JobStartedUtc { get { lock (_gate) return _jobStarted; } }
    public bool QueuePaused { get { lock (_gate) return _queuePaused; } }
    public bool HasInterruptedWork { get { lock (_gate) return _jobState == "Interrupted" && _jobRequest is not null; } }
    public TimeSpan Elapsed { get { lock (_gate) return _elapsed; } }
    public int CompletedToolCalls { get { lock (_gate) return _completedToolCalls; } }
    public long? ReportedTokens { get { lock (_gate) return _reportedTokens; } }
    public decimal? ReportedCostUsd { get { lock (_gate) return _reportedCost; } }
    public CodingAcceptanceResult? Acceptance { get { lock (_gate) return _acceptance; } }
    public string? Phase { get { lock (_gate) return _phase; } }

    /// <summary>Snapshot of the transcript; safe to enumerate from the UI thread.</summary>
    public IReadOnlyList<TranscriptEntry> Entries { get { lock (_gate) return _entries.ToList(); } }

    /// <summary>Raised on the background thread after the model changed; hosts marshal to their UI thread.</summary>
    public event Action? Changed;

    /// <summary>Raised when a turn finishes (completed, errored, or cancelled).</summary>
    public event Action<TurnResult>? TurnFinished;

    // Lifecycle hooks (SessionStart context waiting for the next lane message; the SessionEnd run started by Dispose).
    private string? _pendingSessionContext;
    private Task? _sessionEnd;

    /// <summary>
    /// Runs the SessionStart hooks for this session (<paramref name="source"/>: startup, resume or new). Context the hooks
    /// produce goes in front of the next lane message, once; the transcript keeps what the person typed. Warnings become
    /// notices. Never throws.
    /// </summary>
    public async Task RunSessionStartHooksAsync(string source, CancellationToken ct = default)
    {
        var result = await SwarmCodeTurnHooks.SessionStartAsync(_services, ConversationId, source, ct).ConfigureAwait(false);
        foreach (var warning in result.Warnings) AddNotice(NoticeKind.Info, warning);
        if (result.Context is null) return;
        lock (_gate) _pendingSessionContext = _pendingSessionContext is null ? result.Context : _pendingSessionContext + "\n\n" + result.Context;
    }

    /// <summary>Takes the SessionStart context waiting for the next lane message, if any.</summary>
    private string? TakePendingSessionContext()
    {
        lock (_gate)
        {
            var context = _pendingSessionContext;
            _pendingSessionContext = null;
            return context;
        }
    }

    /// <summary>Runs an informational hook off the caller's path and shows its warnings as notices.</summary>
    private void RunHookInBackground(Func<Task<IReadOnlyList<string>>> run)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var warning in await run().ConfigureAwait(false)) AddNotice(NoticeKind.Info, warning);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "A lifecycle hook for {Conversation} did not run.", ConversationId); }
        });
    }

    /// <summary>
    /// Loads the session's visible history and job state. The job file supplies state, queue
    /// and counters; entries come from the append-only presentation journal (tool cards,
    /// notices and hand-offs included). Sessions saved before the journal existed are rebuilt
    /// from the job's embedded entries or from the stores and migrated into the journal. Tool
    /// calls the provider transcript knows but the final turn's entries do not (a turn cut off
    /// by a crash) are appended as cards, and receipts that landed after the last journal write
    /// update their cards, so nothing that ran is hidden. Loading never resumes an action.
    /// </summary>
    public async Task LoadHistoryAsync(CancellationToken ct = default)
    {
        // The stores complete synchronously, so run the whole load on the thread pool: a
        // caller on the UI dispatcher must not pay for file reads, migration writes, or
        // JSON folding of a long session.
        var (entries, saved) = await Task.Run(() => LoadHistoryCoreAsync(ct), ct).ConfigureAwait(false);
        if (_services.GetService<WorkspaceSessionCatalog>() is { } catalog)
            await catalog.RestoreLegacySelectionAsync(ConversationId, saved?.ActiveLane, saved?.ActiveModel, ct).ConfigureAwait(false);
        if (_plans is not null)
        {
            try
            {
                var plan = await Task.Run(() => _plans.GetAsync(ConversationId, ct).AsTask(), ct).ConfigureAwait(false);
                lock (_gate) _plan = plan;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _logger.LogWarning("Could not load the task list for {Conversation}.", ConversationId);
            }
        }
        lock (_gate)
        {
            ResetEntries(entries);
            if (saved is not null)
            {
                _queue.Clear();
                _queue.AddRange(saved.Queue);
                _queuePaused = saved.QueuePaused || saved.Queue.Count > 0;
                ActiveLane = saved.ActiveLane;
                ActiveModel = saved.ActiveModel;
                _jobStarted = saved.StartedUtc;
                _jobRequest = saved.Request;
                _jobContinuation = saved.Continuation ?? (saved.Request is null ? null : new(saved.Request, []));
                _jobAttachments = saved.Attachments;
                _jobState = saved.State == "Running" ? "Interrupted" : saved.State;
                _elapsed = saved.Elapsed;
                _completedToolCalls = saved.CompletedToolCalls;
                _reportedTokens = saved.ReportedTokens;
                _reportedCost = saved.ReportedCostUsd;
                _acceptance = saved.Acceptance;
                if (_jobState == "Interrupted" && !_entries.OfType<NoticeEntry>().Any(n => n.Kind == NoticeKind.Info && n.Text == InterruptedNotice))
                    AddNoticeUnlocked(NoticeKind.Info, InterruptedNotice);
            }
        }
        if (saved is not null) await PersistAsync().ConfigureAwait(false);
        Changed?.Invoke();
    }

    private async Task<(List<TranscriptEntry> Entries, ConversationJob? Saved)> LoadHistoryCoreAsync(CancellationToken ct)
    {
        await DrainJournalAsync().ConfigureAwait(false);
        var transcripts = _services.GetService<IAgentSessionStore>();
        IReadOnlyList<ChatMessage> transcript = [];
        if (transcripts is not null)
        {
            try { transcript = await transcripts.LoadAsync(ConversationId, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not read the provider transcript for {Conversation}; tool cards may be incomplete.", ConversationId);
            }
        }
        var saved = _jobs?.Load(ConversationId);

        List<TranscriptEntry> entries;
        var reconcileFrom = 0;
        if (_journal is not null && _journal.Exists(ConversationId))
        {
            var contents = _journal.Load(ConversationId);
            entries = contents.Entries.ToList();
            reconcileFrom = contents.ReconcileFromMessage;
        }
        else if (saved is { Entries.Count: > 0 })
        {
            // A job saved before the presentation journal existed carries its entries inline.
            entries = saved.Entries.Select(e => e.Restore()).ToList();
            JournalRange(entries);
        }
        else
        {
            var store = _services.GetRequiredService<IConversationStore>();
            var turns = await store.LoadTurnsAsync(ConversationId, ct).ConfigureAwait(false);
            entries = TranscriptHistory.Build(turns, transcript);
            if (entries.Count > 0) JournalRange(entries);
        }

        // The job state already explains an interrupted turn; the stores-only path adds its own note.
        var reconciled = TranscriptHistory.ReconcileInterruptedTools(entries, transcript, reconcileFrom, addInterruptedNotice: saved is null);
        if (reconciled.Count > 0) JournalRange(reconciled);
        await DrainJournalAsync().ConfigureAwait(false);
        return (entries, saved);
    }

    /// <summary>
    /// Starts a turn. Returns false when one is already running. Credential values typed into
    /// the message are moved into the vault and replaced by <c>{{secret:name}}</c> references
    /// before the transcript, the session store, or any lane sees the text.
    /// </summary>
    /// <param name="origin">Who started this turn when it is not the person typing (a schedule); null for a direct request.</param>
    public bool TrySend(string text, string? laneName, CodingPermissionMode permissionMode = CodingPermissionMode.Auto, bool? allowHandOff = null, string? modelOverride = null, bool isContinuation = false, IReadOnlyList<CodingAttachment>? attachments = null, TurnOrigin? origin = null)
        => TrySendCore(text, laneName, permissionMode, allowHandOff, modelOverride, isContinuation, attachments, origin, null);

    private bool TrySendCore(string text, string? laneName, CodingPermissionMode permissionMode, bool? allowHandOff, string? modelOverride, bool isContinuation, IReadOnlyList<CodingAttachment>? attachments, TurnOrigin? origin, CodingContinuationContext? continuation)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        lock (_gate)
        {
            // An undo writing files must finish first; the caller queues the message instead.
            if (_disposed || _running || _undoing) return false;
            _turnCts?.Dispose();
            _turnCts = new CancellationTokenSource();
            var ct = _turnCts.Token;
            _running = true;
            ++_turnSequence;
            _jobRequest = null;
            _turnTask = Task.Run(() => RunTurnAsync(text, laneName, permissionMode, allowHandOff, modelOverride, isContinuation, attachments?.ToArray() ?? [], ct, origin, continuation), CancellationToken.None);
            return true;
        }
    }

    public void Cancel()
    {
        lock (_gate) { ++_stopGeneration; _resumeSteering = false; _queuePaused = true; _turnCts?.Cancel(); }
        // A stopped turn cannot take an answer: close its question cards rather than leave them waiting.
        DeclinePendingQuestions();
    }

    /// <summary>Protects and queues a follow-up for this session, independent of the visible page.</summary>
    public async Task<bool> EnqueueAsync(string text, string? laneName, CodingPermissionMode permissionMode = CodingPermissionMode.Auto, bool? allowHandOff = null, string? modelOverride = null, IReadOnlyList<CodingAttachment>? attachments = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        lock (_gate) { if (_disposed) return false; }
        string message;
        try { message = await IntakeSecretsAsync(text, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception)
        {
            AddNotice(NoticeKind.Error, "Could not protect credentials. Your follow-up was not queued. Retry or save credentials in Settings.");
            Changed?.Invoke();
            return false;
        }
        await _queueControlGate.WaitAsync().ConfigureAwait(false);
        lock (_gate) _queueMutations++;
        try
        {
            QueuedTurn queued;
            lock (_gate)
            {
                if (_disposed) return false;
                queued = new QueuedTurn(message, laneName, permissionMode != CodingPermissionMode.Auto, allowHandOff) { PermissionMode = CodingPermissionPolicy.Name(permissionMode), ModelOverride = modelOverride, Attachments = attachments?.ToArray() ?? [] };
                _queue.Add(queued);
            }
            try { await PersistAsync().ConfigureAwait(false); }
            catch (Exception)
            {
                lock (_gate) { _queue.Remove(queued); _queuePaused = true; }
                AddNotice(NoticeKind.Error, "Could not save your follow-up. It was not queued. Check the data folder and retry.");
                Changed?.Invoke();
                return false;
            }
            return true;
        }
        finally
        {
            lock (_gate) _queueMutations--;
            _queueControlGate.Release();
            Changed?.Invoke();
            StartNextQueued();
        }
    }

    public Task RemoveQueued(int index) => RemoveQueuedAsync(index);

    public async Task RemoveQueuedAsync(int index)
    {
        string? id;
        lock (_gate) id = index >= 0 && index < _queue.Count ? _queue[index].Id : null;
        if (id is not null) await RemoveQueuedAsync(id).ConfigureAwait(false);
    }

    public Task<bool> RemoveQueuedAsync(string id) => ChangeQueuedAsync(id, null);

    public async Task<bool> UpdateQueuedAsync(string id, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string protectedText;
        try { protectedText = await IntakeSecretsAsync(text, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { AddNotice(NoticeKind.Error, "Could not protect the edited prompt. The queued message was not changed."); Changed?.Invoke(); return false; }
        return await ChangeQueuedAsync(id, protectedText).ConfigureAwait(false);
    }

    private async Task<bool> ChangeQueuedAsync(string id, string? replacement)
    {
        await _queueControlGate.WaitAsync().ConfigureAwait(false);
        QueuedTurn? original = null;
        var index = -1;
        lock (_gate) _queueMutations++;
        try
        {
            lock (_gate)
            {
                index = _queue.FindIndex(q => q.Id == id);
                if (_disposed || index < 0) return false;
                original = _queue[index];
                if (replacement is null) _queue.RemoveAt(index);
                else _queue[index] = original with { Text = replacement };
            }
            await PersistAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            lock (_gate)
            {
                if (original is not null)
                {
                    _queue.RemoveAll(q => q.Id == id);
                    _queue.Insert(Math.Min(index, _queue.Count), original);
                }
                _queuePaused = true;
            }
            AddNotice(NoticeKind.Error, "Could not save the queue change. The original prompt is still queued and paused.");
            return false;
        }
        finally
        {
            lock (_gate) _queueMutations--;
            _queueControlGate.Release();
            Changed?.Invoke();
            StartNextQueued();
        }
    }

    public Task<bool> SteerAsync(string text, string? laneName, CodingPermissionMode permissionMode = CodingPermissionMode.Auto, bool? allowHandOff = null, string? modelOverride = null, IReadOnlyList<CodingAttachment>? attachments = null)
        => SteerCoreAsync(text, laneName, permissionMode, allowHandOff, modelOverride, null, attachments);

    public async Task<bool> SteerQueuedAsync(string id)
    {
        QueuedTurn? item;
        lock (_gate) item = _queue.FirstOrDefault(q => q.Id == id);
        return item is not null && await SteerCoreAsync(item.Text, item.Lane, item.Mode, item.AllowHandOff, item.ModelOverride, id, item.Attachments).ConfigureAwait(false);
    }

    private async Task<bool> SteerCoreAsync(string text, string? laneName, CodingPermissionMode permissionMode, bool? allowHandOff, string? modelOverride, string? queuedId, IReadOnlyList<CodingAttachment>? attachments)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        long turnSequence;
        lock (_gate) { if (_disposed || !_running || _jobRequest is null) return false; turnSequence = _turnSequence; }
        string correction;
        try { correction = await IntakeSecretsAsync(text, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { AddNotice(NoticeKind.Error, "Could not protect the correction. Work was not interrupted."); Changed?.Invoke(); return false; }
        await _queueControlGate.WaitAsync().ConfigureAwait(false);
        QueuedTurn? original = null;
        QueuedTurn? steering = null;
        var originalIndex = -1;
        var wasPaused = false;
        var stopGeneration = 0;
        lock (_gate) _queueMutations++;
        try
        {
            lock (_gate)
            {
                if (_disposed || !_running || _jobRequest is null || _resumeSteering || _turnSequence != turnSequence) return false;
                stopGeneration = _stopGeneration;
                if (queuedId is not null)
                {
                    originalIndex = _queue.FindIndex(q => q.Id == queuedId);
                    if (originalIndex < 0) return false;
                    original = _queue[originalIndex];
                    correction = original.Text;
                    laneName = original.Lane;
                    modelOverride = original.ModelOverride;
                    permissionMode = original.Mode;
                    allowHandOff = original.AllowHandOff;
                    _queue.RemoveAt(originalIndex);
                }
                wasPaused = _queuePaused;
                _queuePaused = true;
                steering = new QueuedTurn(correction, laneName, permissionMode != CodingPermissionMode.Auto, allowHandOff)
                    { PermissionMode = CodingPermissionPolicy.Name(permissionMode), ModelOverride = modelOverride, IsSteering = true, OriginalTask = _jobRequest,
                        Continuation = (_jobContinuation ?? new(_jobRequest, [])).WithUpdate(correction),
                        Attachments = _jobAttachments.Concat(original?.Attachments ?? attachments ?? []).DistinctBy(a => a.Id).ToArray() };
                _queue.Insert(0, steering);
            }
            // The correction is durable before stopping the worker. Resume never overlaps the old process.
            await PersistAsync().ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed || stopGeneration != _stopGeneration) return false;
                _resumeSteering = true;
                _phase = "stopping before applying correction";
                _turnCts?.Cancel();
            }
            AddNotice(NoticeKind.Info, "Continuing the current task with your update after the active turn stops. Saved tool results will be kept.");
            return true;
        }
        catch (Exception)
        {
            lock (_gate)
            {
                if (steering is not null) _queue.Remove(steering);
                if (original is not null) _queue.Insert(Math.Min(originalIndex, _queue.Count), original);
                _queuePaused = wasPaused;
            }
            AddNotice(NoticeKind.Error, "Could not save the correction. Work was not interrupted.");
            return false;
        }
        finally
        {
            lock (_gate) _queueMutations--;
            _queueControlGate.Release();
            Changed?.Invoke();
            StartNextQueued();
        }
    }

    public async Task ResumeQueueAsync()
    {
        lock (_gate) { if (_disposed || _running) return; _queuePaused = false; }
        try { await PersistAsync().ConfigureAwait(false); }
        catch (Exception)
        {
            lock (_gate) _queuePaused = true;
            AddNotice(NoticeKind.Error, "Could not save the resumed queue. No queued work was started. Check the data folder and retry.");
            Changed?.Invoke();
            return;
        }
        StartNextQueued();
    }

    public bool ContinueInterruptedWork(string? laneName, CodingPermissionMode permissionMode, bool? allowHandOff, string? modelOverride = null)
    {
        string? request;
        lock (_gate) { if (_jobState != "Interrupted" || _disposed || _running) return false; request = _jobRequest; }
        if (request is null) return false;
        var context = _jobContinuation ?? new(request, []);
        return TrySendCore(CodingContinuationPrompt.Resume(context), laneName, permissionMode, allowHandOff, modelOverride, true, _jobAttachments, null, context);
    }

    private void StartNextQueued()
    {
        lock (_gate)
        {
            if (_disposed || _running || _queueMutations > 0 || _queue.Count == 0) return;
            if (_queuePaused && !(_resumeSteering && _queue[0].IsSteering)) return;
            var next = _queue[0];
            _queue.RemoveAt(0);
            _resumeSteering = false;
            var context = next.IsSteering ? next.Continuation ?? new CodingContinuationContext(next.OriginalTask ?? "Inspect the saved conversation.", [next.Text]) : null;
            var message = context is null ? next.Text : CodingContinuationPrompt.Resume(context);
            TrySendCore(message, next.Lane, next.Mode, next.AllowHandOff, next.ModelOverride, next.IsSteering, next.Attachments, null, context);
        }
        Changed?.Invoke();
    }

    private async Task FinishAsync(TurnResult result)
    {
        lock (_gate)
        {
            _jobState = result.Cancelled ? "Stopped" : result.Error is not null ? "Failed" : result.Completed ? "Completed" : "Incomplete";
            if (result.Cancelled || result.Error is not null || !result.Completed) _queuePaused = true;
        }
        await PersistSafelyAsync().ConfigureAwait(false);
        _running = false;
        Changed?.Invoke();
        TurnFinished?.Invoke(result);
        if (_resumeSteering || (!result.Cancelled && result.Error is null)) StartNextQueued();
    }

    /// <summary>
    /// Answers a pending permission request. <see cref="PermissionGrantScope.Conversation"/> asks the broker
    /// to remember the grant for the rest of this conversation; the chosen scope is journalled so a
    /// reopened session shows it. SwarmCode keeps no grant store beyond the conversation, so Always
    /// is treated as Conversation.
    /// </summary>
    public void Respond(string requestId, bool approved, PermissionGrantScope scope = PermissionGrantScope.Once)
    {
        if (scope == PermissionGrantScope.Always) scope = PermissionGrantScope.Conversation;
        if (!approved) scope = PermissionGrantScope.Once;
        lock (_gate)
        {
            if (_entries.OfType<PermissionEntry>().FirstOrDefault(p => p.RequestId == requestId) is { } entry)
            {
                entry.Approved = approved;
                entry.Scope = approved ? scope : null;
                Journal(entry);
            }
        }
        _broker?.Respond(requestId, approved, scope);
        Changed?.Invoke();
    }

    /// <summary>
    /// Answers a pending question card (<c>user.ask</c>): the entry shows the answer read-only, the finalized entry is
    /// journalled, and the waiting tool returns the answer. Empty text and no selection count as skipping.
    /// </summary>
    public bool AnswerQuestion(string requestId, string? text, IReadOnlyList<string>? selected)
    {
        var answer = UserAnswer.From(text, selected);
        lock (_gate)
        {
            if (_entries.OfType<QuestionEntry>().FirstOrDefault(q => q.RequestId == requestId) is { IsPending: true } entry)
            {
                entry.Answered = answer.Answered;
                entry.Answer = answer.Text;
                entry.Selected = answer.Selected;
                Journal(entry);
            }
        }
        var delivered = _questions?.Answer(requestId, answer) ?? false;
        Changed?.Invoke();
        return delivered;
    }

    /// <summary>The person skips a pending question; the tool tells the model to decide itself.</summary>
    public bool DeclineQuestion(string requestId) => AnswerQuestion(requestId, null, null);

    /// <summary>Closes every pending question card of this conversation as skipped (turn stopped, transcript cleared, runner disposed).</summary>
    private void DeclinePendingQuestions()
    {
        List<QuestionEntry> pending;
        lock (_gate)
        {
            pending = _entries.OfType<QuestionEntry>().Where(q => q.IsPending).ToList();
            foreach (var entry in pending) { entry.Answered = false; Journal(entry); }
        }
        _questions?.ClearConversation(ConversationId);
        if (pending.Count > 0) Changed?.Invoke();
    }

    private void OnQuestionRequested(PendingUserQuestion question)
    {
        // One broker serves every open session of a workspace: a question belongs to the conversation that asked.
        if (!string.Equals(question.ConversationId, ConversationId, StringComparison.Ordinal)) return;
        lock (_gate)
        {
            AddEntry(new QuestionEntry
            {
                RequestId = question.RequestId,
                Question = question.Request.Question,
                Context = question.Request.Context,
                Options = question.Request.Options.Select(o => new QuestionOption(o.Label, o.Description)).ToList(),
                AllowMultiple = question.Request.AllowMultiple,
                AllowFreeText = question.Request.AllowFreeText,
            });
        }
        Changed?.Invoke();
    }

    /// <summary>Withdraws the conversation grant an approval created; the next matching request asks again.</summary>
    public bool RevokePermission(string requestId)
    {
        var revoked = _broker?.RevokeGrant(requestId) ?? false;
        lock (_gate)
        {
            if (_entries.OfType<PermissionEntry>().FirstOrDefault(p => p.RequestId == requestId) is { Scope: PermissionGrantScope.Conversation, GrantRevoked: false } entry)
            {
                entry.GrantRevoked = true;
                Journal(entry);
                revoked = true;
            }
        }
        Changed?.Invoke();
        return revoked;
    }

    /// <summary>
    /// Empties the visible transcript. The stores keep their history; the journal records the
    /// clear together with the provider transcript's length, so reopening neither replays
    /// the cleared entries nor rebuilds cards for tool calls that happened before the clear.
    /// </summary>
    public void ClearTranscript()
    {
        // A cleared conversation starts over: grants given for it no longer apply, and a question nobody can see any more is skipped.
        _broker?.ClearConversation(ConversationId);
        _questions?.ClearConversation(ConversationId);
        lock (_gate)
        {
            ResetEntries([]);
            _clearedThroughEvent = Interlocked.Read(ref _producedEvents);
            // Text after the clear starts a new visible bubble instead of extending a cleared one.
            _streaming = null;
        }
        var transcripts = _services.GetService<IAgentSessionStore>();
        EnqueueJournal(async () =>
        {
            var count = 0;
            if (transcripts is not null)
            {
                // A watermark of zero only means reconciliation may re-add the final turn's
                // cards; the clear marker itself must land whatever the store does.
                try { count = (await transcripts.LoadAsync(ConversationId).ConfigureAwait(false)).Count; }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not read the transcript length for the clear marker of {Conversation}.", ConversationId); }
            }
            _journal!.AppendClear(ConversationId, count);
        }, "the transcript clear");
        Changed?.Invoke();
    }

    private async Task RunTurnAsync(string text, string? laneName, CodingPermissionMode permissionMode, bool? allowHandOff, string? modelOverride, bool isContinuation, IReadOnlyList<CodingAttachment> attachments, CancellationToken ct, TurnOrigin? origin = null, CodingContinuationContext? continuation = null)
    {
        var result = new TurnResult();
        var started = DateTime.UtcNow;
        // What the turn reports that may be SwarmCode's own defect or a missing capability; classified after the turn.
        var diagnostics = new List<Improvement.TurnDiagnostic>();
        string message;
        try
        {
            message = await IntakeSecretsAsync(text, ct, overwriteExisting: !isContinuation).ConfigureAwait(false);
            if (isContinuation)
            {
                lock (_gate) continuation ??= _jobContinuation ?? new(_jobRequest ?? message, []);
                // Legacy jobs may predate credential intake. Protect every durable intent field too, and never
                // let an old continuation replace a credential the person has updated since then.
                var original = await IntakeSecretsAsync(continuation.OriginalRequest, ct, overwriteExisting: false).ConfigureAwait(false);
                var updates = new List<string>();
                foreach (var update in continuation.Updates)
                    updates.Add(await IntakeSecretsAsync(update, ct, overwriteExisting: false).ConfigureAwait(false));
                continuation = new(original, updates);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled before the text was scrubbed: never let the raw text reach the transcript or a lane.
            result.Cancelled = true;
            AddNotice(NoticeKind.Cancelled, "Stopped.");
            await FinishAsync(result).ConfigureAwait(false);
            return;
        }
        catch (Exception)
        {
            // Vault/provider exceptions may contain the value. Do not log them or fall back to raw text.
            result.Error = "Could not protect credentials. Your message was not sent. Retry or save credentials in Settings.";
            _logger.LogWarning("Secret intake failed for {Conversation}; message was not sent.", ConversationId);
            AddNotice(NoticeKind.Error, result.Error);
            await FinishAsync(result).ConfigureAwait(false);
            return;
        }

        // UserPromptSubmit hooks see only the protected text. A block keeps the message from every lane; added context
        // goes to the lane, while the transcript shows what the person typed.
        var laneMessage = message;
        if (!isContinuation)
        {
            PromptHookResult prompt;
            try { prompt = await SwarmCodeTurnHooks.BeforePromptAsync(_services, ConversationId, message, ct).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                AddNotice(NoticeKind.Cancelled, "Stopped.");
                await FinishAsync(result).ConfigureAwait(false);
                return;
            }
            foreach (var warning in prompt.Warnings) AddNotice(NoticeKind.Info, warning);
            if (prompt.Blocked)
            {
                result.Error = "A UserPromptSubmit hook blocked this message: " + prompt.Reason;
                AddNotice(NoticeKind.Error, result.Error);
                await FinishAsync(result).ConfigureAwait(false);
                return;
            }
            laneMessage = prompt.LaneMessage;

            // @path mentions: the lane gets the referenced files' content as evidence; the transcript keeps the typed text.
            var mentions = await ExpandFileMentionsAsync(laneMessage, ct).ConfigureAwait(false);
            laneMessage = mentions.LaneMessage;
            foreach (var note in mentions.Notes) AddNotice(NoticeKind.Info, note);
        }
        // SessionStart hook context goes in front of the first lane message after the session opened, once.
        laneMessage = CodingHooks.WithSessionContext(laneMessage, TakePendingSessionContext());

        lock (_gate)
        {
            var userEntry = new UserEntry { Text = message, IsContinuation = isContinuation, Attachments = attachments };
            AddEntry(userEntry);
            Journal(userEntry);
            _jobContinuation = isContinuation ? continuation ?? _jobContinuation ?? new(_jobRequest ?? message, []) : new(message, []);
            _jobRequest = _jobContinuation.OriginalRequest;
            _jobAttachments = attachments;
            _jobStarted = started;
            _jobState = "Running";
            _elapsed = TimeSpan.Zero;
            _completedToolCalls = 0;
            _reportedTokens = null;
            _reportedCost = null;
            _acceptance = null;
            _phase = "working";
        }
        try { await PersistAsync().ConfigureAwait(false); }
        catch (Exception)
        {
            result.Error = "Could not save the job. No agent action was started. Check the data folder and retry.";
            AddNotice(NoticeKind.Error, result.Error);
            await FinishAsync(result).ConfigureAwait(false);
            return;
        }
        Changed?.Invoke();

        // Pre-turn working-tree checkpoint, so the turn's file edits can be undone whichever lane makes them. Bounded
        // and fail-soft: a slow or failing snapshot only means this turn has no undo.
        var checkpoint = await BeginCheckpointAsync(ct).ConfigureAwait(false);
        // A scheduled run has no one waiting on it: a question asked during it is answered "no one can answer" at once.
        using var unattendedQuestions = origin is { Unattended: true } or { Authority: TurnOrigin.ScheduleAuthority } ? _questions?.Unattended(ConversationId) : null;

        // Session titles are also persisted user text: only the protected message may become a title.
        var temporary = false;
        if (_services.GetService<WorkspaceSessionCatalog>() is { } catalog)
        {
            try { await catalog.TouchAsync(ConversationId, laneName, titleHint: message, ct: ct).ConfigureAwait(false); }
            catch (Exception) { _logger.LogWarning("Could not update session metadata for {Conversation}.", ConversationId); }
            try
            {
                var session = await catalog.GetAsync(ConversationId, ct).ConfigureAwait(false);
                temporary = session?.Temporary == true;
                // An agent's session never runs with less caution than the session that started it.
                permissionMode = SwarmCodePermissions.ForSession(permissionMode, session);
            }
            catch (Exception) when (!ct.IsCancellationRequested) { temporary = true; } // unknown: fail closed, memory off for this turn
        }

        // Personal memory for this turn: the person's own (credential-scrubbed) message is the only text memory may learn
        // from or that "remember" may cite; a continuation's harness-written prompt is not the person's words.
        var memoryTurns = _services.GetService<SwarmCodeMemoryTurns>();
        var memoryTurn = memoryTurns?.Create(ConversationId, temporary, isContinuation ? null : message, Guid.NewGuid().ToString("N"));

        var channel = Channel.CreateBounded<(long Sequence, TurnEvent Event)>(new BoundedChannelOptions(512) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var orchestrator = _services.GetRequiredService<ICodingTurnOrchestrator>();
        bool planMode, compactFirst;
        lock (_gate) { planMode = _planMode; _planProposed = false; compactFirst = _compactNextTurn; _compactNextTurn = false; }
        // The person's rules plus the repository's own (deny always; allow once approved), read fresh for this turn.
        var permissionPolicy = await SwarmCodePermissions.PolicyAsync(permissionMode, _services).ConfigureAwait(false);
        var request = SwarmCodeTurnPolicy.Apply(new CodingTurnRequest
        {
            PlanMode = planMode,
            CompactWorkingContextFirst = compactFirst,
            ConversationId = ConversationId,
            UserMessage = laneMessage,
            // Hook context and @file contents travel with the lane message; the person's authenticated words stay the typed text.
            AuthenticatedOriginalUserMessage = isContinuation ? _jobContinuation!.AuthenticatedUserRequest
                : !ReferenceEquals(laneMessage, message) && laneMessage != message ? message : null,
            Attachments = attachments,
            LaneName = laneName,
            ModelOverride = modelOverride,
            IsContinuation = isContinuation,
            AllowHandOff = allowHandOff,
            RememberModelResponses = !temporary,
            UnattendedAgentTurn = origin?.Unattended == true,
            Metadata = origin?.Metadata,
        }, permissionPolicy, origin?.Authority ?? "swarmcode-app");

        var decisionCoordinator = _services.GetService<DecisionSessionCoordinator>();
        long? selectionRevision = null;
        var reassessModel = true;
        var producer = Task.Run(async () =>
        {
            // Ambient for everything the orchestrator runs on this flow: recall injectors, memory tools, native host context.
            using var memoryScope = memoryTurn is null ? null : memoryTurns!.Begin(memoryTurn);
            try
            {
                if (decisionCoordinator is not null)
                {
                    var selection = await decisionCoordinator.PrepareAsync(ConversationId, message, laneName, modelOverride,
                        orchestrator.Lanes, isContinuation, ct).ConfigureAwait(false);
                    selectionRevision = selection.Revision;
                    // Picker choices select the first attempt; the person's hand-off preference still governs retries.
                    request = request with { LaneName = selection.Lane, ModelOverride = selection.Model };
                    if (selection.Notice is { } notice) AddNotice(NoticeKind.Info, notice);
                }
                await foreach (var evt in orchestrator.RunAsync(request, ct).ConfigureAwait(false))
                    await channel.Writer.WriteAsync((Interlocked.Increment(ref _producedEvents), evt), CancellationToken.None).ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (Exception ex) { channel.Writer.TryComplete(ex); }
        }, CancellationToken.None);

        try
        {
            await foreach (var (sequence, evt) in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                Apply(evt, result, sequence);
                CollectDiagnostic(evt, diagnostics);
                if (evt is ToolCallCompleted { IsError: true } recovery &&
                    SwarmCore.Coding.Routing.RepeatedToolCallGuard.IsRecoveryBlock(recovery.Result) &&
                    diagnostics.Count(d => d.Kind == Improvement.TurnDiagnosticKind.ToolError && d.ToolName == recovery.ToolName &&
                        SwarmCore.Coding.Routing.RepeatedToolCallGuard.IsRecoveryBlock(d.Message)) == 1)
                    await Improvement.SelfImprovementCapture.ToolRecoveryAsync(_services, ConversationId, recovery.ToolName, origin).ConfigureAwait(false);
                if (evt is CodingLanesExhausted { ReassessModel: false }) reassessModel = false;
                if (decisionCoordinator is not null && evt is CodingLaneSelected selected)
                    selectionRevision = await decisionCoordinator.ObserveAsync(ConversationId, selectionRevision,
                        selected.Lane, selected.Attempt > 1, ct).ConfigureAwait(false);
                // Receipt and lane boundaries are durable before another event is displayed.
                // Streaming text is included in the next boundary, avoiding a disk write per token.
                if (evt is not (TextChunk or ReasoningChunk or ToolCallInputDelta or CodingTurnProgress))
                    await PersistAsync().ConfigureAwait(false);
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
            AddNotice(NoticeKind.Cancelled, "Stopped.");
        }
        catch (Exception ex)
        {
            // A failed journal must stop dispatch. Drain the bounded relay so its producer
            // can exit even when cancellation races with already produced events.
            lock (_gate) _turnCts?.Cancel();
            try { await foreach (var _ in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false)) { } }
            catch (Exception) { }
            _logger.LogError(ex, "Turn failed for {Conversation}", ConversationId);
            result.Error ??= ex.Message;
            diagnostics.Add(new Improvement.TurnDiagnostic(Improvement.TurnDiagnosticKind.Exception, ex.Message, Exception: ex));
            AddNotice(NoticeKind.Error, ex.Message, ex.GetType().Name);
        }
        finally
        {
            await producer.ConfigureAwait(false);
            if (ct.IsCancellationRequested && !result.Cancelled)
            {
                result.Cancelled = true;
                AddNotice(NoticeKind.Cancelled, "Stopped.");
            }
            lock (_gate)
            {
                _elapsed = DateTime.UtcNow - started;
                if (_streaming is not null) { _streaming.IsStreaming = false; _streaming.Duration ??= DateTime.UtcNow - started; Journal(_streaming); _streaming = null; }
                foreach (var open in _openTools.Values) { open.State = ToolCallState.Unknown; open.Result ??= "Outcome unknown: turn ended without a saved result. Inspect the workspace before retrying."; Journal(open); }
                _openTools.Clear();
            }
            // A question the turn left unanswered can never be answered now.
            DeclinePendingQuestions();
            // Recorded for cancelled and failed turns too: whatever they changed on disk can be undone.
            if (checkpoint is not null) await CompleteCheckpointAsync(checkpoint, result).ConfigureAwait(false);
            // Learn from the person's own words after a finished turn, in the background (never for a temporary session).
            if (memoryTurn is { Incognito: false, AuthenticatedUserMessage: not null } && result.Completed && !result.Cancelled && result.Error is null)
                _services.GetService<SwarmCodeMemoryCapture>()?.Schedule(memoryTurn);
            if (planMode && result.Completed && !result.Cancelled && result.Error is null)
                lock (_gate) _planProposed = _planMode;
            if (decisionCoordinator is not null && reassessModel && !result.Cancelled && (!result.Completed || result.Error is not null))
            {
                try { await decisionCoordinator.MarkFailedAsync(ConversationId, selectionRevision, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception) { AddNotice(NoticeKind.Info, "Could not save the model reassessment request."); }
            }
            foreach (var warning in await SwarmCodeTurnHooks.AfterTurnAsync(_services, ConversationId, result.Completed, result.Cancelled, result.Error).ConfigureAwait(false))
                AddNotice(NoticeKind.Info, warning);
            // Self-improvement: product defects and capability gaps this turn surfaced go to the backlog (never for a cycle's own turn).
            Improvement.SelfImprovementCapture.AfterTurn(_services, ConversationId, diagnostics, origin, isContinuation ? null : message);
            await FinishAsync(result).ConfigureAwait(false);
        }
    }

    /// <summary>Keeps the events the self-improvement classifier reads after the turn: lane errors, failed tools, declines.</summary>
    private static void CollectDiagnostic(TurnEvent evt, List<Improvement.TurnDiagnostic> diagnostics)
    {
        switch (evt)
        {
            case TurnErrored errored:
                diagnostics.Add(new(Improvement.TurnDiagnosticKind.ErrorText, errored.Message, errored.Details));
                break;
            case ToolCallCompleted { IsError: true } failed:
                diagnostics.Add(new(Improvement.TurnDiagnosticKind.ToolError, failed.Result, ToolName: failed.ToolName));
                break;
            case ToolCallStarted started when Improvement.SelfImprovementCapture.DeclineReason(started.ToolName, started.Input) is { } reason:
                diagnostics.Add(new(Improvement.TurnDiagnosticKind.Decline, reason));
                break;
        }
    }

    // ---- turn checkpoints and undo -------------------------------------------

    private async Task<TurnCheckpoint?> BeginCheckpointAsync(CancellationToken ct)
    {
        if (_services.GetService<TurnCheckpointService>() is not { } checkpoints) return null;
        try
        {
            CodingWorkspace? workspace;
            using (var scope = _services.CreateScope())
                workspace = scope.ServiceProvider.GetService<ICodingWorkspaceResolver>() is { } resolver
                    ? await resolver.ResolveAsync(ConversationId, ct).ConfigureAwait(false)
                    : null;
            if (workspace is null) return null;
            var start = await checkpoints.BeginAsync(workspace.FullRoot, ConversationId, ct).ConfigureAwait(false);
            if (start.Checkpoint is null && !start.NotARepository && start.UnavailableReason is { } reason)
            {
                // Once per reason and session: a large repository should not add the same note to every turn.
                bool show;
                lock (_gate) { show = _checkpointNotice != reason; _checkpointNotice = reason; }
                if (show) AddNotice(NoticeKind.Info, "This turn cannot be undone: " + reason);
            }
            return start.Checkpoint;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not take the turn checkpoint for {Conversation}.", ConversationId);
            return null;
        }
    }

    private async Task CompleteCheckpointAsync(TurnCheckpoint checkpoint, TurnResult result)
    {
        try
        {
            var service = _services.GetRequiredService<TurnCheckpointService>();
            var record = await service.CompleteAsync(checkpoint, CancellationToken.None).ConfigureAwait(false);
            if (record.UnavailableReason is { } reason)
            {
                AddNotice(NoticeKind.Info, "This turn cannot be undone: " + reason);
                return;
            }
            if (record.Changes.Count == 0) return;
            result.WorkspaceChanged = true;
            lock (_gate)
            {
                var entry = new TurnChangesEntry
                {
                    CheckpointId = record.CheckpointId, RepositoryRoot = record.RepositoryRoot, Files = record.Changes, HeadMoved = record.HeadMoved,
                };
                AddEntry(entry);
                Journal(entry);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record the turn checkpoint for {Conversation}.", ConversationId);
        }
    }

    /// <summary>The newest turn whose file changes can still be undone (for <c>/undo</c>).</summary>
    public TurnChangesEntry? LastUndoableTurn()
    {
        lock (_gate) return _entries.OfType<TurnChangesEntry>().LastOrDefault(e => !e.Undone);
    }

    /// <summary>What undoing <paramref name="entryId"/> would restore now, with any conflicts; changes nothing.</summary>
    public async Task<TurnUndoResult> PreviewUndoAsync(string entryId, CancellationToken ct = default)
    {
        if (FindUndoTarget(entryId) is not { } entry) return UndoUnavailable("That turn has no recorded file changes.");
        if (_services.GetService<TurnCheckpointService>() is not { } service) return UndoUnavailable("Undo is not available in this host.");
        return await Task.Run(() => service.PreviewUndoAsync(entry.RepositoryRoot, ConversationId, entry.CheckpointId, ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Restores the files the turn changed to their content before it. Refused while a turn runs. The outcome is added to
    /// the transcript as a notice; a successful undo marks the turn's entry as undone.
    /// </summary>
    public async Task<TurnUndoResult> UndoAsync(string entryId, TurnUndoConflictMode mode, CancellationToken ct = default)
    {
        if (FindUndoTarget(entryId) is not { } entry) return UndoUnavailable("That turn has no recorded file changes.");
        if (_services.GetService<TurnCheckpointService>() is not { } service) return UndoUnavailable("Undo is not available in this host.");
        lock (_gate)
        {
            if (_disposed) return UndoUnavailable("This session is closed.");
            if (_running) return UndoUnavailable("Stop the current turn before undoing file changes.");
            if (_undoing) return UndoUnavailable("An undo is already running.");
            _undoing = true;
        }
        TurnUndoResult outcome;
        try
        {
            outcome = await Task.Run(() => service.UndoAsync(entry.RepositoryRoot, ConversationId, entry.CheckpointId, mode, ct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { lock (_gate) _undoing = false; throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Undo failed for {Conversation}.", ConversationId);
            outcome = new TurnUndoResult { Status = TurnUndoStatus.Failed, Summary = "Undo failed: " + ex.Message };
        }
        lock (_gate)
        {
            _undoing = false;
            // A partial undo (files left as they are) keeps the action, so "undo anyway" stays possible.
            if (outcome.Status is TurnUndoStatus.Undone or TurnUndoStatus.NothingToUndo && outcome.Kept.Count == 0)
            {
                entry.Undone = true;
                Journal(entry);
            }
            if (outcome.Status != TurnUndoStatus.ConflictsNeedDecision)
                AddNoticeUnlocked(outcome.Status == TurnUndoStatus.Failed ? NoticeKind.Error : NoticeKind.Info, outcome.Summary, UndoDetails(outcome));
        }
        Changed?.Invoke();
        StartNextQueued();
        return outcome;
    }

    private TurnChangesEntry? FindUndoTarget(string entryId)
    {
        lock (_gate) return _entries.OfType<TurnChangesEntry>().FirstOrDefault(e => e.Id == entryId);
    }

    private static TurnUndoResult UndoUnavailable(string summary) => new() { Status = TurnUndoStatus.Unavailable, Summary = summary };

    /// <summary>The file lists behind an undo notice.</summary>
    public static string? UndoDetails(TurnUndoResult outcome)
    {
        var text = new System.Text.StringBuilder();
        void Section(string title, IReadOnlyList<string> paths)
        {
            if (paths.Count == 0) return;
            if (text.Length > 0) text.Append('\n');
            text.Append(title).Append(":\n");
            foreach (var path in paths) text.Append("  ").Append(path).Append('\n');
        }
        Section("Restored", outcome.Restored);
        Section("Deleted (created by the turn)", outcome.Deleted);
        Section("Left as they are", outcome.Kept);
        if (outcome.BackupRef is { } backup)
            text.Append($"\nThe replaced content is saved in git as {backup} (git checkout {backup} -- <path> brings a file back).");
        return text.Length == 0 ? null : text.ToString().TrimEnd();
    }

    /// <summary>Moves typed credential values into the vault; returns the text the lane may see.</summary>
    private async Task<string> IntakeSecretsAsync(string text, CancellationToken ct, bool overwriteExisting = true)
    {
        if (_secrets is null) return text;
        var outcome = await _secrets.ScrubAsync(text, ct, overwriteExisting).ConfigureAwait(false);
        if (outcome.Message is not null) AddNotice(NoticeKind.Secret, outcome.Message);
        return outcome.Text;
    }

    private void Apply(TurnEvent evt, TurnResult result, long sequence = long.MaxValue)
    {
        lock (_gate)
        {
            _applyingClearedEvent = sequence <= _clearedThroughEvent;
            try { ApplyUnlocked(evt, result); }
            finally
            {
                if (_applyingClearedEvent && _streaming is not null && !_live.Contains(_streaming)) _streaming = null;
                _applyingClearedEvent = false;
            }
        }
    }

    private void ApplyUnlocked(TurnEvent evt, TurnResult result)
    {
        {
            switch (evt)
            {
                case CodingLaneSelected sel:
                    ActiveLane = sel.Lane.Name;
                    ActiveModel = sel.Lane.Model;
                    result.LanesUsed.Add(sel.Lane.Name);
                    AddNoticeUnlocked(NoticeKind.LaneSelected, sel.Attempt == 1 ? $"{sel.Lane.SelectionLabel} ({sel.Lane.Route})" : $"Continuing on {sel.Lane.SelectionLabel} ({sel.Lane.Route})");
                    CloseStreaming();
                    break;
                case CodingLaneNotice laneNotice:
                    AddNoticeUnlocked(NoticeKind.Info, $"{laneNotice.Lane}: {laneNotice.Message}", laneNotice.Details);
                    break;
                case CodingLaneSkipped skipped:
                    AddNoticeUnlocked(NoticeKind.Info, $"Skipping {skipped.Lane.SelectionLabel}: {skipped.Reason}");
                    break;
                case CodingLaneHandOff ho:
                    AddNoticeUnlocked(NoticeKind.HandOff, $"Switching from {ho.From.SelectionLabel} to {ho.To.SelectionLabel}.", ho.Reason);
                    CloseStreaming();
                    break;
                case CodingLanesExhausted ex:
                    result.Error = ex.Reason;
                    AddNoticeUnlocked(NoticeKind.LanesExhausted, ex.Reason);
                    break;
                case TextChunk chunk:
                    Streaming().AppendText(chunk.Delta);
                    break;
                case ReasoningChunk reasoning:
                    Streaming().AppendReasoning(reasoning.Delta);
                    break;
                case ToolCallStarted started:
                    var tool = new ToolCallEntry { ToolCallId = started.ToolCallId, ToolName = started.ToolName, InputJson = started.Input.ToJsonString() };
                    _openTools[started.ToolCallId] = tool;
                    AddEntry(tool);
                    CloseStreaming(); // text after a tool call starts a new bubble
                    result.ToolCalls++;
                    break;
                case ToolCallInputDelta delta when _openTools.TryGetValue(delta.ToolCallId, out var pending):
                    pending.InputJson += delta.JsonFragment;
                    break;
                case ToolCallCompleted done:
                    _completedToolCalls++;
                    if (!_openTools.Remove(done.ToolCallId, out var entry))
                    {
                        entry = new ToolCallEntry { ToolCallId = done.ToolCallId, ToolName = done.ToolName };
                        AddEntry(entry);
                    }
                    entry.Result = done.Result;
                    entry.Elapsed = done.Elapsed;
                    entry.State = done.IsError ? ToolCallState.Failed : ToolCallState.Completed;
                    Journal(entry);
                    if (CodingTurnOrchestrator.MutatingToolIds.Contains(done.ToolName) && !done.IsError) result.WorkspaceChanged = true;
                    break;
                case CodingAcceptanceEvaluated acceptance:
                    _acceptance = acceptance.Result;
                    AddNoticeUnlocked(acceptance.Result.Status == CodingAcceptanceStatus.Failed ? NoticeKind.Error : NoticeKind.Info,
                        acceptance.Result.Summary, string.Join("\n\n", acceptance.Result.Checks.Select(check => $"{check.Name}: {(check.Passed ? "PASS" : "FAIL")}\n{check.Evidence}")));
                    break;
                case CodingTurnProgress progress:
                    _elapsed = progress.Elapsed;
                    _reportedTokens = progress.ReportedTokens ?? _reportedTokens;
                    _phase = progress.Phase;
                    break;
                case TurnUsageReported usage:
                    // Native and API providers report deltas. Null means the provider supplied no report.
                    _reportedTokens = (_reportedTokens ?? 0) + usage.InputTokens + usage.OutputTokens;
                    if (usage.CostUsd is { } cost) _reportedCost = (_reportedCost ?? 0) + cost;
                    break;
                case WorkingContextCompactionStarted started:
                    AddNoticeUnlocked(NoticeKind.Compaction, started.Reason == WorkingContextCompactionReason.UserRequested
                        ? "Compacting the conversation context as you asked..." : "Compacting conversation context...");
                    // PreCompact hooks are informational: they run off this path and cannot hold the compaction.
                    RunHookInBackground(() => SwarmCodeTurnHooks.PreCompactAsync(_services, ConversationId, started.Reason.ToString()));
                    break;
                case WorkingContextCompactionCompleted { Reason: WorkingContextCompactionReason.UserRequested } compacted:
                    AddNoticeUnlocked(NoticeKind.Compaction, compacted.Applied
                        ? $"Context compacted: {compacted.OriginalCharacters:N0} → {compacted.CompactedCharacters:N0} characters of working history."
                        : "Nothing to compact yet: the conversation is still short enough to send in full.");
                    break;
                case TurnCompleted completed:
                    var a = Streaming();
                    if (completed.ReplacesStreamedText || a.Text.Length == 0) a.ReplaceText(completed.FinalText);
                    a.IsStreaming = false;
                    a.Duration = completed.Duration;
                    a.LaneName = ActiveLane;
                    Journal(a);
                    result.FinalText = completed.FinalText;
                    result.Completed = true;
                    _streaming = null;
                    break;
                case TurnErrored errored:
                    result.Error = errored.Message;
                    AddNoticeUnlocked(NoticeKind.Error, errored.Message, errored.Details);
                    CloseStreaming();
                    break;
                case TurnAbandoned abandoned:
                    result.Cancelled = true;
                    AddNoticeUnlocked(NoticeKind.Cancelled, abandoned.Reason);
                    break;
            }
        }
    }

    private void CloseStreaming()
    {
        if (_streaming is null) return;
        _streaming.IsStreaming = false;
        if (_streaming.Text.Length > 0 || _streaming.Reasoning.Length > 0) Journal(_streaming);
        _streaming = null;
    }

    private AssistantEntry Streaming()
    {
        if (_streaming is null)
        {
            _streaming = new AssistantEntry { LaneName = ActiveLane };
            AddEntry(_streaming);
        }
        return _streaming;
    }

    private void OnPermissionRequested(PendingPermissionRequest request)
    {
        // One broker serves every open session (agents included): a request belongs to the conversation that asked.
        if (!string.IsNullOrEmpty(request.SessionId) && request.SessionId != ConversationId) return;
        lock (_gate)
        {
            AddEntry(new PermissionEntry
            {
                RequestId = request.RequestId,
                Title = $"{request.SkillName} / {request.FunctionName}",
                Permissions = request.Permissions,
                SkillName = request.SkillName,
                FunctionName = request.FunctionName,
                Levels = request.PermissionLevels,
            });
        }
        Changed?.Invoke();
        // A permission request is raised to the person: Notification hooks (kind "permission") hear about it.
        RunHookInBackground(() => SwarmCodeTurnHooks.NotificationAsync(_services, ConversationId, "permission",
            $"{request.SkillName} / {request.FunctionName} is waiting for your approval."));
    }

    private void AddNotice(NoticeKind kind, string text, string? details = null)
    {
        lock (_gate) AddNoticeUnlocked(kind, text, details);
    }

    private void AddNoticeUnlocked(NoticeKind kind, string text, string? details = null)
    {
        var notice = new NoticeEntry { Kind = kind, Text = text, Details = details };
        AddEntry(notice);
        Journal(notice);
    }

    /// <summary>Adds a visible entry; callers hold <c>_gate</c>.</summary>
    private void AddEntry(TranscriptEntry entry)
    {
        // An event the lane produced before a /clear stays cleared even when the consumer applies it later.
        if (_applyingClearedEvent) return;
        _entries.Add(entry);
        _live.Add(entry);
    }

    /// <summary>Replaces the visible entries; callers hold <c>_gate</c>.</summary>
    private void ResetEntries(IEnumerable<TranscriptEntry> entries)
    {
        _entries.Clear();
        _live.Clear();
        foreach (var entry in entries) AddEntry(entry);
    }

    private void EnqueueJournal(Func<Task> write, string what)
    {
        if (_journal is null) return;
        lock (_journalChainGate)
        {
            _journalTail = _journalTail.ContinueWith(async _ =>
            {
                try { await write().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not journal {What} for {Conversation}.", what, ConversationId); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    private Task DrainJournalAsync()
    {
        lock (_journalChainGate) return _journalTail;
    }

    /// <summary>Waits for queued presentation-journal writes to land; used by hosts before closing and by tests.</summary>
    public Task FlushJournalAsync() => DrainJournalAsync();

    /// <summary>Attaches the files named by <c>@path</c> mentions (resolved in this session's repository) to the lane message.</summary>
    private async Task<SwarmCore.Coding.Prompting.FileMentionExpansion> ExpandFileMentionsAsync(string laneMessage, CancellationToken ct)
    {
        if (!laneMessage.Contains('@')) return new(laneMessage, [], []);
        try
        {
            string? root = null;
            if (_services.GetService<ICodingWorkspaceResolver>() is { } resolver)
                root = (await resolver.ResolveAsync(ConversationId, ct).ConfigureAwait(false))?.FullRoot;
            root ??= _services.GetService<Microsoft.Extensions.Options.IOptions<CodingOptions>>()?.Value.RepositoryRoot;
            return await Task.Run(() => SwarmCore.Coding.Prompting.FileMentions.Expand(laneMessage, root), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not expand @file mentions for {Conversation}.", ConversationId);
            return new(laneMessage, [], []);
        }
    }

    /// <summary>
    /// Appends a finalized entry to the presentation journal; a journal failure never affects
    /// the turn. Callers hold <c>_gate</c>. An entry the user has already cleared from the
    /// visible transcript (a bubble or tool card still in flight when /clear ran) is not
    /// journalled, so it cannot come back on the next open.
    /// </summary>
    private void Journal(TranscriptEntry entry)
    {
        if (_journal is null || !_live.Contains(entry)) return;
        var line = FileTranscriptJournal.Serialize(entry);
        EnqueueJournal(() => { _journal.AppendLines(ConversationId, [line]); return Task.CompletedTask; }, "a transcript entry");
    }

    private void JournalRange(IReadOnlyList<TranscriptEntry> entries)
    {
        if (_journal is null || entries.Count == 0) return;
        var lines = entries.Select(FileTranscriptJournal.Serialize).ToList();
        EnqueueJournal(() => { _journal.AppendLines(ConversationId, lines); return Task.CompletedTask; }, $"{lines.Count} transcript entries");
    }

    private async Task PersistAsync()
    {
        if (_jobs is null) return;
        await _journalGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ConversationJob snapshot;
            lock (_gate)
                snapshot = new ConversationJob
                {
                    State = _jobState, Request = _jobRequest, Continuation = _jobContinuation, Attachments = _jobAttachments, StartedUtc = _jobStarted,
                    ActiveLane = ActiveLane, ActiveModel = ActiveModel, QueuePaused = _queuePaused, Queue = _queue.ToArray(),
                    Elapsed = _elapsed, CompletedToolCalls = _completedToolCalls, ReportedTokens = _reportedTokens,
                    ReportedCostUsd = _reportedCost, Acceptance = _acceptance,
                    // With a presentation journal the job file stays small: entries are appended there
                    // as they finalize instead of being rewritten here on every event.
                    Entries = _journal is null ? _entries.Select(SavedTranscriptEntry.From).OfType<SavedTranscriptEntry>().ToArray() : [],
                };
            await Task.Run(() => _jobs.Save(ConversationId, snapshot)).ConfigureAwait(false);
        }
        finally { _journalGate.Release(); }
    }

    private async Task PersistSafelyAsync()
    {
        try { await PersistAsync().ConfigureAwait(false); }
        catch (Exception)
        {
            lock (_gate) _queuePaused = true;
            _logger.LogWarning("Could not save job journal for {Conversation}.", ConversationId);
            AddNotice(NoticeKind.Error, "Could not save the job journal. Queued work is paused; check the data folder before continuing.");
        }
    }

    public void Dispose()
    {
        bool first;
        lock (_gate)
        {
            first = !_disposed;
            _disposed = true;
            _turnCts?.Cancel();
        }
        _brokerSubscription?.Dispose();
        _questionSubscription?.Dispose();
        DeclinePendingQuestions();
        if (_plans is not null) _plans.Changed -= OnPlanChanged;
        // SessionEnd ("closed") is best effort and never holds disposal: it runs on the pool and DisposeAsync waits a few seconds at most.
        if (first) _sessionEnd = Task.Run(() => SwarmCodeTurnHooks.SessionEndAsync(_services, ConversationId, "closed"));
    }

    /// <summary>Stops and drains the turn before its workspace service provider is disposed.</summary>
    public async ValueTask DisposeAsync()
    {
        Dispose();
        Task? turn;
        lock (_gate) turn = _turnTask;
        if (turn is not null) await turn.ConfigureAwait(false);
        lock (_gate) { _turnCts?.Dispose(); _turnCts = null; }
        try { await DrainJournalAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Presentation-journal writes for {Conversation} did not finish before disposal.", ConversationId); }
        if (_sessionEnd is { } sessionEnd)
        {
            try { await sessionEnd.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (Exception) { /* a slow SessionEnd hook keeps running on the pool; disposal does not wait */ }
        }
    }
}

/// <summary>Outcome summary of one turn, including hand-offs.</summary>
public sealed class TurnResult
{
    public bool Completed { get; set; }
    public bool Cancelled { get; set; }
    public string? Error { get; set; }
    public string? FinalText { get; set; }
    public int ToolCalls { get; set; }
    public bool WorkspaceChanged { get; set; }
    public List<string> LanesUsed { get; } = [];
}
