using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SwarmCode.Hosting.Agents;
using SwarmCore.Coding.Scheduling;

namespace SwarmCode.Hosting
{
    /// <summary>
    /// Who started a runner turn when it is not the person typing in the session. Host state only, never model output.
    /// </summary>
    /// <param name="Authority">The user-requested authority name (<see cref="SwarmCodeTurnPolicy.Scope"/>).</param>
    /// <param name="Unattended">No person is waiting on the turn: the harness then skips native lanes.</param>
    public sealed record TurnOrigin(string Authority, bool Unattended, IReadOnlyDictionary<string, string>? Metadata = null)
    {
        public const string ScheduleAuthority = "swarmcode-schedule";
        public const string SelfImprovementAuthority = "swarmcode-self-improvement";
        public const string SelfImprovementTrigger = "self-improvement";

        /// <summary>
        /// A scheduled run: the person authored the prompt in SwarmCode, so it runs under their user-requested authority
        /// (SwarmCode is gate-free for the person's own requests), and native lanes run it by default; a schedule
        /// that turned native lanes off runs unattended, so the harness skips them.
        /// </summary>
        public static TurnOrigin Scheduled(string scheduleId, bool allowNativeLanes) => new(ScheduleAuthority, !allowNativeLanes,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["swarmcode.trigger"] = "schedule", ["swarmcode.schedule"] = scheduleId });

        /// <summary>
        /// A self-improvement cycle: SwarmCode repairing or extending its own source while idle. Nobody is watching, so a
        /// question asked during it is answered "no one can answer"; native lanes run it unless the person turned that off.
        /// </summary>
        public static TurnOrigin SelfImprovement(string itemId, bool allowNativeLanes) => new(SelfImprovementAuthority, !allowNativeLanes,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["swarmcode.trigger"] = SelfImprovementTrigger, ["swarmcode.self-improvement"] = itemId });

        /// <summary>True for a turn a self-improvement cycle started (its failures are not reported back into the backlog).</summary>
        public bool IsSelfImprovement => Authority == SelfImprovementAuthority
            || (Metadata is not null && Metadata.TryGetValue("swarmcode.trigger", out var trigger) && trigger == SelfImprovementTrigger);
    }
}

namespace SwarmCode.Hosting.Scheduling
{
    /// <summary>A runner for a scheduled run's session, and how to hand it back once the session is idle.</summary>
    public sealed class ScheduledRunnerLease(ConversationRunner runner, Func<ValueTask> release)
    {
        private int _released;

        public ConversationRunner Runner { get; } = runner;

        public ValueTask ReleaseAsync() => Interlocked.Exchange(ref _released, 1) == 0 ? release() : ValueTask.CompletedTask;
    }

    /// <summary>Where a scheduled run's session gets its runner (the open workspace, or one opened for the run).</summary>
    public interface IScheduledRunWorkspaces
    {
        Task<ScheduledRunnerLease> AcquireAsync(string repositoryRoot, string sessionId, CancellationToken ct);
    }

    /// <summary>
    /// SwarmCode's <see cref="IScheduledRunHost"/>: each run is a new top-level session in the schedule's repository,
    /// titled "⏱ title — date", on the schedule's lane/model or the person's default, with the person's approval and
    /// hand-off preferences. Its turn carries <see cref="TurnOrigin.Scheduled"/>. Scheduled sessions are temporary
    /// (personal memory off): the prompt repeats on every run and no person is there to correct what is learned.
    /// </summary>
    public sealed class SwarmCodeScheduledRunHost(
        WorkspaceSessionCatalog sessions,
        AppPreferencesStore preferences,
        IScheduledRunWorkspaces workspaces,
        TimeProvider? time = null,
        ILogger<SwarmCodeScheduledRunHost>? logger = null) : IScheduledRunHost
    {
        public static string SessionTitle(string title, DateTime firedUtc, TimeZoneInfo? zone = null)
            => $"⏱ {title} — {TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(firedUtc, DateTimeKind.Utc), zone ?? TimeZoneInfo.Local):MMM d, HH:mm}";

        public async Task<ScheduledRunHandle> StartRunAsync(ScheduledRunRequest request, CancellationToken ct)
        {
            if (!Directory.Exists(request.RepositoryRoot)) throw new DirectoryNotFoundException($"The repository folder does not exist: {request.RepositoryRoot}");
            var prefs = preferences.Current;
            var lane = request.Lane ?? prefs.DefaultLane;
            var model = request.Model ?? (request.Lane is null && prefs.DefaultLane is not null ? prefs.DefaultModel : null);
            var fired = request.FiredUtc == default ? (time ?? TimeProvider.System).GetUtcNow().UtcDateTime : request.FiredUtc;
            var session = await sessions.CreateAsync(request.RepositoryRoot, SessionTitle(request.Title, fired), lane, ct, model).ConfigureAwait(false);
            ScheduledRunnerLease lease;
            try
            {
                await sessions.SetTemporaryAsync(session.Id, true, ct).ConfigureAwait(false);
                lease = await workspaces.AcquireAsync(request.RepositoryRoot, session.Id, ct).ConfigureAwait(false);
            }
            catch
            {
                // A run that never started leaves no empty "⏱" session behind (each scheduler retry would add one).
                await DeleteUnstartedSessionAsync(session.Id).ConfigureAwait(false);
                throw;
            }
            var runner = lease.Runner;
            var completion = new TaskCompletionSource<ScheduledRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnFinished(TurnResult result)
            {
                completion.TrySetResult(Outcome(result));
                // The person may have queued follow-ups in the session: hand the runner back only once it is idle.
                if (runner.IsRunning || (runner.QueuedMessages.Count > 0 && !runner.QueuePaused)) return;
                runner.TurnFinished -= OnFinished;
                // Never from inside the runner's own turn: releasing disposes the runner, which waits for that turn.
                _ = Task.Run(async () =>
                {
                    try { await lease.ReleaseAsync().ConfigureAwait(false); }
                    catch (Exception ex) { logger?.LogWarning(ex, "Releasing scheduled session {Session}'s runner failed.", session.Id); }
                });
            }
            runner.TurnFinished += OnFinished;
            if (!runner.TrySend(request.Prompt, lane, prefs.Mode, prefs.HandOffOnRefusal, model,
                    isContinuation: false, origin: TurnOrigin.Scheduled(request.ScheduleId, request.AllowNativeLanes)))
            {
                runner.TurnFinished -= OnFinished;
                await lease.ReleaseAsync().ConfigureAwait(false);
                await DeleteUnstartedSessionAsync(session.Id).ConfigureAwait(false);
                throw new InvalidOperationException("The scheduled session could not start its turn.");
            }
            return new ScheduledRunHandle(session.Id, completion.Task);
        }

        private async Task DeleteUnstartedSessionAsync(string sessionId)
        {
            try { await sessions.DeleteAsync(sessionId, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { logger?.LogWarning(ex, "Could not remove scheduled session {Session} after its run failed to start.", sessionId); }
        }

        internal static ScheduledRunResult Outcome(TurnResult result)
            => result.Cancelled ? new(ScheduleRunOutcome.Cancelled, "Stopped before it finished.")
                : result.Error is { } error ? new(ScheduleRunOutcome.Failed, error.Length <= 300 ? error : error[..297] + "...")
                : result.Completed ? new(ScheduleRunOutcome.Succeeded)
                : new(ScheduleRunOutcome.Failed, "The run ended without completing.");
    }

    /// <summary>
    /// Runners for scheduled sessions. A run in the repository the person has open uses that workspace (the host's
    /// <c>openInCurrentWorkspace</c>), so it shows live in the window. A run in any other repository gets a workspace
    /// opened for it in the background (one per repository, shared by concurrent runs, disposed when its last run's
    /// session is idle), so a schedule fires whichever repository is open and never switches the person's window.
    /// While a background run lives, the host serves its runner for that session (<see cref="TryGetRunner"/>).
    /// </summary>
    public sealed class ScheduledRunWorkspaces(
        Func<string, string, CancellationToken, Task<ConversationRunner?>> openInCurrentWorkspace,
        Func<string, CancellationToken, Task<WorkspaceHost>> createBackgroundWorkspace,
        ILogger<ScheduledRunWorkspaces>? logger = null) : IScheduledRunWorkspaces, IConversationRunnerSource, IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Pooled> _hosts = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ConversationRunner> _runners = new(StringComparer.Ordinal);
        private bool _disposed;

        /// <summary>Raised on any thread when a background runner appears or is released.</summary>
        public event Action? Changed;

        public int BackgroundWorkspaceCount { get { lock (_gate) return _hosts.Count; } }

        /// <summary>The background runner of a scheduled session while its run lives; null otherwise.</summary>
        public ConversationRunner? TryGetRunner(string conversationId) => _runners.GetValueOrDefault(conversationId);

        /// <summary>Every runner serving a background run right now (scheduled runs and self-improvement cycles).</summary>
        public IReadOnlyCollection<ConversationRunner> BackgroundRunners => _runners.Values.ToList();

        Task<ConversationRunner?> IConversationRunnerSource.GetRunnerAsync(string conversationId, CancellationToken ct)
            => Task.FromResult(TryGetRunner(conversationId));

        public async Task<ScheduledRunnerLease> AcquireAsync(string repositoryRoot, string sessionId, CancellationToken ct)
        {
            if (await openInCurrentWorkspace(repositoryRoot, sessionId, ct).ConfigureAwait(false) is { } current)
                return new ScheduledRunnerLease(current, () => ValueTask.CompletedTask);

            var key = Normalize(repositoryRoot);
            Pooled pooled;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_hosts.TryGetValue(key, out pooled!))
                {
                    pooled = new Pooled(new Lazy<Task<WorkspaceHost>>(() => createBackgroundWorkspace(key, CancellationToken.None)));
                    _hosts[key] = pooled;
                }
                pooled.References++;
            }
            ConversationRunner runner;
            try
            {
                var host = await pooled.Host.Value.WaitAsync(ct).ConfigureAwait(false);
                runner = host.OpenSession(sessionId);
                try { await runner.LoadHistoryAsync(ct).ConfigureAwait(false); }
                catch
                {
                    await runner.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            catch
            {
                await ReleaseHostAsync(key, pooled).ConfigureAwait(false);
                throw;
            }
            _runners[sessionId] = runner;
            Changed?.Invoke();
            return new ScheduledRunnerLease(runner, async () =>
            {
                // Dispose first (drains the transcript journal), then stop serving it, so a window that opens the
                // session afterwards reads complete history. A disposal that throws (a faulted turn) still stops
                // serving the runner and releases the pooled workspace, or it would outlive every run.
                try { await runner.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { logger?.LogWarning(ex, "Disposing scheduled session {Session}'s runner failed.", sessionId); }
                finally
                {
                    _runners.TryRemove(new KeyValuePair<string, ConversationRunner>(sessionId, runner));
                    Changed?.Invoke();
                    await ReleaseHostAsync(key, pooled).ConfigureAwait(false);
                }
            });
        }

        private async Task ReleaseHostAsync(string key, Pooled pooled)
        {
            lock (_gate)
            {
                if (--pooled.References > 0) return;
                if (_hosts.TryGetValue(key, out var existing) && ReferenceEquals(existing, pooled)) _hosts.Remove(key);
            }
            await DisposeHostAsync(pooled).ConfigureAwait(false);
        }

        private static async Task DisposeHostAsync(Pooled pooled)
        {
            if (!pooled.Host.IsValueCreated) return;
            try { await (await pooled.Host.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { /* creation failed or disposal raced: nothing more to release */ }
        }

        private static string Normalize(string path)
        {
            var full = Path.GetFullPath(path);
            return string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase)
                ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        public async ValueTask DisposeAsync()
        {
            List<Pooled> hosts;
            lock (_gate)
            {
                _disposed = true;
                hosts = _hosts.Values.ToList();
                _hosts.Clear();
            }
            foreach (var runner in _runners.Values) runner.Dispose();
            foreach (var runner in _runners.Values) await runner.DisposeAsync().ConfigureAwait(false);
            _runners.Clear();
            foreach (var pooled in hosts) await DisposeHostAsync(pooled).ConfigureAwait(false);
        }

        private sealed class Pooled(Lazy<Task<WorkspaceHost>> host)
        {
            public Lazy<Task<WorkspaceHost>> Host { get; } = host;
            public int References { get; set; }
        }
    }
}
