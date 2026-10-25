namespace SwarmCore.Abstractions.LlmRouting;

public enum LocalLlmWorkloadKind
{
    Text = 0,
    Embedding = 1,
    Vision = 2
}

/// <summary>Host scheduling intent, separate from model capability and privacy.</summary>
public enum LocalLlmSchedulingClass { Standard = 0, RoutingDecision = 1 }

public enum LocalLlmRuntimeActivityKind
{
    ModelInstall = 0,
    ModelRepair = 1,
    Calibration = 2
}

public sealed record LocalLlmRuntimeRequest(
    string ProviderName,
    string Model,
    LlmCallContext? Context = null,
    LocalLlmWorkloadKind WorkloadKind = LocalLlmWorkloadKind.Text)
{
    public LlmInteractivity Interactivity => Context?.Interactivity ?? LlmInteractivity.Background;
    public LocalLlmSchedulingClass SchedulingClass { get; init; }
    /// <summary>Bounds admission only; response generation has an independent deadline.</summary>
    public TimeSpan? AdmissionTimeout { get; init; }
}

public sealed record LocalLlmRuntimeActivity(
    string ProviderName,
    string Model,
    LocalLlmWorkloadKind WorkloadKind,
    LocalLlmRuntimeActivityKind Kind,
    string Description,
    DateTimeOffset StartedUtc);

public sealed record LocalLlmRuntimeSnapshot(
    IReadOnlyDictionary<LocalLlmWorkloadKind, string> HotModelsByLane,
    IReadOnlyList<LocalLlmRuntimeActivity> ActiveActivities,
    DateTimeOffset? LastSwitchUtc,
    int SwitchCount,
    int HighPriorityPending,
    int ActiveRequests);

/// <summary>
/// An exclusive, preemptible local-runtime session. Long-running calibration uses
/// this to keep one model resident across several provider calls without deadlocking
/// the router's ordinary per-call admission lease.
/// </summary>
public interface ILocalLlmRuntimeExclusiveSession : IDisposable
{
    /// <summary>
    /// Cancelled when a user-waiting local request arrives. The owner must observe
    /// it in provider calls and at work-item boundaries, then dispose the session.
    /// </summary>
    CancellationToken PreemptionToken { get; }

    /// <summary>
    /// Marks nested provider calls as belonging to this session. The returned scope
    /// must be disposed before the session itself.
    /// </summary>
    IDisposable Activate();
}

/// <summary>
/// Raised when background local-runtime work yields to a user-waiting request.
/// This is a scheduling outcome, not model-capability failure evidence.
/// </summary>
public sealed class LocalLlmRuntimePreemptedException : OperationCanceledException
{
    public LocalLlmRuntimePreemptedException(string message, CancellationToken cancellationToken)
        : base(message, cancellationToken)
    {
    }
}

/// <summary>
/// Admission expired before inference began. This is local scheduling pressure,
/// not provider failure evidence or authority to switch providers.
/// </summary>
public sealed class LocalLlmRuntimeBusyException(string message) : TimeoutException(message);

/// <summary>
/// Process-wide coordinator for local LLM work, with a shared cross-process
/// machine lease for every Talavine host running as the same OS user. It
/// owns both admission control and provider execution policy, so local models
/// behave like a managed runtime instead of one-off per-call model loads.
/// </summary>
public interface ILocalLlmRuntimeManager
{
    /// <summary>
    /// Reserve local runtime capacity for the request. Dispose the returned
    /// lease when the provider call completes.
    /// </summary>
    Task<IDisposable> AcquireAsync(LocalLlmRuntimeRequest request, CancellationToken ct = default);

    /// <summary>
    /// Reserve the runtime across a sequence of related background provider calls.
    /// Background work cannot interleave another model load. A user-waiting request
    /// cancels <see cref="ILocalLlmRuntimeExclusiveSession.PreemptionToken"/> and is
    /// admitted as soon as the owner observes it and releases the session.
    /// </summary>
    Task<ILocalLlmRuntimeExclusiveSession> AcquireExclusiveActivityAsync(
        LocalLlmRuntimeRequest request,
        LocalLlmRuntimeActivity activity,
        CancellationToken ct = default);

    /// <summary>
    /// Publish a long-running local runtime operation, such as auto-pulling a
    /// missing model, so UI surfaces can explain why local work is waiting.
    /// </summary>
    IDisposable TrackActivity(LocalLlmRuntimeActivity activity);

    /// <summary>
    /// Apply local runtime execution hints such as Ollama keep_alive. Caller
    /// supplied settings win, so explicit overrides remain possible.
    /// </summary>
    void ApplyExecutionPolicy(LocalLlmRuntimeRequest request, IDictionary<string, object> executionSettings);

    int HighPriorityPending { get; }

    LocalLlmRuntimeSnapshot GetSnapshot();

    /// <summary>Best-effort live scheduling metadata. Older/unreadable hosts may be absent.</summary>
    Task<LocalLlmRuntimeQueueSnapshot> GetQueueSnapshotAsync(CancellationToken ct = default)
        => Task.FromResult(new LocalLlmRuntimeQueueSnapshot([], true));
}
