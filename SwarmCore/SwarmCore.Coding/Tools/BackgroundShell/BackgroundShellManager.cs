using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmCore.Abstractions.Processes;
using SwarmCore.Missions;
using SwarmCore.Services.Abstractions;

namespace SwarmCore.Coding.Tools.BackgroundShell;

public enum BackgroundShellStatus
{
    Running,
    /// <summary>The process ended on its own; <see cref="BackgroundShellInfo.ExitCode"/> is set.</summary>
    Exited,
    /// <summary>Stopped by the conversation, the person, session deletion, or host shutdown (process tree killed).</summary>
    Stopped,
}

/// <summary>A background shell as callers see it. <see cref="Command"/> keeps <c>{{secret:name}}</c> references unexpanded.</summary>
public sealed record BackgroundShellInfo(
    string Id, string Owner, string Name, string Command, string WorkingDirectory, BackgroundShellStatus Status,
    int? ExitCode, DateTime StartedUtc, DateTime? EndedUtc, long OutputCharacters)
{
    public bool IsRunning => Status == BackgroundShellStatus.Running;
}

/// <summary>One output read: new output since the conversation's last read (bounded), and why the read returned.</summary>
public sealed record BackgroundShellRead(BackgroundShellInfo Shell, string Output, long OmittedCharacters, bool PatternMatched, bool TimedOut);

public enum BackgroundShellChangeKind { Started, Output, Exited, Stopped, Removed }

/// <summary>Raised on a background thread. <see cref="BackgroundShellChangeKind.Output"/> is coalesced (at most a few per second per shell).</summary>
public sealed record BackgroundShellChange(string Owner, string ShellId, BackgroundShellChangeKind Kind);

/// <summary>
/// Long-running shell commands a conversation starts and keeps working beside (dev servers, watchers, long builds).
/// Host-neutral: the owner is the trusted conversation id the caller supplies (never model input), and a conversation
/// sees and stops only its own shells. Output from stdout and stderr is merged into a bounded head+tail buffer with
/// resolved <c>{{secret:name}}</c> values scrubbed before buffering, so neither the model nor a host UI sees them.
/// <para>
/// Not durable: shells live only as long as this manager. Disposing it (the workspace container) kills every process
/// tree, and a host restart never resumes or reruns a command; the conversation starts it again if it still needs it.
/// </para>
/// </summary>
public sealed class BackgroundShellManager : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan OutputNotifyDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ExitDrainWait = TimeSpan.FromSeconds(1);
    private const int MaxCommandLength = 32 * 1024;
    private const int MaxNameLength = 80;

    private readonly Func<BackgroundShellOptions> _options;
    private readonly Func<ICredentialStore?> _credentials;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, Shell> _shells = new(StringComparer.Ordinal);
    private bool _disposed;

    public BackgroundShellManager(Func<BackgroundShellOptions> options, Func<ICredentialStore?>? credentials = null, ILogger<BackgroundShellManager>? logger = null)
    {
        _options = options;
        _credentials = credentials ?? (() => null);
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Started, exited, stopped, removed, and (coalesced) new output. Raised on a background thread.</summary>
    public event Action<BackgroundShellChange>? Changed;

    private BackgroundShellOptions Options => _options().Normalized();

    /// <summary>
    /// Starts <paramref name="command"/> through the platform shell in <paramref name="workingDirectory"/> (already resolved
    /// and fenced by the caller) and returns at once. Stdin is closed.
    /// </summary>
    public async Task<BackgroundShellInfo> StartAsync(string owner, string command, string workingDirectory, string? name = null, CancellationToken ct = default)
    {
        RequireOwner(owner);
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("command is required.");
        if (command.Length > MaxCommandLength || command.Contains('\0')) throw new ArgumentException("command is too long or contains a NUL character.");
        if (!Directory.Exists(workingDirectory)) throw new ArgumentException($"Working directory does not exist: {workingDirectory}");
        if (MissionPrimitiveFence.IsMission)
            throw new InvalidOperationException("Background shells are not available to mission work; use shell.exec with a timeout.");

        var options = Options;
        var shell = new Shell(this, NewId(), owner, Label(name, command), command.Trim(), workingDirectory, options);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var running = _shells.Values.Where(s => s.IsRunning).ToList();
            if (running.Count(s => s.Owner == owner) >= options.MaxRunningPerConversation)
                throw new InvalidOperationException($"This conversation already has {options.MaxRunningPerConversation} background shells running. Stop one with shell.background.stop first.");
            if (running.Count >= options.MaxRunningTotal)
                throw new InvalidOperationException($"Talavine Code is already running {options.MaxRunningTotal} background shells. Stop one with shell.background.stop first.");
            _shells[shell.Id] = shell; // reserves the slot while the process starts
        }

        try
        {
            var secrets = new ShellSecretReferences(_credentials());
            var expanded = await secrets.ExpandAsync(command, ct).ConfigureAwait(false) ?? command;
            await secrets.ThrowIfMissingAsync(ct).ConfigureAwait(false);
            var start = new ProcessStartInfo
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true, // a child that prompts must not block on the host terminal
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            // Hold Windows at stdin until its kill-on-close job is attached. A
            // process-tree snapshot alone can miss descendants spawned during Stop.
            var gated = OperatingSystem.IsWindows();
            PlatformShell.SetCommand(start, gated ? "set /p SWARMCODE_BACKGROUND_START= >nul & " + expanded : expanded);
            PlatformShell.UseVerbatimCommandLine(start); // missions are refused above
            if (gated) start.Arguments = "/d " + start.Arguments; // no AutoRun command before containment
            PlatformShell.RemoveParentSdkOverrides(start);
            var process = await Task.Run(() =>
            {
                var child = ChildProcessLauncher.Start(start);
                ChildProcessLifetimeContainment? containment = null;
                try
                {
                    if (gated)
                    {
                        var diagnostics = child.DiagnosticsProcess
                            ?? throw new InvalidOperationException("Could not inspect the background shell for containment.");
                        containment = ChildProcessLifetimeContainment.Attach(diagnostics, "coding-background-shell", _logger);
                    }
                    shell.OwnContainment(containment);
                    if (gated)
                    {
                        child.StandardInput.WriteLine();
                        child.StandardInput.Flush();
                    }
                    return child;
                }
                catch
                {
                    containment?.Dispose();
                    try { child.Kill(entireProcessTree: true); } catch { }
                    child.Dispose();
                    throw;
                }
            }, ct).ConfigureAwait(false);
            shell.Attach(process, secrets);
        }
        catch
        {
            lock (_gate) _shells.Remove(shell.Id);
            throw;
        }

        bool killNow;
        lock (_gate) killNow = _disposed;
        if (killNow) { shell.Stop(); throw new ObjectDisposedException(nameof(BackgroundShellManager)); }
        _logger.LogInformation("Background shell {ShellId} started for {Owner} in {Directory}", shell.Id, owner, workingDirectory);
        Raise(new(owner, shell.Id, BackgroundShellChangeKind.Started));
        Prune(owner, options.RetainFinishedPerConversation);
        return shell.Info();
    }

    /// <summary>
    /// Reads output the conversation has not read yet (bounded; long output keeps head and tail). With
    /// <paramref name="waitForPattern"/> (a regular expression) it waits until new output matches, the process ends, or the
    /// wait runs out; with only <paramref name="waitSeconds"/> it waits until the process ends or the wait runs out.
    /// Waits are capped by <see cref="BackgroundShellOptions.MaxWaitSeconds"/>.
    /// </summary>
    public async Task<BackgroundShellRead> ReadAsync(string owner, string id, string? waitForPattern = null, int? waitSeconds = null, CancellationToken ct = default)
    {
        var shell = Find(owner, id);
        var options = Options;
        Regex? pattern = null;
        if (!string.IsNullOrEmpty(waitForPattern))
        {
            try { pattern = new Regex(waitForPattern, RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)); }
            catch (ArgumentException ex) { throw new ArgumentException($"waitForPattern is not a valid regular expression: {ex.Message}"); }
        }
        var seconds = waitSeconds is { } requested ? Math.Max(0, requested) : pattern is not null ? options.DefaultPatternWaitSeconds : 0;
        var wait = TimeSpan.FromSeconds(Math.Min(seconds, options.MaxWaitSeconds));
        var deadline = DateTime.UtcNow + wait;

        var matched = false;
        while (true)
        {
            Task signal;
            lock (shell.Gate)
            {
                if (pattern is not null && shell.ScanForPattern(pattern)) matched = true;
                signal = shell.Signal;
            }
            if (matched || wait <= TimeSpan.Zero) break;
            if (!shell.IsRunning)
            {
                // Output can still be in flight when the exit is observed; give the pipes a moment to drain once.
                await shell.WaitForDrainAsync(ExitDrainWait, ct).ConfigureAwait(false);
                lock (shell.Gate) { if (pattern is not null && shell.ScanForPattern(pattern)) matched = true; }
                break;
            }
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            await Task.WhenAny(signal, Task.Delay(remaining, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }

        lock (shell.Gate)
        {
            var output = shell.TakeUnread(options.MaxReadCharacters, out var omitted);
            var timedOut = !matched && wait > TimeSpan.Zero && shell.IsRunning;
            return new BackgroundShellRead(shell.Info(), output, omitted, matched, timedOut);
        }
    }

    /// <summary>Recent output for a host UI (head and tail when long). Does not move the conversation's read position.</summary>
    public string Peek(string owner, string id, int maxCharacters = 8 * 1024)
    {
        var shell = Find(owner, id);
        lock (shell.Gate) return shell.Buffer.Read(0, Math.Max(256, maxCharacters), out _);
    }

    /// <summary>Kills the shell's process tree. Stopping a finished shell returns it unchanged.</summary>
    public BackgroundShellInfo Stop(string owner, string id)
    {
        var shell = Find(owner, id);
        if (shell.Stop()) Raise(new(owner, id, BackgroundShellChangeKind.Stopped));
        return shell.Info();
    }

    /// <summary>Stops every shell the conversation owns and forgets them (session deleted).</summary>
    public int StopAll(string owner)
    {
        List<Shell> owned;
        lock (_gate)
        {
            owned = _shells.Values.Where(s => s.Owner == owner).ToList();
            foreach (var shell in owned) _shells.Remove(shell.Id);
        }
        var stopped = 0;
        foreach (var shell in owned)
        {
            if (shell.Stop()) stopped++;
            shell.DisposeProcess();
            Raise(new(owner, shell.Id, BackgroundShellChangeKind.Removed));
        }
        return stopped;
    }

    /// <summary>Forgets a finished shell (a running one must be stopped first).</summary>
    public bool Forget(string owner, string id)
    {
        var shell = Find(owner, id);
        if (shell.IsRunning) throw new InvalidOperationException("Stop the shell before removing it.");
        lock (_gate) if (!_shells.Remove(id)) return false;
        shell.DisposeProcess();
        Raise(new(owner, id, BackgroundShellChangeKind.Removed));
        return true;
    }

    /// <summary>The conversation's shells, running first, newest first.</summary>
    public IReadOnlyList<BackgroundShellInfo> List(string owner)
    {
        RequireOwner(owner);
        List<Shell> owned;
        lock (_gate) owned = _shells.Values.Where(s => s.Owner == owner).ToList();
        return owned.Select(s => s.Info()).OrderByDescending(s => s.IsRunning).ThenByDescending(s => s.StartedUtc).ToList();
    }

    /// <summary>Running shells across every conversation.</summary>
    public int RunningCount
    {
        get { lock (_gate) return _shells.Values.Count(s => s.IsRunning); }
    }

    private Shell Find(string owner, string id)
    {
        RequireOwner(owner);
        Shell? shell;
        lock (_gate) _shells.TryGetValue(id ?? "", out shell);
        // Another conversation's shell is reported exactly like a missing one.
        if (shell is null || !string.Equals(shell.Owner, owner, StringComparison.Ordinal))
            throw new InvalidOperationException($"No background shell '{id}' in this conversation. Use shell.background.list.");
        return shell;
    }

    private void Prune(string owner, int keep)
    {
        List<Shell> removed;
        lock (_gate)
        {
            removed = _shells.Values.Where(s => s.Owner == owner && !s.IsRunning)
                .OrderByDescending(s => s.StartedUtc).Skip(keep).ToList();
            foreach (var shell in removed) _shells.Remove(shell.Id);
        }
        foreach (var shell in removed)
        {
            shell.DisposeProcess();
            Raise(new(owner, shell.Id, BackgroundShellChangeKind.Removed));
        }
    }

    private void Raise(BackgroundShellChange change)
    {
        try { Changed?.Invoke(change); }
        catch (Exception ex) { _logger.LogWarning(ex, "Background shell change handler failed"); }
    }

    private static void RequireOwner(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new InvalidOperationException("Background shells can be used only inside a conversation turn.");
    }

    private static string NewId() => "bg-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    private static string Label(string? name, string command)
    {
        var label = string.IsNullOrWhiteSpace(name) ? command.Trim() : name.Trim();
        label = new string(label.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        return label.Length <= MaxNameLength ? label : label[..(MaxNameLength - 1)] + "…";
    }

    /// <summary>Kills every process tree. Shells never outlive the host that started them.</summary>
    public void Dispose()
    {
        List<Shell> all;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            all = _shells.Values.ToList();
            _shells.Clear();
        }
        foreach (var shell in all)
        {
            shell.Stop();
            shell.DisposeProcess();
        }
        if (all.Count > 0) _logger.LogInformation("Stopped {Count} background shells at shutdown", all.Count);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class Shell(BackgroundShellManager manager, string id, string owner, string name, string command, string workingDirectory, BackgroundShellOptions options)
    {
        public readonly object Gate = new();
        public string Id { get; } = id;
        public string Owner { get; } = owner;
        public ShellOutputBuffer Buffer { get; } = new(options.HeadCharacters, options.TailCharacters);
        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IChildProcess? _process;
        private ChildProcessLifetimeContainment? _containment;
        private ShellSecretReferences? _secrets;
        private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private BackgroundShellStatus _status = BackgroundShellStatus.Running;
        private int? _exitCode;
        private DateTime? _ended;
        private long _readPosition;
        private long _scanPosition;
        private int _openStreams = 2;
        private int _notifyPending;

        public bool IsRunning { get { lock (Gate) return _status == BackgroundShellStatus.Running; } }

        /// <summary>Completes on the next output or state change. Read under <see cref="Gate"/>.</summary>
        public Task Signal => _signal.Task;

        public void OwnContainment(ChildProcessLifetimeContainment? containment)
        {
            lock (Gate) _containment = containment;
        }

        public void Attach(IChildProcess process, ShellSecretReferences secrets)
        {
            bool alreadyStopped;
            lock (Gate) { _process = process; _secrets = secrets; alreadyStopped = _status != BackgroundShellStatus.Running; }
            if (alreadyStopped)
            {
                Interlocked.Exchange(ref _containment, null)?.Dispose();
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new ObjectDisposedException(nameof(BackgroundShellManager));
            }
            try { process.StandardInput.Close(); } catch { /* no stdin for the child */ }
            process.OutputLineReceived += OnLine;
            process.ErrorLineReceived += OnLine;
            process.Exited += (_, _) => OnExited();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        private void OnLine(string? line)
        {
            if (line is null)
            {
                if (Interlocked.Decrement(ref _openStreams) == 0) _drained.TrySetResult();
                return;
            }
            lock (Gate)
            {
                Buffer.Append((_secrets?.Scrub(line) ?? line) + "\n");
                Pulse();
            }
            if (Interlocked.Exchange(ref _notifyPending, 1) == 0)
                _ = Task.Delay(OutputNotifyDelay).ContinueWith(_ =>
                {
                    Interlocked.Exchange(ref _notifyPending, 0);
                    manager.Raise(new(Owner, Id, BackgroundShellChangeKind.Output));
                }, TaskScheduler.Default);
        }

        private void OnExited()
        {
            bool changed;
            lock (Gate)
            {
                try { _exitCode = _process?.ExitCode; } catch { /* exit code unavailable */ }
                changed = _status == BackgroundShellStatus.Running;
                if (changed) { _status = BackgroundShellStatus.Exited; _ended = DateTime.UtcNow; }
                Pulse();
            }
            if (changed) manager.Raise(new(Owner, Id, BackgroundShellChangeKind.Exited));
            Interlocked.Exchange(ref _containment, null)?.Dispose();
        }

        /// <summary>Kills the process tree; true when this call stopped a running shell.</summary>
        public bool Stop()
        {
            IChildProcess? process;
            lock (Gate)
            {
                if (_status != BackgroundShellStatus.Running) return false;
                _status = BackgroundShellStatus.Stopped;
                _ended = DateTime.UtcNow;
                process = _process;
                Pulse();
            }
            if (process is not null)
            {
                Interlocked.Exchange(ref _containment, null)?.Dispose();
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            }
            return true;
        }

        public void DisposeProcess()
        {
            IChildProcess? process;
            lock (Gate) { process = _process; _process = null; }
            Interlocked.Exchange(ref _containment, null)?.Dispose();
            try { process?.Dispose(); } catch { /* best effort */ }
        }

        public Task WaitForDrainAsync(TimeSpan max, CancellationToken ct)
            => Task.WhenAny(_drained.Task, Task.Delay(max, ct));

        /// <summary>Matches the pattern against output not yet scanned. Call under <see cref="Gate"/>.</summary>
        public bool ScanForPattern(Regex pattern)
        {
            var from = Math.Max(_scanPosition, _readPosition);
            if (from >= Buffer.Written) return false;
            var text = Buffer.Read(from, int.MaxValue, out _);
            _scanPosition = Buffer.Written;
            try { return pattern.IsMatch(text); }
            catch (RegexMatchTimeoutException) { return false; }
        }

        /// <summary>Unread output, moving the read position to the end. Call under <see cref="Gate"/>.</summary>
        public string TakeUnread(int max, out long omitted)
        {
            var text = Buffer.Read(_readPosition, max, out omitted);
            _readPosition = Buffer.Written;
            _scanPosition = Math.Max(_scanPosition, _readPosition);
            return text;
        }

        public BackgroundShellInfo Info()
        {
            lock (Gate)
                return new(Id, Owner, name, command, workingDirectory, _status, _exitCode, StartedUtc, _ended, Buffer.Written);
        }

        private void Pulse()
        {
            var previous = _signal;
            _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
        }
    }
}
