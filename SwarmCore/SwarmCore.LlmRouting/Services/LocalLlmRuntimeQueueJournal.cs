using System.Diagnostics;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Abstractions.Processes;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// Each live request owns one ephemeral heartbeat file. No shared-row updates,
/// prompt text, control authority or inference decisions flow through this journal.
/// </summary>
internal sealed class LocalLlmRuntimeQueueJournal
{
    internal static readonly TimeSpan Freshness = TimeSpan.FromSeconds(15);
    private readonly string _directory;
    private readonly ExactProcessIdentity? _process = ExactProcessIdentityInspector.CaptureCurrent();
    private readonly Func<DateTimeOffset> _now;
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public LocalLlmRuntimeQueueJournal(string key, string? directory = null, Func<DateTimeOffset>? now = null)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        // Match the named mutex's OS session namespace. A terminal-launched
        // process in another Unix session does not contend for this same slot.
        using var process = Process.GetCurrentProcess();
        _directory = Path.Combine(directory ?? Path.Combine(Path.GetTempPath(), "SwarmCore", "local-runtime-queue"),
            "session-" + process.SessionId, digest);
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
                    if (row is null || row.SchemaVersion != 1 || !Enum.IsDefined(row.Phase)
                        || row.Id + ".json" != Path.GetFileName(file)) { partial = true; continue; }
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

    internal sealed class Entry : IDisposable
    {
        private readonly LocalLlmRuntimeQueueJournal _journal;
        private readonly object _gate = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _writer;
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
                LocalLlmRuntimeQueuePhase.WaitingInApp, now, now, now);
            journal._entries.TryAdd(_row.Id, this);
            // All disk I/O, including cleanup, stays off a caller's dispatcher.
            _writer = Task.Run(WriteLoopAsync);
        }

        public LocalLlmRuntimeQueueItem? Read()
        {
            lock (_gate) return _disposed ? null : _row with { UpdatedUtc = _journal._now() };
        }
        internal Task Completion => _writer;

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
                    try { AtomicJsonFile.Write(path, row); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    await Task.Delay(TimeSpan.FromSeconds(1), _stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            finally
            {
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
