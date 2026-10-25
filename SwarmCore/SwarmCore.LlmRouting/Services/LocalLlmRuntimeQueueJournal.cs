using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Abstractions.Processes;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// Each live request owns one ephemeral scheduling heartbeat. The physical lease holder alone updates service history.
/// Prompt text, credentials, executable work and control authority never flow through this journal.
/// </summary>
internal sealed class LocalLlmRuntimeQueueJournal
{
    internal static readonly TimeSpan Freshness = TimeSpan.FromSeconds(15);
    private const int MaximumOwnerKeyLength = 4096;
    private const long MaximumHistoryBytes = 16 * 1024 * 1024;
    private readonly string _directory;
    private readonly ExactProcessIdentity? _process = ExactProcessIdentityInspector.CaptureCurrent();
    private readonly Func<DateTimeOffset> _now;
    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private string HistoryPath => Path.Combine(_directory, "scheduler", "service.json");
    internal string DeviceLeasePath => Path.Combine(_directory, "device.lock");

    public LocalLlmRuntimeQueueJournal(string key, string? directory = null, Func<DateTimeOffset>? now = null)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        // GUI, terminal and helper hosts can have different OS sessions and TMPDIRs.
        // Their physical inference device is shared, so the new gate and election
        // use the OS user's stable data directory without either launch namespace.
        _directory = Path.Combine(directory ?? Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "SwarmCore", "local-runtime-queue"),
            digest);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public Entry Begin(LocalLlmRuntimeRequest request) => new(this, request, LocalLlmRuntimeOwnerScope.Current);

    public LocalLlmRuntimeQueueSnapshot Read()
    {
        var items = _entries.Values.Select(x => x.Read()).OfType<LocalLlmRuntimeQueueItem>().ToList();
        var partial = false;
        try
        {
            if (!Directory.Exists(_directory)) return new(items, items.Count > 0);
            // Bounded diagnostics: never enumerate an unbounded abandoned registry.
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json").Take(257))
            {
                if (items.Count == 256) { partial = true; break; }
                try
                {
                    if (new FileInfo(file).Length > 16384) { partial = true; continue; }
                    var row = AtomicJsonFile.Read<LocalLlmRuntimeQueueItem>(file);
                    if (row is null || row.SchemaVersion is not (1 or 2) || !Enum.IsDefined(row.Phase)
                        || !Enum.IsDefined(row.SchedulingClass)
                        || LocalLlmFairScheduler.OwnerKey(row).Length > MaximumOwnerKeyLength
                        || row.Id + ".json" != Path.GetFileName(file)) { partial = true; continue; }
                    if (row.SchemaVersion == 1) partial = true;
                    if (_entries.ContainsKey(row.Id)) continue;
                    using var inspection = ExactProcessIdentityInspector.InspectInstance(new(row.ProcessId, row.ProcessStartedUtc));
                    if (inspection.State is ExactProcessInspectionState.Absent or ExactProcessInspectionState.IdentityMismatch)
                    {
                        File.Delete(file); // Unique request IDs are never reused; this exact writer is gone.
                        continue;
                    }
                    if (inspection.State != ExactProcessInspectionState.Matches) { partial = true; continue; }
                    var age = _now() - row.UpdatedUtc;
                    if (age < -Freshness || age > Freshness) { partial = true; continue; }
                    items.Add(row);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
                { partial = true; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { partial = true; }
        return new(items.OrderBy(x => x.Phase == LocalLlmRuntimeQueuePhase.UsingModel ? 0 : 1)
            .ThenBy(x => x.Interactivity == LlmInteractivity.UserWaiting ? 0 : 1).ThenBy(x => x.QueuedUtc).ToArray(), partial);
    }

    internal Dictionary<string, long> ReadServiceHistory()
    {
        if (!File.Exists(HistoryPath)) return new(StringComparer.Ordinal);
        if (new FileInfo(HistoryPath).Length > MaximumHistoryBytes)
            throw new InvalidDataException("Local AI scheduling history exceeds its bounded size.");
        var state = AtomicJsonFile.Read<ServiceHistory>(HistoryPath)
            ?? throw new InvalidDataException("Local AI scheduling history is unreadable.");
        if (state.Schema != 1 || state.Sequence < 0 || state.LastServed is null || state.LastServed.Count > 512
            || state.LastServed.Any(x => x.Key.Length > MaximumOwnerKeyLength || x.Value < 0 || x.Value > state.Sequence))
            throw new InvalidDataException("Local AI scheduling history is invalid.");
        return new(state.LastServed, StringComparer.Ordinal);
    }

    // Only the holder of the matching physical runtime mutex may call this writer.
    // Re-read under that mutex; do not overwrite from an unchecked election snapshot.
    internal void RecordService(LocalLlmRuntimeQueueItem row)
    {
        var owner = LocalLlmFairScheduler.OwnerKey(row);
        if (owner.Length > MaximumOwnerKeyLength)
            throw new InvalidDataException("Local AI scheduling owner exceeds its bounded size.");
        var history = ReadServiceHistory();
        var sequence = history.Count == 0 ? 0 : history.Values.Max();
        if (sequence == long.MaxValue)
        {
            var ordered = history.OrderBy(x => x.Value).ToArray();
            history = ordered.Select((x, index) => (x.Key, Value: (long)index + 1))
                .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
            sequence = history.Count;
        }
        history[owner] = sequence + 1;
        var state = new ServiceHistory(1, sequence + 1,
            history.OrderByDescending(x => x.Value).Take(512).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal));
        AtomicJsonFile.Write(HistoryPath, state);
    }

    private sealed record ServiceHistory(int Schema, long Sequence, Dictionary<string, long> LastServed);

    internal sealed class Entry : IDisposable
    {
        private readonly LocalLlmRuntimeQueueJournal _journal;
        private readonly object _gate = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _writer;
        private readonly TaskCompletionSource<bool> _published = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private LocalLlmRuntimeQueueItem _row;
        private bool _disposed;

        internal Entry(LocalLlmRuntimeQueueJournal journal, LocalLlmRuntimeRequest request, string? owner)
        {
            _journal = journal;
            var now = journal._now();
            _row = new(Guid.NewGuid().ToString("N"), journal._process?.ProcessId ?? Environment.ProcessId,
                journal._process?.StartedAtUtc ?? DateTime.MinValue,
                LocalLlmRuntimeOwnerScope.CurrentApplication
                    ?? Path.GetFileNameWithoutExtension(journal._process?.ExecutablePath ?? "Local AI app").Replace('.', ' '),
                owner, request.ProviderName, request.Model, request.WorkloadKind, request.Interactivity,
                request.Context?.Workload ?? LlmWorkloadKind.Unspecified,
                LocalLlmRuntimeQueuePhase.WaitingInApp, now, now, now)
            {
                SchedulingClass = request.SchedulingClass,
                SchedulingOwnerId = LocalLlmRuntimeOwnerScope.CurrentSchedulingOwner,
            };
            if (LocalLlmFairScheduler.OwnerKey(_row).Length > MaximumOwnerKeyLength)
            {
                _stop.Dispose();
                throw new LocalLlmRuntimeBusyException("Local AI queue owner metadata exceeds its bounded size. No prompt was sent for this attempt.");
            }
            journal._entries.TryAdd(_row.Id, this);
            // All disk I/O, including cleanup, stays off a caller's dispatcher.
            _writer = Task.Run(WriteLoopAsync);
        }

        public LocalLlmRuntimeQueueItem? Read()
        {
            lock (_gate) return _disposed ? null : _row with { UpdatedUtc = _journal._now() };
        }
        internal Task Completion => _writer;
        internal async Task WaitUntilPublishedAsync(CancellationToken ct)
        {
            if (!await _published.Task.WaitAsync(ct).ConfigureAwait(false))
                throw new LocalLlmRuntimeBusyException("Local AI could not publish its waiting request. No prompt was sent for this attempt.");
        }

        public void SetPhase(LocalLlmRuntimeQueuePhase phase)
        {
            lock (_gate)
                if (!_disposed) _row = _row with { Phase = phase, PhaseStartedUtc = _journal._now(),
                    AdmissionDeadlineUtc = phase == LocalLlmRuntimeQueuePhase.UsingModel ? null : _row.AdmissionDeadlineUtc };
        }

        public void SetDeadline(DateTimeOffset deadline)
        {
            lock (_gate) if (!_disposed) _row = _row with { AdmissionDeadlineUtc = deadline };
        }

        private async Task WriteLoopAsync()
        {
            var path = Path.Combine(_journal._directory, _row.Id + ".json");
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    LocalLlmRuntimeQueueItem row;
                    lock (_gate) row = _row with { UpdatedUtc = _journal._now() };
                    try
                    {
                        if (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(row).Length > 16384)
                            throw new IOException("Local AI queue metadata exceeds its bounded row size.");
                        AtomicJsonFile.Write(path, row);
                        _published.TrySetResult(true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _published.TrySetResult(false); }
                    await Task.Delay(TimeSpan.FromSeconds(1), _stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            finally
            {
                _published.TrySetResult(false);
                try { File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                _stop.Dispose();
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _journal._entries.TryRemove(_row.Id, out _);
                _stop.Cancel();
            }
        }
    }
}
