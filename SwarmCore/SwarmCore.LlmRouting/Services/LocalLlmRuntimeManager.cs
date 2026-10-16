using Microsoft.Extensions.Logging;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// Default local LLM runtime manager: one in-flight local request per process,
/// one in-flight local request per OS user across Talavine processes,
/// user-waiting calls jump ahead of background work, and each workload lane gets
/// a stable keep-alive policy so local providers avoid unnecessary load/unload churn.
/// </summary>
public sealed class LocalLlmRuntimeManager : ILocalLlmRuntimeManager
{
    // This is intentionally provider-agnostic. A GPU/CPU local inference runtime is
    // shared physical capacity whether it is Ollama, LM Studio, or a future local
    // provider, and each desktop/MCP/helper process has its own DI container.
    // A named mutex gives those processes one common lease without requiring a
    // daemon. The lease worker owns and releases it on the same thread because
    // Mutex ownership is thread-affine while provider calls are asynchronous.
    private const string DefaultMachineLeaseMutexName = "Talavine.LocalLlmRuntime.v1";
    private static readonly TimeSpan MachineLeaseRetryDelay = TimeSpan.FromMilliseconds(120);
    private readonly ILogger<LocalLlmRuntimeManager>? _logger;
    private readonly string _machineLeaseMutexName;
    private readonly SemaphoreSlim _slot = new(1, 1);
    private readonly object _priorityGate = new();
    private readonly object _stateGate = new();
    private readonly AsyncLocal<long?> _exclusiveScopeId = new();
    private readonly Dictionary<LocalLlmWorkloadKind, string> _hotModels = new();
    private readonly Dictionary<long, LocalLlmRuntimeActivity> _activeActivities = new();
    private TaskCompletionSource _drained = CompletedSource();
    private DateTimeOffset? _lastSwitchUtc;
    private int _highPending;
    private int _switchCount;
    private int _activeRequests;
    private long _nextActivityId;
    private long _nextExclusiveSessionId;
    private ExclusiveSession? _exclusiveSession;

    public LocalLlmRuntimeManager(
        ILogger<LocalLlmRuntimeManager>? logger = null,
        string? machineLeaseMutexName = null)
    {
        _logger = logger;
        _machineLeaseMutexName = string.IsNullOrWhiteSpace(machineLeaseMutexName)
            ? DefaultMachineLeaseMutexName
            : machineLeaseMutexName;
    }

    public int HighPriorityPending => Volatile.Read(ref _highPending);

    public void ApplyExecutionPolicy(LocalLlmRuntimeRequest request, IDictionary<string, object> executionSettings)
    {
        if (executionSettings.ContainsKey("keep_alive")) return;
        if (!OllamaProviderFamily.IsOllamaFamily(request.ProviderName)) return;

        executionSettings["keep_alive"] = SelectKeepAlive(request);
    }

    public async Task<IDisposable> AcquireAsync(LocalLlmRuntimeRequest request, CancellationToken ct = default)
    {
        if (TryAcquireNestedExclusive(request, out var nestedLease))
        {
            return nestedLease;
        }

        var processLease = await AcquirePrioritySlotAsync(request.Interactivity, ct).ConfigureAwait(false);
        try
        {
            var machineLease = await AcquireMachineLeaseAsync(ct).ConfigureAwait(false);
            RecordAdmission(request);
            return new CompositeLease(processLease, machineLease);
        }
        catch
        {
            processLease.Dispose();
            throw;
        }
    }

    public async Task<ILocalLlmRuntimeExclusiveSession> AcquireExclusiveActivityAsync(
        LocalLlmRuntimeRequest request,
        LocalLlmRuntimeActivity activity,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(activity);

        // Exclusive sessions are background maintenance. Let any already-pending
        // interactive request win before reserving the one physical local slot.
        var slotLease = await AcquirePrioritySlotAsync(LlmInteractivity.Background, ct)
            .ConfigureAwait(false);
        ExclusiveSession? session = null;
        IDisposable? activityLease = null;
        try
        {
            var machineLease = await AcquireMachineLeaseAsync(ct).ConfigureAwait(false);
            slotLease = new CompositeLease(slotLease, machineLease);
            RecordAdmission(request);
            activityLease = TrackActivity(activity);
            session = new ExclusiveSession(
                this,
                Interlocked.Increment(ref _nextExclusiveSessionId),
                request,
                slotLease,
                activityLease);

            lock (_stateGate)
            {
                // The one-slot admission above makes overlap impossible. Keep an
                // explicit invariant here so a future admission change fails loud.
                if (_exclusiveSession is not null)
                {
                    throw new InvalidOperationException("A local-runtime exclusive session is already active.");
                }

                _exclusiveSession = session;
            }

            slotLease = NoOpReleaser.Instance;
            activityLease = null;

            // Close the small race where an interactive waiter arrived after the
            // background slot was acquired but before this session was published.
            if (HighPriorityPending > 0)
            {
                session.RequestPreemption();
            }

            return session;
        }
        catch
        {
            if (session is not null)
            {
                ClearExclusiveSession(session);
            }
            activityLease?.Dispose();
            slotLease.Dispose();
            throw;
        }
    }

    public IDisposable TrackActivity(LocalLlmRuntimeActivity activity)
    {
        var id = Interlocked.Increment(ref _nextActivityId);
        lock (_stateGate)
        {
            _activeActivities[id] = activity;
        }

        _logger?.LogInformation(
            "Local LLM runtime activity started: {Kind} {Model} ({Description})",
            activity.Kind,
            $"{activity.ProviderName}/{activity.Model}",
            activity.Description);

        return new ActivityReleaser(this, id, activity);
    }

    public LocalLlmRuntimeSnapshot GetSnapshot()
    {
        lock (_stateGate)
        {
            return new LocalLlmRuntimeSnapshot(
                new Dictionary<LocalLlmWorkloadKind, string>(_hotModels),
                _activeActivities.Values
                    .OrderBy(a => a.StartedUtc)
                    .ToList(),
                _lastSwitchUtc,
                _switchCount,
                HighPriorityPending,
                Volatile.Read(ref _activeRequests));
        }
    }

    private async Task<IDisposable> AcquirePrioritySlotAsync(LlmInteractivity interactivity, CancellationToken ct)
    {
        // A request canceled before admission has no scheduling authority. In
        // particular, it must not increment the interactive-waiter count or
        // preempt a healthy exclusive calibration session it will never use.
        ct.ThrowIfCancellationRequested();
        var isHigh = interactivity == LlmInteractivity.UserWaiting;

        if (isHigh)
        {
            lock (_priorityGate)
            {
                if (_highPending == 0)
                {
                    _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                _highPending++;
            }

            // A calibration session owns the physical slot across multiple calls.
            // Signal it before waiting so it stops at its next cancellable provider
            // operation or case boundary instead of making chat wait for the suite.
            RequestExclusivePreemption();

            try
            {
                await _slot.WaitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                ReleaseHighPending();
                throw;
            }

            _logger?.LogTrace("Local LLM runtime acquired (user-waiting); pending={Pending}", _highPending);
            Interlocked.Increment(ref _activeRequests);
            return new Releaser(this, wasHigh: true);
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            Task drainedTask;
            lock (_priorityGate)
            {
                drainedTask = _drained.Task;
            }

            await drainedTask.WaitAsync(ct).ConfigureAwait(false);
            await _slot.WaitAsync(ct).ConfigureAwait(false);

            lock (_priorityGate)
            {
                if (_highPending == 0)
                {
                    _logger?.LogTrace("Local LLM runtime acquired (background)");
                    Interlocked.Increment(ref _activeRequests);
                    return new Releaser(this, wasHigh: false);
                }
            }

            _slot.Release();
        }
    }

    private async Task<IDisposable> AcquireMachineLeaseAsync(CancellationToken ct)
    {
        var lease = await MachineMutexLease.AcquireAsync(_machineLeaseMutexName, ct).ConfigureAwait(false);
        _logger?.LogTrace("Local LLM machine lease acquired");
        return lease;
    }

    private bool TryAcquireNestedExclusive(
        LocalLlmRuntimeRequest request,
        out IDisposable lease)
    {
        lease = NoOpReleaser.Instance;
        var scopeId = _exclusiveScopeId.Value;
        if (scopeId is null)
        {
            return false;
        }

        ExclusiveSession? session;
        lock (_stateGate)
        {
            if (_exclusiveSession is null
                || _exclusiveSession.Id != scopeId.Value
                || _exclusiveSession.IsDisposed)
            {
                return false;
            }

            session = _exclusiveSession;
        }

        if (!session.Matches(request))
        {
            throw new InvalidOperationException(
                "A nested local-runtime request attempted to change the provider, model, or workload lane inside an exclusive session.");
        }

        // The session already owns the physical semaphore. Re-entering it here
        // would deadlock the router on the first calibration case.
        RecordAdmission(request);
        return true;
    }

    private void RequestExclusivePreemption()
    {
        ExclusiveSession? session;
        lock (_stateGate)
        {
            session = _exclusiveSession;
        }

        session?.RequestPreemption();
    }

    private void ClearExclusiveSession(ExclusiveSession session)
    {
        lock (_stateGate)
        {
            if (ReferenceEquals(_exclusiveSession, session))
            {
                _exclusiveSession = null;
            }
        }
    }

    private void RecordAdmission(LocalLlmRuntimeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Model)) return;

        var lane = request.WorkloadKind;
        var modelKey = $"{request.ProviderName}/{request.Model}";
        string? previous = null;
        int switchCount = 0;

        lock (_stateGate)
        {
            if (_hotModels.TryGetValue(lane, out previous) &&
                string.Equals(previous, modelKey, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _hotModels[lane] = modelKey;
            _lastSwitchUtc = DateTimeOffset.UtcNow;
            switchCount = ++_switchCount;
        }

        if (previous is null)
        {
            _logger?.LogInformation(
                "Local LLM runtime lane {Lane} warmed with {Model} ({Interactivity})",
                lane,
                modelKey,
                request.Interactivity);
        }
        else
        {
            _logger?.LogInformation(
                "Local LLM runtime lane {Lane} switching {PreviousModel} -> {Model} ({Interactivity}); switches={SwitchCount}",
                lane,
                previous,
                modelKey,
                request.Interactivity,
                switchCount);
        }
    }

    private static string SelectKeepAlive(LocalLlmRuntimeRequest request)
    {
        if (request.WorkloadKind == LocalLlmWorkloadKind.Vision || IsLikelyVisionModel(request.Model))
        {
            return "5m";
        }

        if (request.WorkloadKind == LocalLlmWorkloadKind.Embedding)
        {
            return "24h";
        }

        return request.Interactivity == LlmInteractivity.UserWaiting
            ? "30m"
            : "20m";
    }

    private static bool IsLikelyVisionModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        var lower = model.ToLowerInvariant();
        return lower.Contains("vision")
               || lower.Contains("llava")
               || lower.Contains("bakllava")
               || lower.Contains("moondream")
               || lower.Contains("gemma3")
               || lower.Contains("minicpm-v")
               || lower.Contains("qwen2-vl")
               || lower.Contains("qwen2.5-vl")
               || lower.Contains("qwen3.5")
               || lower.Contains("qwen3.8")
               || lower.Contains("llama4-scout")
               || lower.Contains("llama4-maverick");
    }

    private void ReleaseHighPending()
    {
        TaskCompletionSource? toComplete = null;
        lock (_priorityGate)
        {
            if (_highPending == 0) return;
            if (--_highPending == 0)
            {
                toComplete = _drained;
            }
        }
        toComplete?.TrySetResult();
    }

    private static TaskCompletionSource CompletedSource()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    private sealed class Releaser : IDisposable
    {
        private readonly LocalLlmRuntimeManager _owner;
        private readonly bool _wasHigh;
        private int _disposed;

        public Releaser(LocalLlmRuntimeManager owner, bool wasHigh)
        {
            _owner = owner;
            _wasHigh = wasHigh;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Interlocked.Decrement(ref _owner._activeRequests);
            _owner._slot.Release();
            if (_wasHigh) _owner.ReleaseHighPending();
        }
    }

    private sealed class CompositeLease : IDisposable
    {
        private IDisposable? _processLease;
        private IDisposable? _machineLease;

        public CompositeLease(IDisposable processLease, IDisposable machineLease)
        {
            _processLease = processLease;
            _machineLease = machineLease;
        }

        public void Dispose()
        {
            // Release the cross-process lock first. The per-process queue remains
            // held until that succeeds, so another local request cannot race this
            // one during teardown.
            Interlocked.Exchange(ref _machineLease, null)?.Dispose();
            Interlocked.Exchange(ref _processLease, null)?.Dispose();
        }
    }

    private sealed class MachineMutexLease : IDisposable
    {
        private readonly TaskCompletionSource<MachineMutexLease> _ready =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        public static async Task<MachineMutexLease> AcquireAsync(string mutexName, CancellationToken ct)
        {
            var lease = new MachineMutexLease(mutexName);
            _ = Task.Factory.StartNew(
                () => lease.OwnMutex(ct),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            return await lease._ready.Task.ConfigureAwait(false);
        }

        private readonly string _mutexName;

        private MachineMutexLease(string mutexName)
        {
            _mutexName = mutexName;
        }

        private void OwnMutex(CancellationToken ct)
        {
            using var mutex = new Mutex(initiallyOwned: false, _mutexName);
            var acquired = false;

            try
            {
                while (!acquired)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        acquired = mutex.WaitOne(MachineLeaseRetryDelay);
                    }
                    catch (AbandonedMutexException)
                    {
                        // The previous owner died; Windows/Unix grant ownership
                        // to this waiter so it can safely resume local inference.
                        acquired = true;
                    }
                }

                _ready.TrySetResult(this);
                _released.Task.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
            {
                _ready.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                _ready.TrySetException(ex);
            }
            finally
            {
                if (acquired)
                {
                    mutex.ReleaseMutex();
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _released.TrySetResult();
        }
    }

    private sealed class NoOpReleaser : IDisposable
    {
        public static readonly NoOpReleaser Instance = new();
        public void Dispose()
        {
        }
    }

    private sealed class ExclusiveSession : ILocalLlmRuntimeExclusiveSession
    {
        private readonly LocalLlmRuntimeManager _owner;
        private readonly IDisposable _slotLease;
        private readonly IDisposable _activityLease;
        private readonly CancellationTokenSource _preemption = new();
        private readonly CancellationToken _preemptionToken;
        private readonly object _lifecycleGate = new();
        private int _disposed;

        public ExclusiveSession(
            LocalLlmRuntimeManager owner,
            long id,
            LocalLlmRuntimeRequest reservedRequest,
            IDisposable slotLease,
            IDisposable activityLease)
        {
            _owner = owner;
            Id = id;
            ReservedRequest = reservedRequest;
            _slotLease = slotLease;
            _activityLease = activityLease;
            _preemptionToken = _preemption.Token;
        }

        public long Id { get; }
        private LocalLlmRuntimeRequest ReservedRequest { get; }
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public CancellationToken PreemptionToken => _preemptionToken;

        public IDisposable Activate()
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            var previous = _owner._exclusiveScopeId.Value;
            _owner._exclusiveScopeId.Value = Id;
            return new ExclusiveActivation(_owner, Id, previous);
        }

        public bool Matches(LocalLlmRuntimeRequest request) =>
            NormalizeProvider(ReservedRequest.ProviderName) == NormalizeProvider(request.ProviderName)
            && string.Equals(ReservedRequest.Model, request.Model, StringComparison.OrdinalIgnoreCase)
            && ReservedRequest.WorkloadKind == request.WorkloadKind;

        private static string NormalizeProvider(string provider) =>
            provider.Replace(" ", string.Empty, StringComparison.Ordinal)
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .Trim()
                .ToUpperInvariant();

        public void RequestPreemption()
        {
            lock (_lifecycleGate)
            {
                if (IsDisposed) return;
                try
                {
                    _preemption.Cancel(throwOnFirstException: false);
                }
                catch (AggregateException ex)
                {
                    _owner._logger?.LogDebug(ex, "Local-runtime preemption callback failed");
                }
            }
        }

        public void Dispose()
        {
            lock (_lifecycleGate)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            }

            _owner.ClearExclusiveSession(this);
            try
            {
                _activityLease.Dispose();
            }
            finally
            {
                try
                {
                    _slotLease.Dispose();
                }
                finally
                {
                    _preemption.Dispose();
                }
            }
        }
    }

    private sealed class ExclusiveActivation : IDisposable
    {
        private readonly LocalLlmRuntimeManager _owner;
        private readonly long _sessionId;
        private readonly long? _previous;
        private int _disposed;

        public ExclusiveActivation(LocalLlmRuntimeManager owner, long sessionId, long? previous)
        {
            _owner = owner;
            _sessionId = sessionId;
            _previous = previous;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (_owner._exclusiveScopeId.Value == _sessionId)
            {
                _owner._exclusiveScopeId.Value = _previous;
            }
        }
    }

    private sealed class ActivityReleaser : IDisposable
    {
        private readonly LocalLlmRuntimeManager _owner;
        private readonly long _id;
        private readonly LocalLlmRuntimeActivity _activity;
        private int _disposed;

        public ActivityReleaser(LocalLlmRuntimeManager owner, long id, LocalLlmRuntimeActivity activity)
        {
            _owner = owner;
            _id = id;
            _activity = activity;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            lock (_owner._stateGate)
            {
                _owner._activeActivities.Remove(_id);
            }

            _owner._logger?.LogInformation(
                "Local LLM runtime activity finished: {Kind} {Model}",
                _activity.Kind,
                $"{_activity.ProviderName}/{_activity.Model}");
        }
    }
}
