using System.Collections.Concurrent;

namespace SwarmCore.Agent.ToolUseEngine;

/// <summary>
/// A durable tool scheduler over a host-chosen journal folder, for hosts without their own factory (the CRM web
/// app). Each scheduler partition gets its own subfolder, journal values are protected by the host's protector,
/// and admission is immediate. Retrying an operation id replays its recorded outcome instead of acting twice.
/// Each folder gets a background retention sweep at most once a day per process.
/// </summary>
public sealed class FileToolSchedulerFactory(Func<AgentSession, string> directoryFor, IToolJournalProtector protector, ToolSchedulerOptions? options = null)
    : IToolSchedulerFactory
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromDays(1);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> LastSwept = new(StringComparer.OrdinalIgnoreCase);
    private readonly ToolSchedulerOptions _options = options ?? new ToolSchedulerOptions();
    private readonly ToolSchedulerCapacity _capacity = new(options ?? new ToolSchedulerOptions());

    public bool NativeAsyncEnabled => _options.EnableNativeAsync;

    public DurableToolScheduler Create(AgentSession session)
    {
        var directory = directoryFor(session);
        Directory.CreateDirectory(directory);
        var store = new FileScheduledToolStore(directory, protector);
        var key = Path.GetFullPath(directory);
        var now = DateTimeOffset.UtcNow;
        if (!LastSwept.TryGetValue(key, out var last) || now - last >= SweepInterval)
        {
            LastSwept[key] = now;
            _ = SweepAsync(store, now - _options.JournalRetention);
        }
        return new DurableToolScheduler(store, _options, ImmediateAdmission.Instance, capacity: _capacity);
    }

    private static async Task SweepAsync(FileScheduledToolStore store, DateTimeOffset finishedBefore)
    {
        try { await Task.Run(() => store.PruneAsync(finishedBefore, CancellationToken.None)).ConfigureAwait(false); }
        catch (Exception)
        {
            // Retention is maintenance only: the sweep preserves every record it cannot settle,
            // and the next sweep retries.
        }
    }

    private sealed class ImmediateAdmission : IScheduledToolAdmission
    {
        public static readonly ImmediateAdmission Instance = new();
        public Task<IAsyncDisposable> AdmitAsync(ScheduledToolRecord call, CancellationToken ct) => Task.FromResult<IAsyncDisposable>(Lease.Instance);
        private sealed class Lease : IAsyncDisposable
        {
            public static readonly Lease Instance = new();
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
