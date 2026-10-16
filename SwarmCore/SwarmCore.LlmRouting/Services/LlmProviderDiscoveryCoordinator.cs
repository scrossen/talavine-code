using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Services;

public sealed record LlmProviderConnectivityResult(
    bool Completed,
    bool IsHealthy,
    string Message,
    bool Deferred = false)
{
    public static LlmProviderConnectivityResult Unavailable(
        string message,
        bool deferred = false)
        => new(false, false, message, deferred);
}

public sealed record LlmProviderModelCatalogResult(
    bool Completed,
    IReadOnlyList<string> Models,
    string Message,
    bool Deferred = false)
{
    public static LlmProviderModelCatalogResult Unavailable(
        string message,
        bool deferred = false)
        => new(false, [], message, deferred);
}

/// <summary>
/// Process-wide owner for provider catalog and connectivity calls. Provider
/// implementations expose non-cancellable APIs, so caller timeout/cancellation
/// only stops waiting; the coordinator retains the physical task, observes its
/// completion, and never starts a duplicate for the same provider/operation.
/// Active work is owned per provider instance and operation. A call for changed
/// settings never joins an older worker and is deferred until that physical
/// worker exits, because provider implementations may hold mutable connection
/// state and are not safe to invoke concurrently across configuration changes.
/// Cached evidence remains bound to a one-way configuration identity.
/// Global worker bounds plus a small priority escape lane prevent permanently
/// hung background probes from starving live routing.
/// </summary>
public sealed class LlmProviderDiscoveryCoordinator
{
    private static readonly TimeSpan DefaultModelCacheLifetime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DefaultConnectivityCacheLifetime = TimeSpan.FromSeconds(15);

    // A probe that has produced nothing for this long is treated as hung: it
    // stops counting against worker capacity, and once its configuration is
    // also stale it is abandoned outright so recovery does not require an app
    // restart. Ordinary probes finish in seconds; only wedged CLI children
    // ever get near this bound.
    private static readonly TimeSpan DefaultHungWorkerBound = TimeSpan.FromMinutes(5);

    public static LlmProviderDiscoveryCoordinator Shared { get; } = new(
        NullLogger<LlmProviderDiscoveryCoordinator>.Instance,
        TimeProvider.System,
        maximumGeneralWorkers: 4,
        maximumPriorityEscapeWorkers: 2,
        DefaultModelCacheLifetime,
        DefaultConnectivityCacheLifetime);

    // Provider instances can be replaced when settings change. For one instance,
    // however, only one physical call per operation may run at a time: invoking a
    // mutable provider again while an old non-cancellable call is still using its
    // previous endpoint/credential can cross-contaminate both calls. Weak keys
    // avoid retaining replaced provider instances after their work has completed.
    private readonly ConditionalWeakTable<ILlmProvider, ModelState> _modelStates = new();
    private readonly ConditionalWeakTable<ILlmProvider, ConnectivityState> _connectivityStates = new();
    private readonly ILogger<LlmProviderDiscoveryCoordinator> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly int _maximumGeneralWorkers;
    private readonly int _maximumPriorityEscapeWorkers;
    private readonly TimeSpan _modelCacheLifetime;
    private readonly TimeSpan _connectivityCacheLifetime;
    private readonly TimeSpan _hungWorkerBound;
    private readonly object _capacityGate = new();
    private int _generalWorkers;
    private int _priorityEscapeWorkers;

    public LlmProviderDiscoveryCoordinator(ILogger<LlmProviderDiscoveryCoordinator> logger)
        : this(
            logger,
            TimeProvider.System,
            maximumGeneralWorkers: 4,
            maximumPriorityEscapeWorkers: 2,
            DefaultModelCacheLifetime,
            DefaultConnectivityCacheLifetime)
    {
    }

    internal LlmProviderDiscoveryCoordinator(
        ILogger<LlmProviderDiscoveryCoordinator> logger,
        TimeProvider timeProvider,
        int maximumGeneralWorkers,
        int maximumPriorityEscapeWorkers,
        TimeSpan modelCacheLifetime,
        TimeSpan connectivityCacheLifetime,
        TimeSpan? hungWorkerBound = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (maximumGeneralWorkers <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumGeneralWorkers));
        if (maximumPriorityEscapeWorkers < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumPriorityEscapeWorkers));
        if (modelCacheLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(modelCacheLifetime));
        if (connectivityCacheLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(connectivityCacheLifetime));
        if (hungWorkerBound is { } bound && bound <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(hungWorkerBound));

        _logger = logger;
        _timeProvider = timeProvider;
        _maximumGeneralWorkers = maximumGeneralWorkers;
        _maximumPriorityEscapeWorkers = maximumPriorityEscapeWorkers;
        _modelCacheLifetime = modelCacheLifetime;
        _connectivityCacheLifetime = connectivityCacheLifetime;
        _hungWorkerBound = hungWorkerBound ?? DefaultHungWorkerBound;
    }

    internal int OutstandingWorkers
    {
        get
        {
            lock (_capacityGate)
                return _generalWorkers + _priorityEscapeWorkers;
        }
    }

    public async Task<IReadOnlyList<string>> GetAvailableModelsAsync(
        ILlmProvider provider,
        TimeSpan callerTimeout,
        CancellationToken callerCancellationToken = default,
        bool forceRefresh = false,
        bool highPriority = false,
        string? configurationIdentity = null)
        => (await GetModelCatalogAsync(
                provider,
                callerTimeout,
                callerCancellationToken,
                forceRefresh,
                highPriority,
                configurationIdentity)
            .ConfigureAwait(false)).Models;

    /// <summary>
    /// Returns only recent model-catalog evidence already owned by this process.
    /// This method never starts or joins provider work, so status surfaces can
    /// inspect a saved exact route without turning page navigation into a live
    /// provider probe.
    /// </summary>
    public LlmProviderModelCatalogResult GetCachedModelCatalog(
        ILlmProvider provider,
        string? configurationIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var identity = NormalizeConfigurationIdentity(configurationIdentity);
        return _modelStates.TryGetValue(provider, out var state)
            ? ModelSnapshot(state, identity, "No recent provider model catalog is available.")
            : LlmProviderModelCatalogResult.Unavailable(
                "No recent provider model catalog is available.");
    }

    /// <summary>
    /// Returns only recent connectivity evidence already owned by this process.
    /// It never starts or joins a physical provider call.
    /// </summary>
    public LlmProviderConnectivityResult GetCachedConnectivity(
        ILlmProvider provider,
        string? configurationIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var identity = NormalizeConfigurationIdentity(configurationIdentity);
        return _connectivityStates.TryGetValue(provider, out var state)
            ? ConnectivitySnapshot(state, identity, "No recent provider connectivity result is available.")
            : LlmProviderConnectivityResult.Unavailable(
                "No recent provider connectivity result is available.");
    }

    /// <summary>
    /// Gets a model catalog while preserving whether an empty list was an
    /// affirmative provider response or merely a timeout/error/capacity miss.
    /// Control and spend-authorizing paths must use this typed result.
    /// </summary>
    public async Task<LlmProviderModelCatalogResult> GetModelCatalogAsync(
        ILlmProvider provider,
        TimeSpan callerTimeout,
        CancellationToken callerCancellationToken = default,
        bool forceRefresh = false,
        bool highPriority = false,
        string? configurationIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ValidateTimeout(callerTimeout);
        var identity = NormalizeConfigurationIdentity(configurationIdentity);
        var state = _modelStates.GetValue(provider, static _ => new ModelState());
        ModelWorker? worker = null;
        lock (state.Gate)
        {
            var now = _timeProvider.GetUtcNow();
            if (!forceRefresh
                && state.ActiveWorker is null
                && state.LastCompletedAtUtc is { } completedAt
                && string.Equals(
                    state.LastCompletedConfigurationIdentity,
                    identity,
                    StringComparison.Ordinal)
                && now - completedAt < _modelCacheLifetime)
            {
                return CompletedModelSnapshot(state.LastKnownResult, "Using a recent provider model catalog.");
            }

            if (state.ActiveWorker is { } active)
            {
                var identityMatches = string.Equals(
                    active.ConfigurationIdentity,
                    identity,
                    StringComparison.Ordinal);
                if (IsHung(active.StartedAtUtc, now))
                {
                    // A hung probe must not starve the shared worker pool.
                    ReleaseHungWorkerLease(
                        active.Lease,
                        active.Ownership,
                        "model-discovery",
                        provider.ProviderName,
                        active.StartedAtUtc,
                        now);
                    if (!identityMatches)
                    {
                        // Its configuration no longer exists, so its eventual
                        // result is worthless: abandon it (the completion
                        // observer's ReferenceEquals guard discards any late
                        // result) and let this request start a fresh probe.
                        _logger.LogWarning(
                            "Abandoning a model-discovery worker for {Provider} running {Age} under an earlier configuration; a new probe may start",
                            provider.ProviderName,
                            now - active.StartedAtUtc);
                        state.ActiveWorker = null;
                    }
                }

                if (state.ActiveWorker is { } stillActive)
                {
                    if (!identityMatches)
                    {
                        return LlmProviderModelCatalogResult.Unavailable(
                            "Provider model discovery is waiting for a worker from an earlier provider configuration to exit; retry is deferred.",
                            deferred: true);
                    }

                    worker = stillActive;
                }
            }

            if (worker is null)
            {
                worker = StartModelWorker(provider, state, identity, highPriority, now);
                if (worker is null)
                {
                    _logger.LogDebug(
                        "Provider discovery capacity is full; returning cached models for {Provider}",
                        provider.ProviderName);
                    return ModelSnapshot(
                        state,
                        identity,
                        "Provider model-discovery capacity is currently full.",
                        deferredWhenUnavailable: true);
                }
            }
        }

        try
        {
            var completed = await worker.Task
                    .WaitAsync(callerTimeout, callerCancellationToken)
                    .ConfigureAwait(false);
            await worker.CompletionObserved.Task.ConfigureAwait(false);
            return CompletedModelSnapshot(completed, "Provider model discovery completed.");
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            _logger.LogDebug(
                "Timed out after {TimeoutMs} ms waiting for {Provider} model discovery; the physical worker remains owned",
                callerTimeout.TotalMilliseconds,
                provider.ProviderName);
            return ModelSnapshot(
                state,
                identity,
                "Provider model discovery timed out.",
                deferredWhenUnavailable: true);
        }
        catch (Exception ex)
        {
            await worker.CompletionObserved.Task.ConfigureAwait(false);
            _logger.LogDebug(ex, "Provider model discovery failed for {Provider}", provider.ProviderName);
            return ModelSnapshot(state, identity, "Provider model discovery failed.");
        }
    }

    public async Task<LlmProviderConnectivityResult> ProbeConnectivityAsync(
        ILlmProvider provider,
        TimeSpan callerTimeout,
        CancellationToken callerCancellationToken = default,
        bool forceRefresh = false,
        bool highPriority = false,
        string? configurationIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ValidateTimeout(callerTimeout);
        var identity = NormalizeConfigurationIdentity(configurationIdentity);
        var state = _connectivityStates.GetValue(provider, static _ => new ConnectivityState());
        ConnectivityWorker? worker = null;
        lock (state.Gate)
        {
            var now = _timeProvider.GetUtcNow();
            if (!forceRefresh
                && state.ActiveWorker is null
                && state.LastCompletedAtUtc is { } completedAt
                && string.Equals(
                    state.LastCompletedConfigurationIdentity,
                    identity,
                    StringComparison.Ordinal)
                && now - completedAt < _connectivityCacheLifetime
                && state.LastKnownResult is { } cached)
            {
                return cached;
            }

            if (state.ActiveWorker is { } active)
            {
                var identityMatches = string.Equals(
                    active.ConfigurationIdentity,
                    identity,
                    StringComparison.Ordinal);
                if (IsHung(active.StartedAtUtc, now))
                {
                    ReleaseHungWorkerLease(
                        active.Lease,
                        active.Ownership,
                        "connectivity",
                        provider.ProviderName,
                        active.StartedAtUtc,
                        now);
                    if (!identityMatches)
                    {
                        _logger.LogWarning(
                            "Abandoning a connectivity worker for {Provider} running {Age} under an earlier configuration; a new probe may start",
                            provider.ProviderName,
                            now - active.StartedAtUtc);
                        state.ActiveWorker = null;
                    }
                }

                if (state.ActiveWorker is { } stillActive)
                {
                    if (!identityMatches)
                    {
                        return LlmProviderConnectivityResult.Unavailable(
                            "Provider connectivity is waiting for a worker from an earlier provider configuration to exit; retry is deferred.",
                            deferred: true);
                    }

                    worker = stillActive;
                }
            }

            if (worker is null)
            {
                worker = StartConnectivityWorker(provider, state, identity, highPriority, now);
                if (worker is null)
                {
                    return ConnectivitySnapshot(
                        state,
                        identity,
                        "Provider discovery capacity is currently full.",
                        deferredWhenUnavailable: true);
                }
            }
        }

        try
        {
            var completed = await worker.Task
                .WaitAsync(callerTimeout, callerCancellationToken)
                .ConfigureAwait(false);
            await worker.CompletionObserved.Task.ConfigureAwait(false);
            return completed;
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            _logger.LogDebug(
                "Timed out after {TimeoutMs} ms waiting for {Provider} connectivity; the physical worker remains owned",
                callerTimeout.TotalMilliseconds,
                provider.ProviderName);
            return ConnectivitySnapshot(
                state,
                identity,
                "Provider connectivity timed out.",
                deferredWhenUnavailable: true);
        }
        catch (Exception ex)
        {
            await worker.CompletionObserved.Task.ConfigureAwait(false);
            _logger.LogDebug(ex, "Provider connectivity failed for {Provider}", provider.ProviderName);
            return ConnectivitySnapshot(state, identity, "Provider connectivity failed.");
        }
    }

    /// <summary>
    /// Builds a stable one-way identity for provider settings that can affect
    /// connectivity or catalog results. The returned value never contains an
    /// endpoint, credential, profile name, executable path, or custom setting.
    /// </summary>
    public static string CreateConfigurationIdentity(LlmProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.ProviderName);

        using var material = new MemoryStream();
        using (var writer = new BinaryWriter(material, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(1); // Identity schema version.
            WriteIdentityValue(writer, settings.ProviderName.Trim().ToUpperInvariant());
            WriteIdentityValue(writer, settings.Endpoint);
            WriteIdentityValue(writer, settings.ApiKey);
            WriteIdentityValue(writer, settings.DefaultModel);
            WriteIdentityValue(writer, settings.DefaultEmbeddingModel);

            var additionalSettings = settings.AdditionalSettings
                ?? new Dictionary<string, string>();
            writer.Write(additionalSettings.Count);
            foreach (var pair in additionalSettings.OrderBy(
                         static pair => pair.Key,
                         StringComparer.Ordinal))
            {
                WriteIdentityValue(writer, pair.Key);
                WriteIdentityValue(writer, pair.Value);
            }
        }

        return Convert.ToHexString(SHA256.HashData(material.ToArray()));
    }

    private async Task ObserveModelCompletionAsync(
        ILlmProvider provider,
        ModelState state,
        ModelWorker worker)
    {
        IReadOnlyList<string>? completed = null;
        try
        {
            completed = await worker.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Physical model discovery worker failed for {Provider}", provider.ProviderName);
        }
        finally
        {
            lock (state.Gate)
            {
                if (ReferenceEquals(state.ActiveWorker, worker))
                {
                    if (completed is not null)
                        state.LastKnownResult = Clone(completed);
                    if (completed is not null)
                        state.LastCompletedConfigurationIdentity = worker.ConfigurationIdentity;
                    if (completed is not null)
                        state.LastCompletedAtUtc = _timeProvider.GetUtcNow();
                    state.ActiveWorker = null;
                }
            }
            ReleaseWorkerOnce(worker.Lease, worker.Ownership);
            worker.CompletionObserved.TrySetResult();
        }
    }

    private async Task ObserveConnectivityCompletionAsync(
        ILlmProvider provider,
        ConnectivityState state,
        ConnectivityWorker worker)
    {
        LlmProviderConnectivityResult? completed = null;
        try
        {
            completed = await worker.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Physical connectivity worker failed for {Provider}", provider.ProviderName);
        }
        finally
        {
            lock (state.Gate)
            {
                if (ReferenceEquals(state.ActiveWorker, worker))
                {
                    if (completed is not null)
                        state.LastKnownResult = completed;
                    if (completed is not null)
                        state.LastCompletedConfigurationIdentity = worker.ConfigurationIdentity;
                    if (completed is not null)
                        state.LastCompletedAtUtc = _timeProvider.GetUtcNow();
                    state.ActiveWorker = null;
                }
            }
            ReleaseWorkerOnce(worker.Lease, worker.Ownership);
            worker.CompletionObserved.TrySetResult();
        }
    }

    private ModelWorker? StartModelWorker(
        ILlmProvider provider,
        ModelState state,
        string identity,
        bool highPriority,
        DateTimeOffset now)
    {
        var lease = TryReserveWorker(highPriority);
        if (lease is null)
            return null;

        try
        {
            var task = Task.Run(async () => NormalizeModels(
                await provider.GetAvailableModelsAsync().ConfigureAwait(false)));
            var worker = new ModelWorker(identity, task, lease.Value, now);
            state.ActiveWorker = worker;
            _ = ObserveModelCompletionAsync(provider, state, worker);
            return worker;
        }
        catch
        {
            ReleaseWorker(lease.Value);
            throw;
        }
    }

    private ConnectivityWorker? StartConnectivityWorker(
        ILlmProvider provider,
        ConnectivityState state,
        string identity,
        bool highPriority,
        DateTimeOffset now)
    {
        var lease = TryReserveWorker(highPriority);
        if (lease is null)
            return null;

        try
        {
            var task = Task.Run(async () =>
            {
                var (healthy, message) = await provider.TestConnectivityAsync().ConfigureAwait(false);
                return new LlmProviderConnectivityResult(
                    true,
                    healthy,
                    RoutingDiagnosticSanitizer.Sanitize(message, 300)
                    ?? (healthy ? "Provider is reachable." : "Provider is unavailable."));
            });
            var worker = new ConnectivityWorker(identity, task, lease.Value, now);
            state.ActiveWorker = worker;
            _ = ObserveConnectivityCompletionAsync(provider, state, worker);
            return worker;
        }
        catch
        {
            ReleaseWorker(lease.Value);
            throw;
        }
    }

    private void ReleaseHungWorkerLease(
        WorkerLease lease,
        LeaseOwnership ownership,
        string operation,
        string providerName,
        DateTimeOffset startedAtUtc,
        DateTimeOffset now)
    {
        if (!ownership.TryTake())
            return;

        ReleaseWorker(lease);
        _logger.LogWarning(
            "A {Operation} worker for {Provider} has been running for {Age} and is treated as hung; it no longer counts against discovery capacity",
            operation,
            providerName,
            now - startedAtUtc);
    }

    private WorkerLease? TryReserveWorker(bool highPriority)
    {
        lock (_capacityGate)
        {
            if (_generalWorkers < _maximumGeneralWorkers)
            {
                _generalWorkers++;
                return new WorkerLease(false);
            }
            if (highPriority && _priorityEscapeWorkers < _maximumPriorityEscapeWorkers)
            {
                _priorityEscapeWorkers++;
                return new WorkerLease(true);
            }
            return null;
        }
    }

    private void ReleaseWorker(WorkerLease lease)
    {
        lock (_capacityGate)
        {
            if (lease.UsesPriorityEscape)
                _priorityEscapeWorkers--;
            else
                _generalWorkers--;
        }
    }

    private void ReleaseWorkerOnce(WorkerLease lease, LeaseOwnership ownership)
    {
        if (ownership.TryTake())
            ReleaseWorker(lease);
    }

    private bool IsHung(DateTimeOffset startedAtUtc, DateTimeOffset now)
        => now - startedAtUtc >= _hungWorkerBound;

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    private LlmProviderModelCatalogResult ModelSnapshot(
        ModelState state,
        string configurationIdentity,
        string fallbackMessage,
        bool deferredWhenUnavailable = false)
    {
        lock (state.Gate)
        {
            return state.LastCompletedAtUtc is { } completedAt
                   && string.Equals(
                       state.LastCompletedConfigurationIdentity,
                       configurationIdentity,
                       StringComparison.Ordinal)
                   && _timeProvider.GetUtcNow() - completedAt < _modelCacheLifetime
                ? CompletedModelSnapshot(
                    state.LastKnownResult,
                    "Using a recent provider model catalog while refresh is pending.")
                : LlmProviderModelCatalogResult.Unavailable(
                    fallbackMessage,
                    deferred: deferredWhenUnavailable);
        }
    }

    private static LlmProviderModelCatalogResult CompletedModelSnapshot(
        IReadOnlyList<string> models,
        string message)
        => new(true, Clone(models), message);

    private LlmProviderConnectivityResult ConnectivitySnapshot(
        ConnectivityState state,
        string configurationIdentity,
        string fallbackMessage,
        bool deferredWhenUnavailable = false)
    {
        lock (state.Gate)
        {
            return state.LastCompletedAtUtc is { } completedAt
                   && string.Equals(
                       state.LastCompletedConfigurationIdentity,
                       configurationIdentity,
                       StringComparison.Ordinal)
                   && _timeProvider.GetUtcNow() - completedAt < _connectivityCacheLifetime
                   && state.LastKnownResult is { } cached
                ? cached
                : LlmProviderConnectivityResult.Unavailable(
                    fallbackMessage,
                    deferred: deferredWhenUnavailable);
        }
    }

    private static IReadOnlyList<string> NormalizeModels(IEnumerable<string>? models)
        => models?
               .Where(static model => !string.IsNullOrWhiteSpace(model))
               .Select(static model => model.Trim())
               .Distinct(StringComparer.OrdinalIgnoreCase)
               .OrderBy(static model => model, StringComparer.OrdinalIgnoreCase)
               .ToArray()
           ?? [];

    private static IReadOnlyList<string> Clone(IReadOnlyList<string> models)
        => models.Count == 0 ? [] : models.ToArray();

    private static string NormalizeConfigurationIdentity(string? configurationIdentity)
    {
        // Hash caller-supplied material once more before it reaches any cache or
        // diagnostic state. This preserves privacy even if a future caller
        // accidentally passes raw configuration material instead of the output
        // from CreateConfigurationIdentity.
        var material = string.IsNullOrWhiteSpace(configurationIdentity)
            ? "<DEFAULT-PROVIDER-CONFIGURATION>"
            : configurationIdentity;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static void WriteIdentityValue(BinaryWriter writer, string? value)
    {
        if (value is null)
        {
            writer.Write(-1);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private readonly record struct WorkerLease(bool UsesPriorityEscape);

    /// <summary>
    /// One-shot ownership of a worker's capacity lease. Both the normal
    /// completion observer and the hung-worker watchdog may try to release the
    /// same lease; whichever runs first wins and the other becomes a no-op, so
    /// capacity accounting can never double-decrement.
    /// </summary>
    private sealed class LeaseOwnership
    {
        private int _released;
        internal bool TryTake() => Interlocked.Exchange(ref _released, 1) == 0;
    }

    private sealed record ModelWorker(
        string ConfigurationIdentity,
        Task<IReadOnlyList<string>> Task,
        WorkerLease Lease,
        DateTimeOffset StartedAtUtc)
    {
        internal TaskCompletionSource CompletionObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal LeaseOwnership Ownership { get; } = new();
    }

    private sealed record ConnectivityWorker(
        string ConfigurationIdentity,
        Task<LlmProviderConnectivityResult> Task,
        WorkerLease Lease,
        DateTimeOffset StartedAtUtc)
    {
        internal TaskCompletionSource CompletionObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal LeaseOwnership Ownership { get; } = new();
    }

    private sealed class ModelState
    {
        internal object Gate { get; } = new();
        internal ModelWorker? ActiveWorker { get; set; }
        internal IReadOnlyList<string> LastKnownResult { get; set; } = [];
        internal string? LastCompletedConfigurationIdentity { get; set; }
        internal DateTimeOffset? LastCompletedAtUtc { get; set; }
    }

    private sealed class ConnectivityState
    {
        internal object Gate { get; } = new();
        internal ConnectivityWorker? ActiveWorker { get; set; }
        internal LlmProviderConnectivityResult? LastKnownResult { get; set; }
        internal string? LastCompletedConfigurationIdentity { get; set; }
        internal DateTimeOffset? LastCompletedAtUtc { get; set; }
    }
}
