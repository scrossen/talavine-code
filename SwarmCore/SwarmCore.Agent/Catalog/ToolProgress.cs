using System.Collections.Concurrent;

namespace SwarmCore.Agent.Catalog;

/// <summary>
/// One progress update from a running tool: what it is working on right now,
/// and optionally how far along it is.
/// </summary>
/// <param name="ToolCallId">The call this update belongs to.</param>
/// <param name="ToolName">Tool that is running, for display.</param>
/// <param name="Status">Human-readable description of the current step.</param>
/// <param name="Fraction">0..1 completion when the tool can compute it.</param>
/// <param name="At">When the update was reported.</param>
public sealed record ToolProgressUpdate(
    string ToolCallId,
    string ToolName,
    string Status,
    double? Fraction,
    DateTimeOffset At);

/// <summary>
/// Ambient identity of the tool call executing on this async flow, so a tool
/// implementation buried several layers deep (a Roslyn script inside a skill
/// inside a tool) can report progress without threading a parameter through
/// every signature.
/// <para>
/// Set by the agent engine immediately before invoking the tool — deliberately
/// NOT across a yield, because an <see cref="AsyncLocal{T}"/> write does not
/// survive into the consumer's execution context (the same trap the engine's
/// turn-context re-assert exists for).
/// </para>
/// </summary>
public static class ToolProgressContext
{
    private static readonly AsyncLocal<string?> CurrentCall = new();

    public static string? CurrentToolCallId
    {
        get => CurrentCall.Value;
        set => CurrentCall.Value = value;
    }
}

/// <summary>
/// Carries progress from a running tool to whoever is displaying it, and a
/// cancel request back. Long work is not a failure: the answer to "this is
/// taking a while" is to show the user what it is doing and let them stop it,
/// not to kill it on a stopwatch.
/// <para>
/// Liveness is measured by silence, not by total runtime. A tool that keeps
/// reporting keeps running for as long as the user lets it; a tool that goes
/// quiet for the silence budget is treated as hung and cancelled.
/// </para>
/// </summary>
public interface IAgentToolProgressBroker
{
    /// <summary>Raised on every accepted update, including cancellation notices.</summary>
    event Action<ToolProgressUpdate>? Updated;

    /// <summary>Tool side: report what is happening now. Never throws.</summary>
    void Report(string toolCallId, string status, double? fraction = null);

    /// <summary>
    /// Tool side, using the ambient call id. A no-op when nothing is tracking,
    /// so tools can report unconditionally.
    /// </summary>
    void ReportCurrent(string status, double? fraction = null);

    /// <summary>
    /// Engine side: track a running call. <paramref name="cancellation"/> fires
    /// when the user cancels or when the tool has been silent for
    /// <paramref name="silenceBudget"/> after its first report. Dispose to stop.
    /// </summary>
    IDisposable Track(
        string toolCallId,
        string toolName,
        CancellationTokenSource cancellation,
        TimeSpan silenceBudget);

    /// <summary>
    /// UI side: the user asked to stop this call. Returns false when the call is
    /// unknown (already finished).
    /// </summary>
    bool Cancel(string toolCallId);

    /// <summary>True when the user cancelled this call rather than it failing.</summary>
    bool WasCancelledByUser(string toolCallId);

    /// <summary>Latest update for a call, when one has been reported.</summary>
    ToolProgressUpdate? TryGetLatest(string toolCallId);
}

/// <summary>
/// Process-local <see cref="IAgentToolProgressBroker"/>. Tracking entries live
/// only for the duration of a tool call; the cancelled-by-user marks are kept
/// briefly afterwards so the engine can label the result correctly.
/// </summary>
public sealed class InMemoryAgentToolProgressBroker : IAgentToolProgressBroker
{
    private const int MaxRememberedCancellations = 64;

    private readonly ConcurrentDictionary<string, Entry> _tracked = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _cancelledOrder = new();
    private readonly ConcurrentDictionary<string, byte> _cancelledByUser = new(StringComparer.Ordinal);

    public event Action<ToolProgressUpdate>? Updated;

    public void Report(string toolCallId, string status, double? fraction = null)
    {
        if (string.IsNullOrWhiteSpace(toolCallId) || string.IsNullOrWhiteSpace(status))
        {
            return;
        }

        if (!_tracked.TryGetValue(toolCallId, out var entry))
        {
            return;
        }

        var update = new ToolProgressUpdate(
            toolCallId,
            entry.ToolName,
            status.Trim(),
            fraction is null ? null : Math.Clamp(fraction.Value, 0d, 1d),
            DateTimeOffset.UtcNow);
        entry.Latest = update;

        // The tool is alive: restart the silence watchdog. Reporting work runs
        // until the user stops it, never on a fixed overall deadline.
        entry.RestartSilenceWatchdog();
        RaiseUpdated(update);
    }

    public void ReportCurrent(string status, double? fraction = null)
    {
        if (ToolProgressContext.CurrentToolCallId is { } id)
        {
            Report(id, status, fraction);
        }
    }

    public IDisposable Track(
        string toolCallId,
        string toolName,
        CancellationTokenSource cancellation,
        TimeSpan silenceBudget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolCallId);
        ArgumentNullException.ThrowIfNull(cancellation);

        // Call ids are not globally unique: CLI providers reuse ids such as
        // "cli_call_1" on every reply. A cancellation remembered from an
        // earlier call must not label this fresh call as user-cancelled.
        _cancelledByUser.TryRemove(toolCallId, out _);

        var entry = new Entry(toolName ?? string.Empty, cancellation, silenceBudget);
        _tracked[toolCallId] = entry;
        return new Registration(this, toolCallId);
    }

    public bool Cancel(string toolCallId)
    {
        if (string.IsNullOrWhiteSpace(toolCallId)
            || !_tracked.TryGetValue(toolCallId, out var entry))
        {
            return false;
        }

        RememberCancellation(toolCallId);
        var update = new ToolProgressUpdate(
            toolCallId,
            entry.ToolName,
            "Stopping — cancelled by you…",
            entry.Latest?.Fraction,
            DateTimeOffset.UtcNow);
        entry.Latest = update;
        RaiseUpdated(update);

        try
        {
            entry.Cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // The call finished between the lookup and the cancel.
            return false;
        }
    }

    public bool WasCancelledByUser(string toolCallId)
        => !string.IsNullOrWhiteSpace(toolCallId) && _cancelledByUser.ContainsKey(toolCallId);

    public ToolProgressUpdate? TryGetLatest(string toolCallId)
        => !string.IsNullOrWhiteSpace(toolCallId) && _tracked.TryGetValue(toolCallId, out var entry)
            ? entry.Latest
            : null;

    private void RaiseUpdated(ToolProgressUpdate update)
    {
        try
        {
            Updated?.Invoke(update);
        }
        catch
        {
            // A display failure must never break the work being displayed.
        }
    }

    private void RememberCancellation(string toolCallId)
    {
        if (!_cancelledByUser.TryAdd(toolCallId, 0))
        {
            return;
        }

        _cancelledOrder.Enqueue(toolCallId);
        while (_cancelledOrder.Count > MaxRememberedCancellations
               && _cancelledOrder.TryDequeue(out var oldest))
        {
            _cancelledByUser.TryRemove(oldest, out _);
        }
    }

    private sealed class Entry : IDisposable
    {
        private readonly TimeSpan _silenceBudget;

        public Entry(string toolName, CancellationTokenSource cancellation, TimeSpan silenceBudget)
        {
            ToolName = toolName;
            Cancellation = cancellation;
            _silenceBudget = silenceBudget;
        }

        public string ToolName { get; }
        public CancellationTokenSource Cancellation { get; }
        public ToolProgressUpdate? Latest { get; set; }

        /// <summary>
        /// (Re)arms the silence watchdog. Armed only once the tool has reported
        /// at least once — a tool that never reports keeps whatever deadline the
        /// engine gave it, so existing tools behave exactly as before.
        /// </summary>
        public void RestartSilenceWatchdog()
        {
            if (_silenceBudget <= TimeSpan.Zero)
            {
                return;
            }

            try
            {
                // Replaces whatever deadline the engine scheduled, so a tool
                // that reports is judged on silence rather than total runtime.
                Cancellation.CancelAfter(_silenceBudget);
            }
            catch (ObjectDisposedException)
            {
                // Call already finished.
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class Registration : IDisposable
    {
        private readonly InMemoryAgentToolProgressBroker _owner;
        private readonly string _toolCallId;
        private bool _disposed;

        public Registration(InMemoryAgentToolProgressBroker owner, string toolCallId)
        {
            _owner = owner;
            _toolCallId = toolCallId;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_owner._tracked.TryRemove(_toolCallId, out var entry))
            {
                entry.Dispose();
            }
        }
    }
}
