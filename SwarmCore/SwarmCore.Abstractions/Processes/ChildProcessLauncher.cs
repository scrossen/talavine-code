using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SwarmCore.Abstractions.Processes;

/// <summary>
/// A started child process. Mirrors the parts of
/// <see cref="System.Diagnostics.Process"/> the codebase actually uses so call
/// sites can migrate mechanically. Obtain instances via
/// <see cref="ChildProcessLauncher.Start(ProcessStartInfo)"/>.
/// </summary>
public interface IChildProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }

    /// <summary>Exit code; throws while the process is still running.</summary>
    int ExitCode { get; }

    /// <summary>Writer for redirected stdin; throws when stdin was not redirected.</summary>
    StreamWriter StandardInput { get; }

    /// <summary>Reader for redirected stdout; throws when stdout was not redirected.</summary>
    StreamReader StandardOutput { get; }

    /// <summary>Reader for redirected stderr; throws when stderr was not redirected.</summary>
    StreamReader StandardError { get; }

    /// <summary>
    /// Raised once on a background thread when the child exits. Because a
    /// handle only exists after the child is already running, a handler
    /// attached after a fast exit is invoked immediately instead of being
    /// silently missed.
    /// </summary>
    event EventHandler? Exited;

    /// <summary>
    /// Line events for redirected stdout/stderr. A null line signals end of
    /// stream, matching <c>DataReceivedEventArgs.Data</c> semantics. Start the
    /// pumps with <see cref="BeginOutputReadLine"/>/<see cref="BeginErrorReadLine"/>.
    /// </summary>
    event Action<string?>? OutputLineReceived;
    event Action<string?>? ErrorLineReceived;

    void BeginOutputReadLine();
    void BeginErrorReadLine();

    void Kill(bool entireProcessTree = false);
    bool WaitForExit(TimeSpan timeout);
    Task WaitForExitAsync(CancellationToken ct = default);

    /// <summary>
    /// The wrapped <see cref="Process"/> when this child was started through
    /// <see cref="System.Diagnostics.Process"/>; null for posix_spawn children.
    /// Callers that need a <see cref="Process"/> for exact-identity inspection
    /// should use <see cref="AttachDiagnosticsProcess"/> instead.
    /// </summary>
    Process? DiagnosticsProcess { get; }

    /// <summary>
    /// Attaches a fresh <see cref="Process"/> to this child's PID for identity
    /// inspection or containment interop. Returns null once the child is gone.
    /// The caller owns and disposes the returned instance.
    /// </summary>
    Process? AttachDiagnosticsProcess();
}

/// <summary>
/// Starts child processes without ever calling <c>fork()</c> on Apple
/// platforms. .NET's <c>Process.Start</c> forks on macOS/Mac Catalyst, and
/// <c>fork()</c> runs libSystem's atfork preparers, which acquire every
/// Objective-C runtime lock. In a Catalyst UI process that deadlocks against
/// the main thread whenever it is inside an Obj-C weak-reference callout (for
/// example WKWebView editor-state updates through the Xamarin
/// retainWeakReference trampoline). posix_spawn does not run atfork preparers,
/// so spawning through it removes that whole deadlock class. On Windows and
/// other platforms this launcher is a straight passthrough to
/// <see cref="System.Diagnostics.Process"/> with identical behavior.
/// </summary>
public static class ChildProcessLauncher
{
    /// <summary>
    /// Starts a child described by <paramref name="startInfo"/>. Honors
    /// FileName, ArgumentList/Arguments, WorkingDirectory, Environment and the
    /// three standard-stream redirects (with their encodings). On Apple
    /// platforms with <c>UseShellExecute=true</c>, URLs and non-executable
    /// documents are handed to <c>/usr/bin/open</c>; anything else is spawned
    /// directly with PATH resolution.
    /// </summary>
    public static IChildProcess Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
        {
            var searchPath = startInfo.Environment.TryGetValue("PATH", out var configuredPath)
                ? configuredPath ?? string.Empty
                : string.Empty;
            var resolvedFileName = AppleSiliconExecutable.ResolveCompatiblePath(
                startInfo.FileName,
                startInfo.WorkingDirectory,
                searchPath);
            return PosixSpawnChildProcess.Start(startInfo, resolvedFileName);
        }
        return DiagnosticsChildProcess.Start(startInfo);
    }
}

/// <summary>
/// <see cref="IChildProcess"/> over a plain <see cref="Process"/>. Used on
/// every platform where fork() is not involved in process creation.
/// </summary>
internal sealed class DiagnosticsChildProcess : IChildProcess
{
    private readonly Process _process;
    private readonly ExitedEventRelay _exitedRelay = new();
    private int _outputPumpStarted;
    private int _errorPumpStarted;

    private DiagnosticsChildProcess(Process process)
    {
        _process = process;
        _process.Exited += (_, _) => _exitedRelay.Raise(this);
    }

    public static DiagnosticsChildProcess Start(ProcessStartInfo startInfo)
    {
        var process = new Process { StartInfo = startInfo };
        var wrapper = new DiagnosticsChildProcess(process);
        try
        {
            // EnableRaisingEvents must be set before Start for Exited to fire.
            process.EnableRaisingEvents = true;
        }
        catch (PlatformNotSupportedException)
        {
            // UseShellExecute launches on some platforms cannot raise events.
        }
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException(
                $"Process.Start returned false for {startInfo.FileName}.");
        }
        return wrapper;
    }

    public int Id => _process.Id;
    public bool HasExited => _process.HasExited;
    public int ExitCode => _process.ExitCode;
    public StreamWriter StandardInput => _process.StandardInput;
    public StreamReader StandardOutput => _process.StandardOutput;
    public StreamReader StandardError => _process.StandardError;
    public Process? DiagnosticsProcess => _process;

    public event EventHandler? Exited
    {
        add => _exitedRelay.Add(this, value);
        remove => _exitedRelay.Remove(value);
    }

    public event Action<string?>? OutputLineReceived;
    public event Action<string?>? ErrorLineReceived;

    public void BeginOutputReadLine()
    {
        if (Interlocked.Exchange(ref _outputPumpStarted, 1) != 0) return;
        _process.OutputDataReceived += (_, e) => OutputLineReceived?.Invoke(e.Data);
        _process.BeginOutputReadLine();
    }

    public void BeginErrorReadLine()
    {
        if (Interlocked.Exchange(ref _errorPumpStarted, 1) != 0) return;
        _process.ErrorDataReceived += (_, e) => ErrorLineReceived?.Invoke(e.Data);
        _process.BeginErrorReadLine();
    }

    public void Kill(bool entireProcessTree = false) => _process.Kill(entireProcessTree);

    public bool WaitForExit(TimeSpan timeout)
        => _process.WaitForExit((int)Math.Min(int.MaxValue, timeout.TotalMilliseconds));

    public Task WaitForExitAsync(CancellationToken ct = default)
        => _process.WaitForExitAsync(ct);

    public Process? AttachDiagnosticsProcess()
    {
        try
        {
            return Process.GetProcessById(_process.Id);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose() => _process.Dispose();
}

/// <summary>
/// Exited-event plumbing shared by both <see cref="IChildProcess"/>
/// implementations. A handle only exists after its child is already running,
/// so a subscriber that arrives after a fast exit is invoked immediately
/// (asynchronously) instead of waiting for an event that already fired.
/// </summary>
internal sealed class ExitedEventRelay
{
    private readonly object _gate = new();
    private EventHandler? _handlers;
    private bool _raised;

    public void Add(object sender, EventHandler? handler)
    {
        if (handler is null) return;
        lock (_gate)
        {
            if (!_raised)
            {
                _handlers += handler;
                return;
            }
        }
        _ = Task.Run(() => handler(sender, EventArgs.Empty));
    }

    public void Remove(EventHandler? handler)
    {
        if (handler is null) return;
        lock (_gate)
        {
            _handlers -= handler;
        }
    }

    public void Raise(object sender)
    {
        EventHandler? toInvoke;
        lock (_gate)
        {
            if (_raised) return;
            _raised = true;
            toInvoke = _handlers;
            _handlers = null;
        }
        toInvoke?.Invoke(sender, EventArgs.Empty);
    }
}

/// <summary>
/// <see cref="IChildProcess"/> implemented directly over posix_spawn(p) for
/// Apple platforms. Redirected standard streams are plain pipes; the child is
/// spawned with CLOEXEC_DEFAULT so it inherits exactly the descriptors dup2'd
/// onto fds 0-2 and nothing else; signal dispositions are reset to default
/// like .NET's own fork/exec path does. A dedicated background thread waits
/// for the exit status, so no fork ever happens in this process.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
[System.Runtime.Versioning.SupportedOSPlatform("maccatalyst")]
internal sealed class PosixSpawnChildProcess : IChildProcess
{
    private readonly int _pid;
    private readonly ExitedEventRelay _exitedRelay = new();
    private readonly TaskCompletionSource<int> _exit =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StreamWriter? _standardInput;
    private readonly StreamReader? _standardOutput;
    private readonly StreamReader? _standardError;
    private readonly TaskCompletionSource _outputPumpDone =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _errorPumpDone =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _outputPumpStarted;
    private int _errorPumpStarted;

    private PosixSpawnChildProcess(
        int pid,
        StreamWriter? standardInput,
        StreamReader? standardOutput,
        StreamReader? standardError)
    {
        _pid = pid;
        _standardInput = standardInput;
        _standardOutput = standardOutput;
        _standardError = standardError;

        var waiter = new Thread(WaitForChildExit)
        {
            IsBackground = true,
            Name = $"posix_spawn-wait-{pid}",
        };
        waiter.Start();
    }

    public static PosixSpawnChildProcess Start(
        ProcessStartInfo startInfo,
        string resolvedFileName)
    {
        var redirectAny = startInfo.RedirectStandardInput
                          || startInfo.RedirectStandardOutput
                          || startInfo.RedirectStandardError;
        if (startInfo.UseShellExecute && redirectAny)
        {
            throw new InvalidOperationException(
                "UseShellExecute cannot be combined with standard-stream redirection.");
        }

        var (fileName, argv) = ResolveInvocation(startInfo, resolvedFileName);

        int[]? stdinPipe = null;
        int[]? stdoutPipe = null;
        int[]? stderrPipe = null;
        var fileActions = IntPtr.Zero;
        var attributes = IntPtr.Zero;
        try
        {
            if (startInfo.RedirectStandardInput) stdinPipe = CreatePipe();
            if (startInfo.RedirectStandardOutput) stdoutPipe = CreatePipe();
            if (startInfo.RedirectStandardError) stderrPipe = CreatePipe();

            ThrowIfPosixError(
                posix_spawn_file_actions_init(ref fileActions),
                "posix_spawn_file_actions_init");
            ThrowIfPosixError(
                posix_spawnattr_init(ref attributes),
                "posix_spawnattr_init");

            // Reset every signal disposition in the child (like .NET's own
            // fork/exec does), start from an empty signal mask, and close all
            // descriptors the file actions below don't explicitly map.
            uint allSignals = ~0u;
            uint noSignals = 0u;
            ThrowIfPosixError(
                posix_spawnattr_setsigdefault(ref attributes, ref allSignals),
                "posix_spawnattr_setsigdefault");
            ThrowIfPosixError(
                posix_spawnattr_setsigmask(ref attributes, ref noSignals),
                "posix_spawnattr_setsigmask");
            ThrowIfPosixError(
                posix_spawnattr_setflags(
                    ref attributes,
                    PosixSpawnSetSigDef | PosixSpawnSetSigMask | PosixSpawnCloexecDefault),
                "posix_spawnattr_setflags");

            AddStdioAction(ref fileActions, stdinPipe?[0], 0);
            AddStdioAction(ref fileActions, stdoutPipe?[1], 1);
            AddStdioAction(ref fileActions, stderrPipe?[1], 2);

            if (!string.IsNullOrWhiteSpace(startInfo.WorkingDirectory))
            {
                ThrowIfPosixError(
                    posix_spawn_file_actions_addchdir_np(ref fileActions, startInfo.WorkingDirectory),
                    "posix_spawn_file_actions_addchdir_np");
            }

            using var nativeArgv = new NativeStringArray(argv);
            using var nativeEnvironment = new NativeStringArray(BuildEnvironment(startInfo));

            var result = posix_spawnp(
                out var pid,
                fileName,
                ref fileActions,
                ref attributes,
                nativeArgv.ArrayPointer,
                nativeEnvironment.ArrayPointer);
            if (result != 0)
            {
                throw new InvalidOperationException(
                    $"posix_spawnp failed for {fileName} with error {result}: {DescribePosixError(result)}");
            }

            // The child owns its dup2'd ends now; only the parent ends stay open.
            CloseQuietly(stdinPipe?[0]);
            CloseQuietly(stdoutPipe?[1]);
            CloseQuietly(stderrPipe?[1]);

            return new PosixSpawnChildProcess(
                pid,
                stdinPipe is null
                    ? null
                    : new StreamWriter(
                        ParentStream(stdinPipe[1], writable: true),
                        WithoutPreamble(startInfo.StandardInputEncoding))
                    { AutoFlush = true },
                stdoutPipe is null
                    ? null
                    : new StreamReader(
                        ParentStream(stdoutPipe[0], writable: false),
                        startInfo.StandardOutputEncoding ?? Encoding.UTF8),
                stderrPipe is null
                    ? null
                    : new StreamReader(
                        ParentStream(stderrPipe[0], writable: false),
                        startInfo.StandardErrorEncoding ?? Encoding.UTF8));
        }
        catch
        {
            ClosePipeQuietly(stdinPipe);
            ClosePipeQuietly(stdoutPipe);
            ClosePipeQuietly(stderrPipe);
            throw;
        }
        finally
        {
            if (fileActions != IntPtr.Zero) posix_spawn_file_actions_destroy(ref fileActions);
            if (attributes != IntPtr.Zero) posix_spawnattr_destroy(ref attributes);
        }
    }

    public int Id => _pid;
    public bool HasExited => _exit.Task.IsCompleted;

    public int ExitCode => _exit.Task.IsCompleted
        ? _exit.Task.Result
        : throw new InvalidOperationException("The child process has not exited yet.");

    public StreamWriter StandardInput => _standardInput
        ?? throw new InvalidOperationException("Standard input was not redirected.");

    public StreamReader StandardOutput => _standardOutput
        ?? throw new InvalidOperationException("Standard output was not redirected.");

    public StreamReader StandardError => _standardError
        ?? throw new InvalidOperationException("Standard error was not redirected.");

    public Process? DiagnosticsProcess => null;

    public event EventHandler? Exited
    {
        add => _exitedRelay.Add(this, value);
        remove => _exitedRelay.Remove(value);
    }

    public event Action<string?>? OutputLineReceived;
    public event Action<string?>? ErrorLineReceived;

    public void BeginOutputReadLine()
    {
        if (Interlocked.Exchange(ref _outputPumpStarted, 1) != 0) return;
        PumpLines(StandardOutput, line => OutputLineReceived?.Invoke(line), _outputPumpDone);
    }

    public void BeginErrorReadLine()
    {
        if (Interlocked.Exchange(ref _errorPumpStarted, 1) != 0) return;
        PumpLines(StandardError, line => ErrorLineReceived?.Invoke(line), _errorPumpDone);
    }

    public void Kill(bool entireProcessTree = false)
    {
        if (HasExited) return;
        if (entireProcessTree)
        {
            // Delegate descendant enumeration to the runtime; sending the
            // signals themselves does not require a parent/child relation.
            using var attached = AttachDiagnosticsProcess();
            if (attached is not null)
            {
                try
                {
                    attached.Kill(entireProcessTree: true);
                    return;
                }
                catch (InvalidOperationException)
                {
                    return;
                }
                catch
                {
                    // Fall through to a direct SIGKILL of the root.
                }
            }
        }
        _ = kill(_pid, Sigkill);
    }

    public bool WaitForExit(TimeSpan timeout)
        => _exit.Task.Wait((int)Math.Min(int.MaxValue, timeout.TotalMilliseconds));

    public async Task WaitForExitAsync(CancellationToken ct = default)
    {
        await _exit.Task.WaitAsync(ct).ConfigureAwait(false);
        // Parity with Process.WaitForExitAsync: after the child is reaped,
        // wait for started line pumps to hit end-of-stream so callers observe
        // all buffered output. waitpid can win the race against the pump task
        // for short-lived children.
        if (Volatile.Read(ref _outputPumpStarted) != 0)
        {
            await _outputPumpDone.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        if (Volatile.Read(ref _errorPumpStarted) != 0)
        {
            await _errorPumpDone.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    public Process? AttachDiagnosticsProcess()
    {
        if (HasExited) return null;
        try
        {
            return Process.GetProcessById(_pid);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        // Parity with Process.Dispose: release stream resources, never kill.
        // The background waiter keeps reaping so no zombie is left behind.
        try { _standardInput?.Dispose(); } catch (IOException) { }
        _standardOutput?.Dispose();
        _standardError?.Dispose();
    }

    private void WaitForChildExit()
    {
        while (true)
        {
            var waited = waitpid(_pid, out var status, 0);
            if (waited == _pid)
            {
                _exit.TrySetResult(TranslateExitStatus(status));
                break;
            }
            if (waited == -1 && Marshal.GetLastPInvokeError() != Eintr)
            {
                // ECHILD or similar: the status is unobtainable. Surface the
                // conventional signal-death code rather than hanging waiters.
                _exit.TrySetResult(-1);
                break;
            }
        }
        _exitedRelay.Raise(this);
    }

    private static int TranslateExitStatus(int status)
    {
        var terminationSignal = status & 0x7f;
        if (terminationSignal == 0)
        {
            return (status >> 8) & 0xff;
        }
        // Match .NET's Unix convention for signal deaths.
        return 128 + terminationSignal;
    }

    private static void PumpLines(
        StreamReader reader, Action<string?> deliver, TaskCompletionSource done)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    deliver(line);
                }
            }
            catch (ObjectDisposedException) { }
            catch (IOException) { }
            deliver(null);
            done.TrySetResult();
        });
    }

    private static (string FileName, IReadOnlyList<string> Argv) ResolveInvocation(
        ProcessStartInfo startInfo,
        string resolvedFileName)
    {
        var fileName = startInfo.FileName;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException("ProcessStartInfo.FileName is required.");
        }

        var arguments = startInfo.ArgumentList.Count > 0
            ? startInfo.ArgumentList.ToList()
            : SplitArguments(startInfo.Arguments);

        if (startInfo.UseShellExecute && ShouldOpenWithLaunchServices(fileName))
        {
            var openArgv = new List<string> { "open", fileName };
            openArgv.AddRange(arguments);
            return ("/usr/bin/open", openArgv);
        }

        fileName = resolvedFileName;
        var argv = new List<string> { fileName };
        argv.AddRange(arguments);
        return (fileName, argv);
    }

    private static bool ShouldOpenWithLaunchServices(string fileName)
    {
        if (Uri.TryCreate(fileName, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
        {
            return true;
        }
        if (!File.Exists(fileName) && !Directory.Exists(fileName))
        {
            return false;
        }
        try
        {
            return Directory.Exists(fileName)
                   || (File.GetUnixFileMode(fileName) & UnixFileMode.UserExecute) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Splits a legacy <see cref="ProcessStartInfo.Arguments"/> string the way
    /// .NET does on Unix: whitespace-separated, double quotes group, backslash
    /// escapes a following quote or backslash inside quotes.
    /// </summary>
    internal static List<string> SplitArguments(string? arguments)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(arguments)) return result;

        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < arguments.Length; i++)
        {
            var character = arguments[i];
            if (character == '\\' && i + 1 < arguments.Length
                && (arguments[i + 1] == '"' || arguments[i + 1] == '\\'))
            {
                current.Append(arguments[i + 1]);
                i++;
                continue;
            }
            if (character == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (!inQuotes && char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(character);
        }
        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }
        return result;
    }

    private static IEnumerable<string> BuildEnvironment(ProcessStartInfo startInfo)
    {
        // ProcessStartInfo.Environment starts as a snapshot of the current
        // environment, so serializing it honors both inherited and added keys.
        foreach (var pair in startInfo.Environment)
        {
            if (pair.Value is not null)
            {
                yield return $"{pair.Key}={pair.Value}";
            }
        }
    }

    private static void AddStdioAction(ref IntPtr fileActions, int? childPipeEnd, int targetFd)
    {
        // CLOEXEC_DEFAULT closes everything that is not explicitly mapped, so
        // non-redirected standard streams must be inherited by name.
        var result = childPipeEnd is { } sourceFd
            ? posix_spawn_file_actions_adddup2(ref fileActions, sourceFd, targetFd)
            : posix_spawn_file_actions_addinherit_np(ref fileActions, targetFd);
        ThrowIfPosixError(result, "posix_spawn_file_actions stdio setup");
    }

    private static int[] CreatePipe()
    {
        var descriptors = new int[2];
        if (pipe(descriptors) != 0)
        {
            throw new InvalidOperationException(
                $"pipe() failed: {DescribePosixError(Marshal.GetLastPInvokeError())}");
        }
        // Keep parent ends out of any concurrently-started child.
        _ = fcntl(descriptors[0], FSetFd, FdCloexec);
        _ = fcntl(descriptors[1], FSetFd, FdCloexec);
        return descriptors;
    }

    // Pipe streams, not FileStream: on Unix, PipeStream async I/O is
    // cancellation-interruptible (a canceled token aborts a write blocked on a
    // full pipe), matching Process.StandardInput/Output/Error semantics. A
    // FileStream over the same descriptor blocks uncancellably.
    private static Stream ParentStream(int descriptor, bool writable)
        => new System.IO.Pipes.AnonymousPipeClientStream(
            writable ? System.IO.Pipes.PipeDirection.Out : System.IO.Pipes.PipeDirection.In,
            new Microsoft.Win32.SafeHandles.SafePipeHandle(new IntPtr(descriptor), ownsHandle: true));

    private static Encoding WithoutPreamble(Encoding? encoding)
    {
        // A BOM written into a CLI's stdin corrupts the first message.
        if (encoding is null || encoding.CodePage == Encoding.UTF8.CodePage)
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        return encoding;
    }

    private static void ClosePipeQuietly(int[]? descriptors)
    {
        if (descriptors is null) return;
        CloseQuietly(descriptors[0]);
        CloseQuietly(descriptors[1]);
    }

    private static void CloseQuietly(int? descriptor)
    {
        if (descriptor is { } fd)
        {
            _ = close(fd);
        }
    }

    private static void ThrowIfPosixError(int result, string operation)
    {
        if (result != 0)
        {
            throw new InvalidOperationException(
                $"{operation} failed with error {result}: {DescribePosixError(result)}");
        }
    }

    private static string DescribePosixError(int error)
    {
        var message = strerror(error);
        return message == IntPtr.Zero
            ? "unknown error"
            : Marshal.PtrToStringUTF8(message) ?? "unknown error";
    }

    private sealed class NativeStringArray : IDisposable
    {
        private readonly List<IntPtr> _allocations = [];

        public NativeStringArray(IEnumerable<string> values)
        {
            var valueList = values.ToList();
            ArrayPointer = Marshal.AllocHGlobal(IntPtr.Size * (valueList.Count + 1));
            _allocations.Add(ArrayPointer);
            for (var i = 0; i < valueList.Count; i++)
            {
                var pointer = AllocUtf8(valueList[i]);
                _allocations.Add(pointer);
                Marshal.WriteIntPtr(ArrayPointer, i * IntPtr.Size, pointer);
            }
            Marshal.WriteIntPtr(ArrayPointer, valueList.Count * IntPtr.Size, IntPtr.Zero);
        }

        public IntPtr ArrayPointer { get; }

        public void Dispose()
        {
            foreach (var allocation in _allocations)
            {
                Marshal.FreeHGlobal(allocation);
            }
        }

        private static IntPtr AllocUtf8(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var pointer = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            Marshal.WriteByte(pointer, bytes.Length, 0);
            return pointer;
        }
    }

    private const short PosixSpawnSetSigDef = 0x0004;
    private const short PosixSpawnSetSigMask = 0x0008;
    private const short PosixSpawnCloexecDefault = 0x4000;
    private const int Sigkill = 9;
    private const int Eintr = 4;
    private const int FSetFd = 2;
    private const int FdCloexec = 1;

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_spawnp(
        out int pid,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string file,
        ref IntPtr fileActions,
        ref IntPtr attrp,
        IntPtr argv,
        IntPtr envp);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_init(ref IntPtr fileActions);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_destroy(ref IntPtr fileActions);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_adddup2(
        ref IntPtr fileActions,
        int fd,
        int newFd);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_addinherit_np(
        ref IntPtr fileActions,
        int fd);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_addchdir_np(
        ref IntPtr fileActions,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport("libc")]
    private static extern int posix_spawnattr_init(ref IntPtr attr);

    [DllImport("libc")]
    private static extern int posix_spawnattr_destroy(ref IntPtr attr);

    [DllImport("libc")]
    private static extern int posix_spawnattr_setflags(ref IntPtr attr, short flags);

    [DllImport("libc")]
    private static extern int posix_spawnattr_setsigdefault(ref IntPtr attr, ref uint sigset);

    [DllImport("libc")]
    private static extern int posix_spawnattr_setsigmask(ref IntPtr attr, ref uint sigset);

    [DllImport("libc", SetLastError = true)]
    private static extern int pipe(int[] descriptors);

    [DllImport("libc")]
    private static extern int close(int fd);

    [DllImport("libc")]
    private static extern int fcntl(int fd, int command, int argument);

    [DllImport("libc")]
    private static extern int kill(int pid, int signal);

    [DllImport("libc", SetLastError = true)]
    private static extern int waitpid(int pid, out int status, int options);

    [DllImport("libc")]
    private static extern IntPtr strerror(int errnum);
}
