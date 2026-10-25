using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.Diagnostics;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Abstractions.Processes;
using SwarmCore.LlmRouting.Configuration;
using SwarmCore.LlmRouting.ProviderPacks;
using SwarmCore.LlmRouting.Services;

namespace SwarmCore.LlmRouting.Providers;

public enum SubscriptionCliProcessOperation
{
    Inference,
    LoginStatus,
    ModelDiscovery,
    ExecutableReadiness
}

public enum SubscriptionCliProcessPriority
{
    Background,
    Foreground
}

public static class SubscriptionCliExecutionSettings
{
    public const string ProcessPriority = "subscription_cli_process_priority";

    public static SubscriptionCliProcessPriority FromInteractivity(
        LlmInteractivity interactivity) =>
        interactivity == LlmInteractivity.UserWaiting
            ? SubscriptionCliProcessPriority.Foreground
            : SubscriptionCliProcessPriority.Background;

    public static SubscriptionCliProcessPriority GetProcessPriority(
        IReadOnlyDictionary<string, object>? settings)
    {
        if (settings is null
            || !settings.TryGetValue(ProcessPriority, out var value)
            || value is null)
        {
            return SubscriptionCliProcessPriority.Foreground;
        }

        if (value is SubscriptionCliProcessPriority priority
            && Enum.IsDefined(priority))
        {
            return priority;
        }

        return value is string text
               && Enum.TryParse<SubscriptionCliProcessPriority>(
                   text,
                   ignoreCase: true,
                   out var parsed)
               && Enum.IsDefined(parsed)
            ? parsed
            : SubscriptionCliProcessPriority.Foreground;
    }
}

public sealed record SubscriptionCliProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? StandardInput,
    string WorkingDirectory,
    TimeSpan Timeout,
    string? OutputPath = null,
    string? ProviderName = null,
    SubscriptionCliProcessOperation Operation = SubscriptionCliProcessOperation.Inference,
    // Defaults to Background so a future call site that forgets to classify its
    // work cannot silently join the privileged lane and starve discovery. Every
    // real foreground path sets this explicitly (inference rewrites it from the
    // resolved interactivity; login-status passes its own).
    SubscriptionCliProcessPriority Priority = SubscriptionCliProcessPriority.Background,
    SubscriptionCliCatalogProtocol? CatalogProtocol = null)
{
    /// <summary>Exact route before normalization; host launch admission can bind the effort tier too.</summary>
    public string? RequestedModel { get; init; }
    /// <summary>Opt-in host launch snapshot. Null retains ordinary inherited-environment behavior.</summary>
    public IReadOnlyDictionary<string, string?>? ChildEnvironmentSnapshot { get; init; }
    /// <summary>Runtime-only final physical launch validation; never a model argument or persisted credential.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Action<ProcessStartInfo>? ValidateFinalLaunch { get; init; }
}

public sealed record SubscriptionCliProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    /// <summary>
    /// True when the runner drained stdout to completion but retained only the
    /// configured bounded prefix. Consumers must treat the response as
    /// incomplete instead of parsing or returning the retained text.
    /// </summary>
    public bool StandardOutputTruncated { get; init; }

    /// <summary>
    /// True when the runner drained stderr to completion but retained only the
    /// configured bounded prefix. Consumers must not infer auth or response
    /// success from a partial diagnostic stream.
    /// </summary>
    public bool StandardErrorTruncated { get; init; }

    public bool OutputWasTruncated => StandardOutputTruncated || StandardErrorTruncated;
}

internal sealed class SubscriptionCliSafeStopException : InvalidOperationException
{
    public SubscriptionCliSafeStopException()
        : base(
            "A subscription CLI left by an earlier process could not be stopped safely. " +
            "Retry after the previous process exits.")
    {
    }
}

internal sealed class SubscriptionCliOutputLimitExceededException : InvalidOperationException
{
    public SubscriptionCliOutputLimitExceededException()
        : base("Subscription CLI response output exceeded the safe capture limit.")
    {
    }
}

internal readonly record struct SubscriptionCliBoundedTextCapture(
    string Text,
    bool WasTruncated);

public interface ISubscriptionCliProcessRunner
{
    Task<SubscriptionCliProcessResult> RunAsync(SubscriptionCliProcessRequest request, CancellationToken ct = default);
}

public static class SubscriptionCliEnvironment
{
    public static IReadOnlyList<string> ApiCredentialEnvironmentVariables { get; } =
    [
        // Anthropic / Claude direct API paths. Claude Code should use its logged-in
        // subscription session for this provider, never a Talavine/API-account key.
        "ANTHROPIC_API_KEY",
        "CLAUDE_API_KEY",
        "ANTHROPIC_AUTH_TOKEN",
        "ANTHROPIC_BASE_URL",
        "ANTHROPIC_API_URL",
        "ANTHROPIC_CUSTOM_HEADERS",
        "CLAUDE_CODE_USE_BEDROCK",
        "CLAUDE_CODE_USE_VERTEX",
        "CLAUDE_CODE_USE_FOUNDRY",
        "ANTHROPIC_VERTEX_PROJECT_ID",
        "ANTHROPIC_VERTEX_REGION",
        "ANTHROPIC_BEDROCK_BASE_URL",
        "ANTHROPIC_FOUNDRY_API_KEY",
        "ANTHROPIC_FOUNDRY_RESOURCE",
        "ANTHROPIC_FOUNDRY_BASE_URL",

        // Microsoft Foundry can fall back to the Azure SDK credential chain.
        // Remove inherited non-subscription identities as well as explicit keys.
        "AZURE_CLIENT_ID",
        "AZURE_TENANT_ID",
        "AZURE_CLIENT_SECRET",
        "AZURE_CLIENT_CERTIFICATE_PATH",
        "AZURE_CLIENT_CERTIFICATE_PASSWORD",
        "AZURE_USERNAME",
        "AZURE_PASSWORD",
        "AZURE_AUTHORITY_HOST",
        "AZURE_FEDERATED_TOKEN_FILE",

        // OpenAI / Codex direct API paths. Codex subscription routing should not
        // inherit OpenAI project keys from the parent app process.
        "OPENAI_API_KEY",
        "CODEX_API_KEY",
        "CODEX_ACCESS_TOKEN",
        "OPENAI_BASE_URL",
        "OPENAI_API_BASE",
        "OPENAI_API_HOST",
        "OPENAI_API_TYPE",
        "OPENAI_ORG_ID",
        "OPENAI_ORGANIZATION",
        "OPENAI_PROJECT",
        "AZURE_OPENAI_API_KEY",
        "AZURE_OPENAI_ENDPOINT",
        "AZURE_OPENAI_DEPLOYMENT",
        "AZURE_OPENAI_API_VERSION",

        // Other common model-provider API keys should not leak into a subscription
        // CLI child process either. These CLIs are explicitly non-LocalOnly, but
        // their spend source should be the logged-in subscription account.
        "GEMINI_API_KEY",
        "GOOGLE_API_KEY",
        "GOOGLE_GENERATIVE_AI_API_KEY",
        "DEEPSEEK_API_KEY",
        "XAI_API_KEY",
        "GROK_API_KEY"
    ];

    public static void ScrubApiCredentialEnvironment(IDictionary<string, string?> environment)
    {
        foreach (var key in ApiCredentialEnvironmentVariables)
        {
            environment.Remove(key);
        }
    }
}

public sealed class DefaultSubscriptionCliProcessRunner : ISubscriptionCliProcessRunner
{
    // These limits bound retained managed memory, not pipe drainage. The runner
    // continues consuming both streams after a limit is reached so a noisy CLI
    // cannot deadlock on a full OS pipe. Truncation is surfaced explicitly and
    // every consumer fails closed rather than treating the retained prefix as a
    // complete provider response or authentication verdict.
    internal const int DefaultStandardOutputCaptureLimitChars = 4 * 1024 * 1024;
    internal const int DefaultStandardErrorCaptureLimitChars = 512 * 1024;

    private readonly ICrashBreadcrumbSink _crashBreadcrumbs;
    private readonly ChildProcessExecutionGate _executionGate;
    private readonly Func<bool> _requiresSerialization;

    /// <summary>
    /// Containment keys are provider-scoped so a live child of one CLI never
    /// declines a launch of a different CLI. Internal seam: tests redirect to
    /// run-unique keys so real-marker scenarios stay hermetic.
    /// </summary>
    internal Func<string?, string> ContainmentWorkloadKeySelector { get; set; } =
        SubscriptionCliProcessExecutionPolicy.GetLifetimeWorkloadKey;

    /// <summary>
    /// The pre-provider-scoping shared key, swept (never written) while old
    /// markers remain under it. Null disables the transition sweep in tests.
    /// </summary>
    internal string? LegacyContainmentWorkloadKey { get; set; } =
        SubscriptionCliProcessExecutionPolicy.LegacyLifetimeWorkloadKey;

    /// <summary>
    /// Cross-process foreground-yield channel: raised when a user-waiting
    /// launch is declined by another process's live child, honored by
    /// unserialized hosts (WorkerHost bulk lanes) before they start their next
    /// child for the same provider.
    /// </summary>
    internal ForegroundWorkPendingSignal ForegroundPendingSignal { get; set; } =
        ForegroundWorkPendingSignal.Default;

    /// <summary>
    /// Upper bound on how long an unserialized background launch defers to a
    /// fresh foreground-pending sentinel. Deliberately finite: bulk work slows
    /// down for the user's turn but can never be deadlocked by a wedged or
    /// crashed desktop app.
    /// </summary>
    internal TimeSpan ForegroundYieldMaximumWait { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Sentinel poll cadence during a foreground-yield wait.</summary>
    internal TimeSpan ForegroundYieldPollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Internal seam for small, deterministic overflow tests.</summary>
    internal int StandardOutputCaptureLimitChars { get; set; } =
        DefaultStandardOutputCaptureLimitChars;

    /// <summary>Internal seam for small, deterministic overflow tests.</summary>
    internal int StandardErrorCaptureLimitChars { get; set; } =
        DefaultStandardErrorCaptureLimitChars;

    public DefaultSubscriptionCliProcessRunner(
        ICrashBreadcrumbSink? crashBreadcrumbs = null,
        IOptions<SubscriptionCliProviderOptions>? options = null)
        : this(
            crashBreadcrumbs,
            SubscriptionCliProcessExecutionPolicy.SharedGate,
            SubscriptionCliProcessExecutionPolicy.RequiresSerialization)
    {
        _options = options;
    }

    private readonly IOptions<SubscriptionCliProviderOptions>? _options;

    internal DefaultSubscriptionCliProcessRunner(
        ICrashBreadcrumbSink? crashBreadcrumbs,
        ChildProcessExecutionGate executionGate,
        Func<bool> requiresSerialization)
    {
        _crashBreadcrumbs = crashBreadcrumbs ?? NullCrashBreadcrumbSink.Instance;
        _executionGate = executionGate;
        _requiresSerialization = requiresSerialization;
    }

    public async Task<SubscriptionCliProcessResult> RunAsync(
        SubscriptionCliProcessRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var serialized = _requiresSerialization();
        ChildProcessExecutionLease? executionLease = null;
        if (serialized)
        {
            WriteBreadcrumb(request, "gate-wait");
            try
            {
                executionLease = await _executionGate.AcquireAsync(
                        foreground: request.Priority == SubscriptionCliProcessPriority.Foreground,
                        ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                WriteBreadcrumb(
                    request,
                    "gate-wait-cancelled",
                    outcome: "cancelled",
                    failureType: ex.GetType().Name);
                throw;
            }

            WriteBreadcrumb(request, "gate-acquired");
        }

        try
        {
            return await RunCoreAsync(
                    request,
                    serialized,
                    IsPreemptible(request) ? executionLease?.Preempted ?? default : default,
                    ct)
                .ConfigureAwait(false);
        }
        catch (SubscriptionCliPreemptedException ex)
        {
            WriteBreadcrumb(
                request,
                "process-preempted",
                outcome: "preempted",
                failureType: ex.GetType().Name);
            throw;
        }
        catch (TimeoutException ex)
        {
            WriteBreadcrumb(
                request,
                "process-failure",
                outcome: "timeout",
                failureType: ex.GetType().Name);
            throw;
        }
        catch (OperationCanceledException ex)
        {
            WriteBreadcrumb(
                request,
                "process-failure",
                outcome: "cancelled",
                failureType: ex.GetType().Name);
            throw;
        }
        catch (Exception ex)
        {
            WriteBreadcrumb(
                request,
                "process-failure",
                outcome: "failed",
                failureType: ex.GetType().Name);
            throw;
        }
        finally
        {
            executionLease?.Dispose();
            if (serialized)
            {
                WriteBreadcrumb(request, "gate-released");
            }
        }
    }

    /// <summary>
    /// Only work that is cheap to abandon and repeat may be stopped to free the
    /// slot. Discovery and login probes are idempotent and re-run on the next
    /// sweep, so killing one costs nothing. A background INFERENCE call is
    /// different: it has already spent subscription quota and may be seconds
    /// from a result that gets persisted, so it keeps the slot and is bounded
    /// by its own timeout instead.
    /// </summary>
    private static bool IsPreemptible(SubscriptionCliProcessRequest request)
        => request.Operation is SubscriptionCliProcessOperation.ModelDiscovery
                             or SubscriptionCliProcessOperation.LoginStatus
                             or SubscriptionCliProcessOperation.ExecutableReadiness;

    private async Task<SubscriptionCliProcessResult> RunCoreAsync(
        SubscriptionCliProcessRequest request,
        bool serialized,
        CancellationToken preempted,
        CancellationToken ct)
    {
        var workloadKey = ContainmentWorkloadKeySelector(request.ProviderName);

        // Foreground yield: an unserialized host (WorkerHost bulk lanes,
        // headless helpers) defers a new background child for this provider
        // while the desktop app keeps a fresh foreground-pending sentinel
        // raised — its user-waiting turn is being declined by a live child of
        // this provider, most likely ours. The serialized Catalyst app itself
        // never waits here: its foreground/background ordering belongs to the
        // in-process gate, and sleeping while HOLDING that gate's only slot
        // would starve the very foreground work the sentinel describes.
        if (!serialized
            && request.Priority == SubscriptionCliProcessPriority.Background
            && ForegroundPendingSignal.IsPending(workloadKey))
        {
            WriteBreadcrumb(request, "foreground-yield-wait");
            var cleared = await ForegroundPendingSignal.WaitWhilePendingAsync(
                    workloadKey,
                    ForegroundYieldMaximumWait,
                    ct,
                    ForegroundYieldPollInterval)
                .ConfigureAwait(false);
            WriteBreadcrumb(
                request,
                cleared ? "foreground-yield-cleared" : "foreground-yield-timeout");
        }

        var orphanSweep = await SubscriptionCliProcessExecutionPolicy
            .ReapOrphansWithLegacyTransitionAsync(
                workloadKey,
                LegacyContainmentWorkloadKey,
                logger: null,
                ct)
            .ConfigureAwait(false);
        if (!orphanSweep.Succeeded
            || (serialized && orphanSweep.LiveOwnerMarkers > 0))
        {
            if (request.Priority == SubscriptionCliProcessPriority.Foreground)
            {
                // The user is actively waiting and another process's live
                // child (or an unverifiable sweep) is blocking this provider.
                // Raise/refresh the cross-process yield sentinel so worker
                // hosts pause this provider's lane between children; each
                // foreground retry lands here again and refreshes it.
                ForegroundPendingSignal.Raise(workloadKey);
            }

            throw new SubscriptionCliSafeStopException();
        }

        var redirectStandardInput = request.StandardInput is not null || request.CatalogProtocol is not null;
        if (request.CatalogProtocol is not null && request.Operation != SubscriptionCliProcessOperation.ModelDiscovery)
            throw new InvalidOperationException("A catalog exchange requires a model-discovery operation.");
        var startInfo = SubscriptionCliProcessStartInfo.Create(
            request.FileName,
            request.Arguments,
            request.ChildEnvironmentSnapshot,
            _options?.Value.PrivateCliDataRoot);
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.RedirectStandardInput = redirectStandardInput;
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;
        startInfo.CreateNoWindow = true;
        startInfo.WorkingDirectory = request.WorkingDirectory;

        // StandardInputEncoding may only be assigned when stdin is actually
        // redirected; setting it otherwise throws on Process.Start. Connectivity
        // probes (e.g. `--version`) pass no stdin, so guard the assignment — this
        // is what makes provider TestConnectivityAsync work from hosts that don't
        // have their own detection path (the Vibes Settings page).
        if (redirectStandardInput)
        {
            // JSONL control protocols require the first byte to be '{', without a UTF-8 BOM.
            startInfo.StandardInputEncoding = new UTF8Encoding(false);
        }

        SubscriptionCliEnvironment.ScrubApiCredentialEnvironment(startInfo.Environment);

        // Recheck the actual final executable, cwd, environment and evidence after
        // all startup adapters and serialization waits, immediately before launch.
        request.ValidateFinalLaunch?.Invoke(startInfo);

        WriteBreadcrumb(request, "process-starting");
        using var process = ChildProcessLauncher.Start(startInfo);
        using var diagnosticsProcess = process.AttachDiagnosticsProcess();
        using var containment = diagnosticsProcess is null
            ? null
            : ChildProcessLifetimeContainment.Attach(
                diagnosticsProcess,
                workloadKey);
        WriteBreadcrumb(
            request,
            "process-started",
            childProcessId: process.Id);
        if (request.Priority == SubscriptionCliProcessPriority.Foreground)
        {
            // The user-waiting child is actually running; stop asking worker
            // hosts to defer instead of letting the sentinel age out its TTL.
            ForegroundPendingSignal.Clear(workloadKey);
        }

        using var timeoutCts = preempted.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(ct, preempted)
            : CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(request.Timeout);
        var ioToken = timeoutCts.Token;

        // Drain output from the instant the process starts. A CLI can emit enough
        // stdout/stderr to fill its pipes while it is still consuming a large
        // prompt; starting these reads after stdin would create a classic pipe
        // deadlock. The same linked token governs every blocking I/O operation.
        var stdoutTask = request.CatalogProtocol is { } protocol
            ? SubscriptionCliModelDiscovery.ExchangeAsync(process.StandardOutput, process.StandardInput, protocol, StandardOutputCaptureLimitChars, ioToken)
            : ReadBoundedToEndAsync(
            process.StandardOutput,
            StandardOutputCaptureLimitChars,
            drainBeyondLimit: true,
            ioToken);
        var stderrTask = ReadBoundedToEndAsync(
            process.StandardError,
            StandardErrorCaptureLimitChars,
            drainBeyondLimit: true,
            ioToken);

        try
        {
            if (request.StandardInput is not null && request.CatalogProtocol is null)
            {
                await WriteStandardInputUntilChildClosesAsync(
                        process,
                        request.StandardInput,
                        ioToken)
                    .ConfigureAwait(false);
            }

            var exitTask = process.WaitForExitAsync(ioToken);
            if (request.CatalogProtocol is not null)
            {
                await Task.WhenAny(stdoutTask, exitTask).ConfigureAwait(false);
                if (stdoutTask.IsFaulted) await stdoutTask.ConfigureAwait(false);
            }
            await exitTask.ConfigureAwait(false);
            var standardOutput = await stdoutTask.ConfigureAwait(false);
            var standardError = await stderrTask.ConfigureAwait(false);

            if (standardOutput.WasTruncated || standardError.WasTruncated)
            {
                WriteBreadcrumb(
                    request,
                    "process-output-truncated",
                    outcome: "failed",
                    failureType: "OutputCaptureLimitExceeded",
                    childProcessId: process.Id);
            }

            WriteBreadcrumb(
                request,
                "process-complete",
                outcome: "exited",
                childProcessId: process.Id);
            return new SubscriptionCliProcessResult(
                process.ExitCode,
                standardOutput.Text,
                standardError.Text)
            {
                StandardOutputTruncated = standardOutput.WasTruncated,
                StandardErrorTruncated = standardError.WasTruncated
            };
        }
        catch (OperationCanceledException) when (
            !ct.IsCancellationRequested && preempted.IsCancellationRequested)
        {
            WriteBreadcrumb(
                request,
                "process-preempt-reap-start",
                outcome: "preempted",
                childProcessId: process.Id);
            await ChildProcessReaper.KillProcessTreeAndWaitAsync(process, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await ObservePipeTasksAfterTerminationAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            WriteBreadcrumb(
                request,
                "process-preempt-kill-wait-complete",
                outcome: "preempted",
                childProcessId: process.Id);
            throw new SubscriptionCliPreemptedException(
                $"Subscription CLI '{request.FileName}' was stopped so user-waiting work could run.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            WriteBreadcrumb(
                request,
                "process-timeout-reap-start",
                outcome: "timeout",
                childProcessId: process.Id);
            await ChildProcessReaper.KillProcessTreeAndWaitAsync(process, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await ObservePipeTasksAfterTerminationAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            WriteBreadcrumb(
                request,
                "process-timeout-kill-wait-complete",
                outcome: "timeout",
                childProcessId: process.Id);
            throw new TimeoutException(
                $"Subscription CLI '{request.FileName}' did not exit within {request.Timeout.TotalSeconds:0} seconds.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            WriteBreadcrumb(
                request,
                "process-cancel-reap-start",
                outcome: "cancelled",
                childProcessId: process.Id);
            await ChildProcessReaper.KillProcessTreeAndWaitAsync(process, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await ObservePipeTasksAfterTerminationAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            WriteBreadcrumb(
                request,
                "process-cancel-kill-wait-complete",
                outcome: "cancelled",
                childProcessId: process.Id);
            throw;
        }
        catch
        {
            // A malformed control response must not leave an interactive child waiting on stdin.
            await ChildProcessReaper.KillProcessTreeAndWaitAsync(process, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            try { await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    private void WriteBreadcrumb(
        SubscriptionCliProcessRequest request,
        string stage,
        string? outcome = null,
        string? failureType = null,
        int? childProcessId = null)
    {
        try
        {
            _crashBreadcrumbs.Write(new CrashBreadcrumbEvent
            {
                Component = nameof(DefaultSubscriptionCliProcessRunner),
                Operation = request.Operation.ToString(),
                Stage = stage,
                Provider = request.ProviderName,
                Outcome = outcome,
                FailureType = failureType,
                ChildProcessId = childProcessId
            });
        }
        catch
        {
            // Diagnostic sinks must never strand the execution lease or alter
            // subscription CLI behavior, even if an injected sink is faulty.
        }
    }

    private static async Task WriteStandardInputUntilChildClosesAsync(
        IChildProcess process,
        string standardInput,
        CancellationToken ct)
    {
        try
        {
            await process.StandardInput
                .WriteAsync(standardInput.AsMemory(), ct)
                .ConfigureAwait(false);
            await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExpectedClosedStandardInputPipe(ex, process))
        {
            // A short-lived CLI/wrapper may accept enough input (or reject its
            // command line) and close stdin just before the parent finishes its
            // write/flush. The process exit code and captured stderr are the
            // authoritative result; a concurrent ERROR_BROKEN_PIPE/
            // ERROR_NO_DATA race must not replace them with an IOException.
        }
        finally
        {
            // Do not synchronously flush a buffered StreamWriter after timeout or
            // caller cancellation; the child may not read stdin at all. The outer
            // cancellation path owns killing/reaping the process and its streams.
            if (!ct.IsCancellationRequested)
            {
                try
                {
                    process.StandardInput.Close();
                }
                catch (Exception ex) when (IsExpectedClosedStandardInputPipe(ex, process))
                {
                    // Closing an already-closed anonymous pipe is the same benign
                    // child-exit race as the write above.
                }
            }
        }
    }

    private static bool IsExpectedClosedStandardInputPipe(Exception exception, IChildProcess process)
    {
        if (exception is ObjectDisposedException)
        {
            return true;
        }

        if (exception is InvalidOperationException)
        {
            return HasExited(process);
        }

        if (exception is not IOException io)
        {
            return false;
        }

        // Windows reports ERROR_BROKEN_PIPE (109), ERROR_NO_DATA (232), or
        // ERROR_PIPE_NOT_CONNECTED (233). Unix commonly exposes EPIPE (32).
        // Keep a message fallback for framework/platform wrappers whose HRESULT
        // does not preserve the native code.
        var nativeCode = io.HResult & 0xffff;
        return nativeCode is 32 or 109 or 232 or 233
               || HasExited(process)
               || (io.Message.Contains("pipe", StringComparison.OrdinalIgnoreCase)
                   && (io.Message.Contains("closed", StringComparison.OrdinalIgnoreCase)
                       || io.Message.Contains("closing", StringComparison.OrdinalIgnoreCase)
                       || io.Message.Contains("broken", StringComparison.OrdinalIgnoreCase)
                       || io.Message.Contains("not connected", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool HasExited(IChildProcess process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    internal static async Task<SubscriptionCliBoundedTextCapture> ReadBoundedFileAsync(
        string path,
        int captureLimitChars,
        CancellationToken ct = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 8192,
            useAsync: true);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 8192,
            leaveOpen: false);
        return await ReadBoundedToEndAsync(
                reader,
                captureLimitChars,
                drainBeyondLimit: false,
                ct)
            .ConfigureAwait(false);
    }

    internal static async Task<SubscriptionCliBoundedTextCapture> ReadBoundedToEndAsync(
        StreamReader reader,
        int captureLimitChars,
        bool drainBeyondLimit,
        CancellationToken ct)
    {
        var safeLimit = Math.Max(1, captureLimitChars);
        var retained = new StringBuilder(Math.Min(safeLimit, 4096));
        var buffer = new char[8192];
        var wasTruncated = false;

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var available = safeLimit - retained.Length;
            if (available > 0)
            {
                retained.Append(buffer, 0, Math.Min(read, available));
            }

            if (read > available)
            {
                wasTruncated = true;
                if (!drainBeyondLimit)
                {
                    break;
                }
            }
        }

        return new SubscriptionCliBoundedTextCapture(retained.ToString(), wasTruncated);
    }

    private static async Task ObservePipeTasksAfterTerminationAsync(
        Task<SubscriptionCliBoundedTextCapture> stdoutTask,
        Task<SubscriptionCliBoundedTextCapture> stderrTask)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Cancellation has already killed and reaped the child. Observing
            // the terminal pipe tasks prevents unobserved faults during teardown.
        }
    }

}

public abstract class SubscriptionCliLlmProviderBase : ILlmProvider, ILlmModelCatalogProvider
{
    private const string SubscriptionUsageRemainingDiagnosticPrefix = "subscription_usage_remaining_fraction=";

    // The CLIs report usage with opposite polarity: Codex prints how much is
    // LEFT ("10% usage remaining"), Claude Code prints how much is SPENT
    // ("You've used 37% of your 5-hour limit", "82% used"). Both normalize to
    // a remaining fraction before reaching the quota tracker; the phrasing
    // itself carries the polarity, so both patterns apply to every CLI.
    private static readonly Regex UsageRemainingRegex = new(
        @"(?<percent>\d{1,3}(?:\.\d+)?)\s*%\s+(?:usage\s+)?remaining",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex UsageConsumedRegex = new(
        @"(?:used\s+(?<percent>\d{1,3}(?:\.\d+)?)\s*%|(?<percent>\d{1,3}(?:\.\d+)?)\s*%\s+(?:of\s+[^.\n]{0,60}?limit\b(?:\s+used)?|used\b|consumed\b))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Rate/usage-limit phrasings across the Claude/Codex/Gemini CLIs. Matched
    // against raw CLI output on non-zero exit so the failure can be classified
    // without quoting that output anywhere. Keep the token list aligned with
    // the router's message classifier (LlmRouter.Helpers IsRateLimitError).
    private static readonly Regex RateLimitOutputRegex = new(
        @"rate[\s_-]?limit|too\s+many\s+requests|resource[\s_-]?exhausted|quota|usage[\s_-]?limit|\b(?:session|daily|weekly|monthly)\s+limit\b|\b429\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Argument-parse failures from the CLI's own option parser (clap emits
    // "error: unexpected argument", commander "error: unknown option", plus
    // invalid-value/missing-argument/usage-banner variants). These fire when an
    // installed CLI release drops or renames a flag this provider still passes
    // — an instant exit that is otherwise indistinguishable from a model-call
    // failure once output is scrubbed (see the Jul 2026 Codex
    // `exec --ask-for-approval` removal, 141 silent exit-2 failures).
    private static readonly Regex InvocationErrorOutputRegex = new(
        @"unexpected\s+argument|unrecognized\s+(?:option|subcommand)|unknown\s+(?:option|argument|command)|invalid\s+value|argument\s+.{0,40}\s+(?:is\s+required|was\s+not\s+provided)|required\s+arguments?\s+(?:was|were)\s+not\s+provided|^\s*usage:",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex EmailRedactionRegex = new(
        @"[^\s@'""]+@[^\s@'""]+\.[^\s@'""]+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HomePathRedactionRegex = new(
        @"(?:/(?:Users|home)/|[A-Za-z]:\\Users\\)[^\s/\\'""]+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Long alphanumeric runs that contain a digit look like keys/tokens/session
    // ids; flag names and English words rarely do, so they survive redaction.
    private static readonly Regex TokenRedactionRegex = new(
        @"[A-Za-z0-9_\-]{16,}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IOptions<SubscriptionCliProviderOptions> _options;
    private readonly ISubscriptionCliProcessRunner _runner;
    private readonly ILogger _logger;
    private string? _preferredExecutablePath;
    private long _preferredExecutableVerifiedAtUtcTicks;
    private long _safeStopFailureCount;
    private long _safeStopSuppressedStackCount;
    private long _launchDeclineBackoffUntilUtcTicks;
    private static readonly TimeSpan AuthReadinessCacheLifetime = TimeSpan.FromMinutes(1);

    /// <summary>
    /// In-process debounce after a co-run launch decline — BACKGROUND work
    /// only. Retrying a declined launch immediately just re-runs the pre-launch
    /// orphan sweep against the same live owner marker, so for this window new
    /// background launch attempts return the same launch-declined failure
    /// without spawning anything. Deliberately short and never recorded in the
    /// cooldown tracker: a decline is self-inflicted and routinely clears
    /// within seconds. Foreground (user-waiting) attempts are exempt: they
    /// always try the real launch, both because the user is waiting and
    /// because each real foreground decline refreshes the cross-process
    /// foreground-pending sentinel that makes worker hosts yield the provider.
    /// </summary>
    internal static readonly TimeSpan LaunchDeclineBackoff = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Clock for the launch-decline debounce. Internal so tests can advance
    /// time instead of sleeping out the backoff window.
    /// </summary>
    internal Func<DateTime> UtcNow { get; set; } = static () => DateTime.UtcNow;

    protected SubscriptionCliLlmProviderBase(
        IOptions<SubscriptionCliProviderOptions> options,
        ISubscriptionCliProcessRunner runner,
        ILogger logger)
    {
        _options = options;
        _runner = runner;
        _logger = logger;
    }

    public abstract string ProviderName { get; }
    public bool SupportsStreaming => false;
    public bool SupportsEmbeddings => false;
    public string? DefaultEmbeddingModel => null;

    protected abstract SubscriptionCliExecutableOptions ExecutableOptions { get; }
    protected virtual string CommandName => ProviderName;
    protected virtual string DefaultPathEnvironmentVariable => string.Empty;
    protected virtual IReadOnlyList<string> BuiltInModels => [];

    public bool SupportsModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        return GetAllKnownModels()
            .Any(m => m.Equals(model, StringComparison.OrdinalIgnoreCase))
            || NormalizeCliModel(model) is { Length: > 0 };
    }

    public virtual bool SupportsVision(string model) => false;

    /// <summary>
    /// The CLI's read-only model catalog exchange, or null when the CLI has no
    /// machine-readable catalog. Each CLI provider declares its own.
    /// </summary>
    private protected virtual SubscriptionCliCatalogExchange? CatalogExchange => null;

    public async Task<List<string>> GetAvailableModelsAsync()
    {
        if (!ExecutableOptions.Enabled)
        {
            return [];
        }

        // A file on PATH is not an executable route. Model Scout treats this
        // list as live admission evidence, so only advertise subscription models
        // after the CLI itself confirms a logged-in subscription session.
        var readiness = await CheckConnectivityAsync(
                SubscriptionCliProcessPriority.Background)
            .ConfigureAwait(false);
        if (!readiness.IsHealthy)
        {
            Logger.LogDebug(
                "{Provider} subscription models are not advertised: {Reason}",
                ProviderName,
                readiness.Message);
            return [];
        }

        return GetAllKnownModels().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<LlmModelCatalog> GetModelCatalogAsync(CancellationToken cancellationToken = default)
    {
        if (!ExecutableOptions.Enabled) throw new InvalidOperationException("This CLI route is disabled.");
        var exchange = CatalogExchange
            ?? throw new InvalidOperationException("This CLI does not expose a machine-readable model catalog. Use its default or enter a custom model ID.");
        var readiness = await ResolveAuthenticatedExecutableAsync(true, SubscriptionCliProcessPriority.Foreground, cancellationToken).ConfigureAwait(false);
        if (readiness.Executable is null) throw new InvalidOperationException(readiness.Message);
        var request = new SubscriptionCliProcessRequest(readiness.Executable, exchange.Arguments, null, Path.GetTempPath(), TimeSpan.FromSeconds(12),
            ProviderName: ProviderName, Operation: SubscriptionCliProcessOperation.ModelDiscovery, Priority: SubscriptionCliProcessPriority.Foreground, CatalogProtocol: exchange.Protocol);
        var result = await _runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.OutputWasTruncated) throw new InvalidDataException("The CLI model catalog was incomplete.");
        var catalog = SubscriptionCliModelDiscovery.ParseCatalog(result.StandardOutput, exchange.Protocol, exchange.ModelPrefix);
        if (catalog.Models.Count == 0) throw new InvalidOperationException("The installed CLI did not return a model catalog. Update it or enter a custom model ID.");
        return catalog with
        {
            ReasoningEfforts = SubscriptionCliModelDiscovery.ParseReasoningEfforts(result.StandardOutput, exchange.Protocol,
                exchange.ModelPrefix,
                ProviderPackCatalog.GetRequired(ProviderName).SubscriptionCli!.ReasoningEfforts)
        };
    }

    public Task<(bool IsHealthy, string Message)> TestConnectivityAsync() =>
        CheckConnectivityAsync(SubscriptionCliProcessPriority.Background);

    private async Task<(bool IsHealthy, string Message)> CheckConnectivityAsync(
        SubscriptionCliProcessPriority priority)
    {
        if (!ExecutableOptions.Enabled)
        {
            return (false, $"{ProviderName} is disabled.");
        }

        var readiness = await ResolveAuthenticatedExecutableAsync(
                allowRecent: false,
                priority,
                CancellationToken.None)
            .ConfigureAwait(false);
        return (readiness.Executable is not null, readiness.Message);
    }

    private async Task<(string? Executable, string Message)> ResolveAuthenticatedExecutableAsync(
        bool allowRecent,
        SubscriptionCliProcessPriority priority,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (priority == SubscriptionCliProcessPriority.Background
            && IsInLaunchDeclineBackoff())
        {
            // A launch was just declined by the cross-process co-run guard.
            // Don't re-run the orphan sweep or spawn background probes for a
            // few seconds; report the same transient decline so callers keep
            // their retry-shortly signal without hammering the guard.
            // Foreground attempts deliberately skip this short-circuit: the
            // user is waiting, the guard may have cleared a moment ago, and a
            // real foreground attempt is what refreshes the cross-process
            // foreground-pending sentinel worker hosts yield to.
            return (null, LaunchDeclinedMessage);
        }

        if (allowRecent
            && TryGetRecentlyAuthenticatedExecutable() is { } recent)
        {
            return (recent, $"{ProviderName} CLI is installed and its subscription session is authenticated.");
        }

        var candidates = ResolveExecutableCandidates();
        if (candidates.Count == 0)
        {
            return (null, $"{ProviderName} CLI executable was not found.");
        }

        if (ProviderName.Equals(SubscriptionCliProviders.ClaudeCode, StringComparison.OrdinalIgnoreCase)
            && ExecutableOptions.PreferLocalSubscriptionAuthProof)
        {
            // Older Claude versions lack `auth status`. Read subscription OAuth
            // proof off the caller's dispatcher; macOS may consult Keychain.
            var localProbe = await Task.Run(ProbeSubscriptionSessionWithoutCli, ct).ConfigureAwait(false);
            if (localProbe.IsAuthenticated)
            {
                return await ResolveLocallyAuthenticatedExecutableAsync(candidates, priority, ct)
                    .ConfigureAwait(false);
            }

            // An expired access token with a cached refresh token is renewed
            // only by the CLI itself; let its login-status boundary decide
            // instead of refusing every call until someone runs `claude`.
            if (!ClaudeCodeAuthProbe.IsRenewalPending(localProbe.Detail))
            {
                return (null,
                    localProbe.Detail
                    ?? $"{ProviderName} CLI is installed, but no authenticated subscription session is ready. Sign in with the CLI and retry.");
            }
        }

        var loginStatusArguments = SubscriptionCliLoginProbe.GetLoginStatusArguments(ProviderName);
        if (loginStatusArguments is null || loginStatusArguments.Count == 0)
        {
            // Some CLIs (Gemini) have no non-interactive status command; a
            // provider can still prove a session from on-disk auth state.
            var probe = await Task.Run(ProbeSubscriptionSessionWithoutCli, ct).ConfigureAwait(false);
            if (probe.IsAuthenticated)
            {
                return await ResolveLocallyAuthenticatedExecutableAsync(candidates, priority, ct)
                    .ConfigureAwait(false);
            }

            return (null,
                probe.Detail
                ?? $"{ProviderName} CLI is installed, but this CLI does not expose a supported non-interactive subscription login-status check. Automatic routing remains unavailable until login readiness can be verified safely.");
        }

        var anyProbeCompleted = false;
        var anyLaunchInfrastructureBlocked = false;
        var anyLocallyAuthenticatedExecutableFailed = false;
        Stopwatch? compatibilitySweep = null;
        var readinessTimeout = TimeSpan.FromSeconds(Math.Clamp(_options.Value.TimeoutSeconds, 1, 30));
        foreach (var executable in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = readinessTimeout - (compatibilitySweep?.Elapsed ?? TimeSpan.Zero);
            if (remaining <= TimeSpan.Zero)
            {
                BeginLaunchDeclineBackoff();
                return (null, LaunchDeclinedMessage);
            }
            SubscriptionCliLoginProbeResult probe;
            try
            {
                probe = await ProbeLoginStatusWithinReadinessAsync(
                        executable,
                        loginStatusArguments,
                        priority,
                        compatibilitySweep is null ? null : remaining,
                        ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // An exhausted owning slice ends this sweep. A quick launch
                // decline remains a normal probe result and can try a candidate.
                BeginLaunchDeclineBackoff();
                return (null, LaunchDeclinedMessage);
            }

            switch (probe.Status)
            {
                case SubscriptionCliLoginProbeStatus.CompletedAuthenticated:
                    // Any completed probe proves the safe-stop guard is no
                    // longer blocking process launch.
                    ClearLaunchDeclineBackoff();
                    LogSafeStopRecovery();
                    SetAuthenticatedExecutable(executable);
                    return (executable, $"{ProviderName} CLI is installed and its subscription session is authenticated.");

                case SubscriptionCliLoginProbeStatus.CompletedNotAuthenticated:
                    if (ProviderName.Equals(
                            SubscriptionCliProviders.ClaudeCode,
                            StringComparison.OrdinalIgnoreCase)
                        && !probe.StatusOutputRecognized)
                    {
                        // Claude Code 2.1.38 lacks `auth status` and treats
                        // those words as a normal print-mode prompt. Only in
                        // that unrecognized-output case may cached OAuth shape
                        // prove a subscription; explicit logged-out/API JSON
                        // still fails closed.
                        (bool IsAuthenticated, string? Detail) localProbe;
                        remaining = readinessTimeout - (compatibilitySweep?.Elapsed ?? TimeSpan.Zero);
                        if (remaining <= TimeSpan.Zero)
                        {
                            BeginLaunchDeclineBackoff();
                            return (null, LaunchDeclinedMessage);
                        }
                        try
                        {
                            localProbe = compatibilitySweep is null
                                ? await Task.Run(ProbeSubscriptionSessionWithoutCli, ct).ConfigureAwait(false)
                                : await SubscriptionCliExecutableReadiness.RunBoundedAsync(
                                    _ => Task.FromResult(ProbeSubscriptionSessionWithoutCli()),
                                    remaining, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            BeginLaunchDeclineBackoff();
                            return (null, LaunchDeclinedMessage);
                        }
                        if (localProbe.IsAuthenticated)
                        {
                            compatibilitySweep ??= Stopwatch.StartNew();
                            remaining = readinessTimeout - compatibilitySweep.Elapsed;
                            if (remaining <= TimeSpan.Zero)
                            {
                                BeginLaunchDeclineBackoff();
                                return (null, LaunchDeclinedMessage);
                            }
                            var executableReadiness = await SubscriptionCliExecutableReadiness.ProbeAsync(
                                _runner, ProviderName, [executable], GetWorkingDirectory(),
                                remaining,
                                priority, ct).ConfigureAwait(false);
                            ct.ThrowIfCancellationRequested();
                            if (executableReadiness.Status == SubscriptionCliExecutableReadinessStatus.Ready)
                            {
                                ClearLaunchDeclineBackoff();
                                LogSafeStopRecovery();
                                SetAuthenticatedExecutable(executable);
                                return (executable,
                                    $"{ProviderName} CLI is installed and its subscription session is authenticated.");
                            }
                            if (executableReadiness.DeadlineExpired)
                            {
                                BeginLaunchDeclineBackoff();
                                return (null, LaunchDeclinedMessage);
                            }
                            anyLocallyAuthenticatedExecutableFailed = true;
                            anyLaunchInfrastructureBlocked |= executableReadiness.Status
                                == SubscriptionCliExecutableReadinessStatus.InfrastructureBlocked;
                            continue;
                        }
                    }

                    // Also a completed probe, even though this particular CLI
                    // candidate reports an unauthenticated session.
                    anyProbeCompleted = true;
                    ClearLaunchDeclineBackoff();
                    LogSafeStopRecovery();
                    break;

                case SubscriptionCliLoginProbeStatus.InfrastructureBlocked:
                    anyLaunchInfrastructureBlocked = true;
                    LogInfrastructureBlockedProbe(probe.Failure);
                    break;

                default:
                    // ExecutionFailed: a discovered path can be an unlaunchable
                    // app-execution alias. Continue to later candidates and
                    // never include status output in the log or user-visible
                    // result.
                    Logger.LogDebug(
                        probe.Failure,
                        "{Provider} subscription login-status candidate failed to launch",
                        ProviderName);
                    break;
            }
        }

        if (!anyProbeCompleted && anyLaunchInfrastructureBlocked)
        {
            // No probe actually ran to completion, so a signed-out session was
            // never observed — every candidate died to transient launch
            // infrastructure (co-run guard, preemption, or a killed hang).
            // Reporting "sign in and retry" here blamed a healthy, logged-in
            // provider and earned it a 15-minute cooldown. Keep the cached
            // auth readiness too (the decline says nothing about the login
            // session — same rule as the inference-path decline arm), and
            // debounce further launch attempts briefly.
            BeginLaunchDeclineBackoff();
            return (null, LaunchDeclinedMessage);
        }

        ClearAuthenticatedExecutable();

        if (!anyProbeCompleted && anyLocallyAuthenticatedExecutableFailed)
        {
            return (null, $"{ProviderName} subscription metadata is available, but no CLI executable passed its readiness check.");
        }

        // Login-status output may contain an account email, organization, or
        // credential metadata. Never carry stdout/stderr into UI, logs, or a
        // Model Scout audit receipt.
        return (null,
            $"{ProviderName} CLI is installed, but no authenticated subscription session is ready. Sign in with the CLI and retry.");
    }

    /// <summary>
    /// Stable, machine-matchable failure text for a launch the cross-process
    /// co-run guard declined (or a probe it starved). "CLI launch was declined"
    /// is the phrase <see cref="LlmErrorPolicy"/> classifies as
    /// <see cref="LlmFailureCategory.LaunchDeclined"/> — transient,
    /// non-penalizing, fallback-eligible. Keep the two in sync.
    /// </summary>
    private string LaunchDeclinedMessage =>
        $"{ProviderName} CLI launch was declined: another app process is using the subscription CLI. The call can be retried.";

    private void LogSafeStopFailure(SubscriptionCliSafeStopException exception)
    {
        var failureCount = Interlocked.Increment(ref _safeStopFailureCount);
        if (failureCount == 1)
        {
            Logger.LogDebug(
                exception,
                "{Provider} subscription login-status probe was blocked by the safe-stop guard; repeated stacks will be suppressed until the earlier CLI process exits",
                ProviderName);
            return;
        }

        var suppressed = Interlocked.Increment(ref _safeStopSuppressedStackCount);
        // Emit compact progress at powers of two. This preserves evidence that
        // the condition persists without producing one stack per executable
        // candidate on every Model Scout sweep.
        if ((failureCount & (failureCount - 1)) == 0)
        {
            Logger.LogInformation(
                "{Provider} subscription CLI safe-stop guard remains active after {FailureCount} blocked candidate probes; {SuppressedCount} repeated exception stack(s) suppressed",
                ProviderName,
                failureCount,
                suppressed);
        }
    }

    /// <summary>
    /// Per-cause logging for an infrastructure-blocked login probe, preserving
    /// the exact pre-refactor messages: safe-stop keeps its compact counting
    /// via <see cref="LogSafeStopFailure"/>; preemption and killed hangs stay
    /// Debug-level scheduling notes, never login verdicts.
    /// </summary>
    private void LogInfrastructureBlockedProbe(Exception? failure)
    {
        switch (failure)
        {
            case SubscriptionCliSafeStopException safeStop:
                LogSafeStopFailure(safeStop);
                break;
            case SubscriptionCliPreemptedException preempted:
                Logger.LogDebug(
                    preempted,
                    "{Provider} subscription login-status probe was preempted by user-waiting work",
                    ProviderName);
                break;
            case TimeoutException timedOut:
                Logger.LogDebug(
                    timedOut,
                    "{Provider} subscription login-status probe timed out before it could answer",
                    ProviderName);
                break;
        }
    }

    private void LogSafeStopRecovery()
    {
        var failures = Interlocked.Exchange(ref _safeStopFailureCount, 0);
        var suppressed = Interlocked.Exchange(ref _safeStopSuppressedStackCount, 0);
        if (failures == 0) return;

        Logger.LogInformation(
            "{Provider} subscription CLI safe-stop guard cleared after {FailureCount} blocked candidate probes; {SuppressedCount} repeated exception stack(s) were suppressed",
            ProviderName,
            failures,
            suppressed);
    }

    public async Task<LlmResponse> GenerateResponseAsync(
        string prompt,
        string model,
        Dictionary<string, object>? executionSettings = null)
    {
        var requestCancellation =
            LlmReservedExecutionSettings.GetRequestCancellationToken(executionSettings);
        requestCancellation.ThrowIfCancellationRequested();
        var processPriority =
            SubscriptionCliExecutionSettings.GetProcessPriority(executionSettings);
        var stopwatch = Stopwatch.StartNew();
        if (!ExecutableOptions.Enabled)
        {
            stopwatch.Stop();
            return FailedResponse(model, stopwatch, $"{ProviderName} is disabled.");
        }

        // Checked before the readiness probe so an unsupported request spends no
        // CLI launch, and reported like every other failure here.
        var attachments = executionSettings?.GetValueOrDefault(CliImageAttachments.ExecutionSetting) as CliImageAttachments;
        if (attachments is not null && !SupportsVision(model))
        {
            stopwatch.Stop();
            return FailedResponse(model, stopwatch, "Selected subscription CLI model does not support images.");
        }

        // Provider boundary: never invoke inference merely because a CLI path
        // exists. Every direct call must use a candidate that recently proved a
        // subscription session, so Console/API auth cannot silently incur spend.
        var readiness = await ResolveAuthenticatedExecutableAsync(
                allowRecent: true,
                processPriority,
                requestCancellation)
            .ConfigureAwait(false);
        if (readiness.Executable is null)
        {
            stopwatch.Stop();
            return FailedResponse(model, stopwatch, readiness.Message);
        }
        using var imageFiles = CliImageAttachmentLease.Create(attachments, GetWorkingDirectory());
        if (imageFiles.Paths.Count > 0)
        {
            executionSettings = executionSettings is null ? new() : new(executionSettings);
            executionSettings[CliImageAttachmentLease.PathsSetting] = imageFiles.Paths;
        }
        var normalizedModel = NormalizeCliModel(model);
        var request = BuildRequest(
                readiness.Executable,
                prompt,
                model,
                normalizedModel,
                executionSettings)
            with
        {
            ProviderName = ProviderName,
            Operation = SubscriptionCliProcessOperation.Inference,
            Priority = processPriority,
            RequestedModel = model
        };

        // A using declaration compiles to a provider-level finally and keeps
        // ownership active across every return and exception below.
        using var outputFileCleanup = new OutputFileCleanupLease(request.OutputPath);

        SubscriptionCliProcessResult result;
        try
        {
            result = await _runner.RunAsync(request, requestCancellation).ConfigureAwait(false);
            stopwatch.Stop();
            // The launch actually ran, so the co-run guard is demonstrably no
            // longer declining; drop any lingering decline debounce.
            ClearLaunchDeclineBackoff();
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
            // Watchdog cancellation is a scheduling outcome. The process runner
            // has killed and reaped the child; keep auth readiness intact and let
            // the coordinator decide whether to resume or try another route.
            stopwatch.Stop();
            throw;
        }
        catch (Exception ex) when (ex is SubscriptionCliSafeStopException
                                       or SubscriptionCliPreemptedException)
        {
            // Deliberate cross-process co-run decline: another live app
            // process holds the subscription CLI slot (or the pre-launch orphan
            // sweep was blocked), so this launch was refused before the CLI ran.
            // Nothing is wrong with the CLI or its login — keep the cached auth
            // readiness so a retry can skip a fresh probe, and report a distinct
            // stable message that classifies as LaunchDeclined (no provider
            // cooldown) instead of the penalizing "could not be started".
            stopwatch.Stop();
            BeginLaunchDeclineBackoff();
            Logger.LogDebug(
                ex,
                "{Provider} CLI inference launch was declined by the cross-process co-run guard",
                ProviderName);
            return FailedResponse(model, stopwatch, LaunchDeclinedMessage);
        }
        catch (TimeoutException ex)
        {
            // The CLI started but hung past its deadline and was killed. Keep
            // its Timeout classification ("timed out" is the stable keyword the
            // error policy matches) instead of misreporting a launch failure.
            stopwatch.Stop();
            ClearAuthenticatedExecutable();
            Logger.LogDebug(ex, "{Provider} authenticated CLI execution timed out", ProviderName);
            return FailedResponse(
                model,
                stopwatch,
                $"{ProviderName} CLI call timed out and the process was stopped.");
        }
        catch (Exception ex)
        {
            // Genuine launch failure (deleted binary, spawn failure, pipe or
            // containment breakage). This one really is provider trouble and
            // keeps its penalizing classification.
            stopwatch.Stop();
            ClearAuthenticatedExecutable();
            Logger.LogDebug(ex, "{Provider} authenticated CLI execution failed to launch", ProviderName);
            return FailedResponse(model, stopwatch, $"{ProviderName} CLI call could not be started.");
        }

        if (result.OutputWasTruncated)
        {
            // A retained prefix is never a complete response contract. This is
            // deliberately checked before exit-code and output-file parsing so
            // neither successful-looking JSON nor a valid marker near the start
            // of an oversized stream can be accepted. Do not log the retained
            // output: CLIs may echo prompts, credentials, or account metadata.
            Logger.LogWarning(
                "{Provider} CLI output exceeded the safe capture limit and was discarded as incomplete (stdout truncated: {StdoutTruncated}; stderr truncated: {StderrTruncated})",
                ProviderName,
                result.StandardOutputTruncated,
                result.StandardErrorTruncated);
            return FailedResponse(
                model,
                stopwatch,
                $"{ProviderName} CLI returned more output than the safe capture limit; the incomplete response was discarded.");
        }

        if (result.ExitCode != 0)
        {
            ClearAuthenticatedExecutable();
            var failed = new LlmResponse
            {
                Content = string.Empty,
                Provider = ProviderName,
                Model = model,
                ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
                WasSuccessful = false,
                ErrorMessage = BuildNonZeroExitErrorMessage(result)
            };
            AddUsageRemainingDiagnostic(failed, result);
            return failed;
        }

        string content;
        try
        {
            content = await ExtractContentAsync(request, result).ConfigureAwait(false);
        }
        catch (SubscriptionCliOutputLimitExceededException)
        {
            // Codex writes its final answer to --output-last-message rather
            // than stdout. Apply the same fail-closed contract to that file;
            // never log its retained prefix because it may contain the prompt
            // or other sensitive response material.
            Logger.LogWarning(
                "{Provider} CLI response file exceeded the safe capture limit and was discarded as incomplete",
                ProviderName);
            return FailedResponse(
                model,
                stopwatch,
                $"{ProviderName} CLI returned more output than the safe capture limit; the incomplete response was discarded.");
        }
        if (string.IsNullOrWhiteSpace(content))
        {
            // Exit 0 with empty stdout is the CLI failure mode that is
            // invisible after the fact: stderr is normally discarded on
            // success, so retain only structural evidence that stderr existed.
            // Even an ordinary sentence can echo private prompt content; no
            // content-derived excerpt or deterministic hash belongs in logs.
            // The response still reports success — exit 0 is the CLI's contract
            // — and the caller's quality layer owns retry.
            var stderrCategory = string.IsNullOrWhiteSpace(result.StandardError)
                ? "empty"
                : "present";
            Logger.LogWarning(
                "{Provider} CLI exited 0 but produced an empty result after {ElapsedMs}ms (stdout {StdoutChars} chars, stderr {StderrChars} chars, stderr category: {StderrCategory})",
                ProviderName,
                stopwatch.ElapsedMilliseconds,
                result.StandardOutput?.Length ?? 0,
                result.StandardError?.Length ?? 0,
                stderrCategory);
        }
        var response = new LlmResponse
        {
            Content = content,
            Provider = ProviderName,
            Model = model,
            InputTokens = EstimateTokens(prompt),
            OutputTokens = EstimateTokens(content),
            TotalTokens = EstimateTokens(prompt) + EstimateTokens(content),
            EstimatedCost = 0m,
            ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
            WasSuccessful = true
        };
        if (ReadReportedUsage(result) is { } usage)
        {
            response.InputTokens = usage.InputTokens;
            response.OutputTokens = usage.OutputTokens;
            response.TotalTokens = checked(usage.InputTokens + usage.OutputTokens);
            response.CacheReadInputTokens = usage.CachedInputTokens;
            response.UsageMeasurementComplete = true;
        }
        AddUsageRemainingDiagnostic(response, result);
        return response;
    }

    /// <summary>Only structured CLI-owned usage qualifies; character estimates never become measured receipts.</summary>
    protected virtual ChatUsage? ReadReportedUsage(SubscriptionCliProcessResult result) => null;

    private LlmResponse FailedResponse(string model, Stopwatch stopwatch, string message)
        => new()
        {
            Content = string.Empty,
            Provider = ProviderName,
            Model = model,
            ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
            WasSuccessful = false,
            ErrorMessage = message
        };

    private static void TryDeleteOutputFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup only. The original provider result remains
            // authoritative even when an external process still holds the file.
        }
    }

    private sealed class OutputFileCleanupLease(string? path) : IDisposable
    {
        public void Dispose()
        {
            // Codex creates --output-last-message before the runner starts.
            // Own that temporary file at the provider boundary so every exit
            // path cleans it: success, non-zero exit, timeout, cancellation,
            // co-run decline, launch exception, or output-contract rejection.
            // Claude/Gemini requests have no OutputPath, so this cannot cross
            // the subscription-CLI provider boundary into API/local routes.
            TryDeleteOutputFile(path);
        }
    }

    private async Task<SubscriptionCliLoginProbeResult> ProbeLoginStatusWithinReadinessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        SubscriptionCliProcessPriority priority,
        TimeSpan? remaining,
        CancellationToken ct)
    {
        Task<SubscriptionCliLoginProbeResult> Probe(CancellationToken token) =>
            SubscriptionCliLoginProbe.ProbeAsync(_runner, ProviderName, executable,
                arguments, GetWorkingDirectory(), remaining
                    ?? TimeSpan.FromSeconds(Math.Min(30, Math.Max(5, _options.Value.TimeoutSeconds))),
                priority, token);
        if (remaining is null) return await Probe(ct).ConfigureAwait(false);
        // Preserve owning-deadline cancellation for the compatibility loop;
        // generic infrastructure failure is not equivalent to an expired slice.
        return await SubscriptionCliExecutableReadiness.RunBoundedAsync(
            Probe, remaining.Value, ct).ConfigureAwait(false);
    }

    private async Task<(string? Executable, string Message)> ResolveLocallyAuthenticatedExecutableAsync(
        IReadOnlyList<string> candidates,
        SubscriptionCliProcessPriority priority,
        CancellationToken ct)
    {
        var readiness = await SubscriptionCliExecutableReadiness.ProbeAsync(
            _runner, ProviderName, candidates, GetWorkingDirectory(),
            TimeSpan.FromSeconds(Math.Clamp(_options.Value.TimeoutSeconds, 1, 30)),
            priority, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (readiness.Status == SubscriptionCliExecutableReadinessStatus.Ready)
        {
            ClearLaunchDeclineBackoff();
            LogSafeStopRecovery();
            SetAuthenticatedExecutable(readiness.Executable!);
            return (readiness.Executable, $"{ProviderName} CLI is installed and its subscription session is authenticated.");
        }
        if (readiness.Status == SubscriptionCliExecutableReadinessStatus.InfrastructureBlocked)
        {
            BeginLaunchDeclineBackoff();
            return (null, LaunchDeclinedMessage);
        }
        ClearAuthenticatedExecutable();
        return (null, $"{ProviderName} subscription metadata is available, but no CLI executable passed its readiness check.");
    }

    private string? TryGetRecentlyAuthenticatedExecutable()
    {
        var verifiedTicks = Interlocked.Read(ref _preferredExecutableVerifiedAtUtcTicks);
        var executable = Volatile.Read(ref _preferredExecutablePath);
        if (verifiedTicks <= 0
            || string.IsNullOrWhiteSpace(executable)
            || !File.Exists(executable))
        {
            return null;
        }

        var age = DateTime.UtcNow - new DateTime(verifiedTicks, DateTimeKind.Utc);
        return age >= TimeSpan.Zero && age <= AuthReadinessCacheLifetime
            ? executable
            : null;
    }

    private void SetAuthenticatedExecutable(string executable)
    {
        Volatile.Write(ref _preferredExecutablePath, executable);
        Interlocked.Exchange(ref _preferredExecutableVerifiedAtUtcTicks, DateTime.UtcNow.Ticks);
    }

    private void ClearAuthenticatedExecutable()
    {
        Interlocked.Exchange(ref _preferredExecutableVerifiedAtUtcTicks, 0);
        Volatile.Write(ref _preferredExecutablePath, null);
    }

    private bool IsInLaunchDeclineBackoff()
    {
        var untilTicks = Interlocked.Read(ref _launchDeclineBackoffUntilUtcTicks);
        return untilTicks > 0 && UtcNow().Ticks < untilTicks;
    }

    private void BeginLaunchDeclineBackoff() =>
        Interlocked.Exchange(
            ref _launchDeclineBackoffUntilUtcTicks,
            UtcNow().Add(LaunchDeclineBackoff).Ticks);

    private void ClearLaunchDeclineBackoff() =>
        Interlocked.Exchange(ref _launchDeclineBackoffUntilUtcTicks, 0);

    public Task<LlmResponse> GenerateVisionResponseAsync(
        string prompt,
        string imageBase64,
        string model,
        Dictionary<string, object>? executionSettings = null)
    {
        if (SupportsVision(model))
        {
            var settings = executionSettings is null ? new Dictionary<string, object>() : new(executionSettings);
            var media = imageBase64.StartsWith("/9j/", StringComparison.Ordinal) ? "image/jpeg"
                : imageBase64.StartsWith("UklGR", StringComparison.Ordinal) ? "image/webp" : "image/png";
            if (imageBase64.StartsWith("data:", StringComparison.Ordinal))
            {
                var separator = imageBase64.IndexOf(";base64,", StringComparison.Ordinal);
                if (separator < 5) throw new ArgumentException("Vision input must be base64 image data.");
                media = imageBase64[5..separator];
                imageBase64 = imageBase64[(separator + 8)..];
            }
            settings[CliImageAttachments.ExecutionSetting] = new CliImageAttachments([new(imageBase64, media)]);
            return GenerateResponseAsync(prompt, model, settings);
        }
        return Task.FromResult(new LlmResponse
        {
            Content = string.Empty,
            Provider = ProviderName,
            Model = model,
            WasSuccessful = false,
            ErrorMessage = $"{ProviderName} subscription CLI provider does not support vision routing."
        });
    }

    public Task<LlmResponse> GenerateStreamingResponseAsync(
        string prompt,
        string model,
        Func<string, Task> onChunk,
        Dictionary<string, object>? executionSettings = null) =>
        throw new NotSupportedException($"{ProviderName} subscription CLI provider does not support streaming.");

    public Task<LlmEmbeddingResponse> GenerateEmbeddingAsync(
        string text,
        string model,
        Dictionary<string, object>? executionSettings = null) =>
        throw new NotSupportedException($"{ProviderName} subscription CLI provider does not support embeddings.");

    public Task<List<LlmEmbeddingResponse>> GenerateEmbeddingsAsync(
        IList<string> texts,
        string model,
        Dictionary<string, object>? executionSettings = null) =>
        throw new NotSupportedException($"{ProviderName} subscription CLI provider does not support embeddings.");

    protected abstract SubscriptionCliProcessRequest BuildRequest(
        string executable,
        string prompt,
        string routerModel,
        string? normalizedCliModel,
        Dictionary<string, object>? executionSettings);

    protected virtual Task<string> ExtractContentAsync(
        SubscriptionCliProcessRequest request,
        SubscriptionCliProcessResult result) =>
        Task.FromResult(result.StandardOutput.Trim());

    /// <summary>
    /// Session proof for CLIs with no non-interactive status command (a null
    /// <see cref="SubscriptionCliLoginProbe.GetLoginStatusArguments"/> entry):
    /// a provider may verify auth from on-disk state instead. Default keeps
    /// the fail-closed behavior. Detail is user-facing guidance; never include
    /// account identifiers or raw output.
    /// </summary>
    protected virtual (bool IsAuthenticated, string? Detail) ProbeSubscriptionSessionWithoutCli()
        => (false, null);

    /// <summary>
    /// The host's project folder (<see cref="LlmReservedExecutionSettings.SubscriptionCliWorkingDirectory"/>) when it
    /// is an existing, fully qualified directory and the request carries no image files; otherwise the private
    /// scratch folder. Only CLIs whose contract loads no project settings, hooks or MCP servers may use this.
    /// </summary>
    protected string GetWorkingDirectory(IReadOnlyDictionary<string, object>? executionSettings)
    {
        if (executionSettings is not null
            && !executionSettings.ContainsKey(CliImageAttachmentLease.PathsSetting)
            && executionSettings.TryGetValue(LlmReservedExecutionSettings.SubscriptionCliWorkingDirectory, out var value)
            && value is string folder
            && Path.IsPathFullyQualified(folder)
            && Directory.Exists(folder))
        {
            return folder;
        }

        return GetWorkingDirectory();
    }

    protected string GetWorkingDirectory()
    {
        var configured = _options.Value.WorkingDirectory;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            Directory.CreateDirectory(configured);
            return configured;
        }

        var temp = Path.Combine(Path.GetTempPath(), "SwarmCore.SubscriptionCli");
        Directory.CreateDirectory(temp);
        return temp;
    }

    protected TimeSpan GetTimeout(Dictionary<string, object>? executionSettings)
    {
        var seconds = ReadInt(executionSettings, "subscription_cli_timeout_seconds")
            ?? ReadInt(executionSettings, "timeout_seconds")
            ?? _options.Value.TimeoutSeconds;

        return TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 3600));
    }

    protected static int? ReadInt(Dictionary<string, object>? settings, string key)
    {
        if (settings is null || !settings.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            int i => i,
            long l => checked((int)l),
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => null
        };
    }

    protected string? NormalizeCliModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var trimmed = model.Trim();
        var colon = trimmed.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0 && colon < trimmed.Length - 1)
        {
            trimmed = trimmed[(colon + 1)..];
        }

        return trimmed.Equals("default", StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }

    private IReadOnlyList<string> ResolveExecutableCandidates()
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var candidates = new List<string>();
        var seen = new HashSet<string>(comparer);

        void Add(string? candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate)
                && File.Exists(candidate)
                && seen.Add(candidate))
            {
                candidates.Add(candidate);
            }
        }

        // Connectivity selects a candidate by actual authenticated execution.
        // Reuse that exact path for inference before consulting discovery again.
        Add(Volatile.Read(ref _preferredExecutablePath));
        foreach (var candidate in SubscriptionCliDiscovery.ResolveExecutableCandidates(ProviderName, ExecutableOptions))
        {
            Add(candidate);
        }

        return candidates;
    }

    private List<string> GetAllKnownModels()
    {
        var models = new List<string>();
        if (!string.IsNullOrWhiteSpace(ExecutableOptions.DefaultModel))
        {
            models.Add(ExecutableOptions.DefaultModel);
        }

        models.AddRange(BuiltInModels);
        models.AddRange(ExecutableOptions.AdditionalModels.Where(m => !string.IsNullOrWhiteSpace(m)));
        return models;
    }

    protected static int EstimateTokens(string text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : (int)Math.Ceiling(text.Length / 4.0);

    // CLI output can echo prompts, account identifiers, or auth metadata, so
    // non-zero-exit errors never quote it. Rate/usage-limit failures still
    // have to stay classifiable, though: the router's cooldown tracker matches
    // on error text, and an opaque "exited with code 1" leaves a rate-limited
    // CLI in the candidate pool where Model Scout keeps re-selecting it on
    // every following turn. Scan the output privately, then emit only
    // canonical phrasing plus a normalized retry-after hint.
    private string BuildNonZeroExitErrorMessage(SubscriptionCliProcessResult result)
    {
        var output = string.Join('\n', result.StandardError, result.StandardOutput);
        if (string.IsNullOrWhiteSpace(output) || !RateLimitOutputRegex.IsMatch(output))
        {
            var invocationError = !string.IsNullOrWhiteSpace(output)
                && InvocationErrorOutputRegex.IsMatch(output);
            LogRedactedNonZeroExit(result, invocationError);
            return invocationError
                ? $"{ProviderName} CLI exited with code {result.ExitCode} (invocation error: the installed CLI rejected this provider's arguments, likely a CLI version mismatch)."
                : $"{ProviderName} CLI exited with code {result.ExitCode} (runtime failure).";
        }

        var message =
            Regex.IsMatch(output, @"\bsession\s+limit\b", RegexOptions.IgnoreCase)
                ? $"{ProviderName} session limit reached (subscription usage limit)."
                : $"{ProviderName} CLI exited with code {result.ExitCode}: subscription rate limit / usage limit reached.";
        if (RateLimitRetryAfterParser.TryParse(output) is { } retryAfter && retryAfter > TimeSpan.Zero)
        {
            var minutes = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalMinutes));
            message += $" Try again in {minutes} minutes.";
            message += $" Resets at {DateTimeOffset.Now.Add(retryAfter):h:mm tt}.";
        }

        return message;
    }

    // Debug-only breadcrumb so an invocation break stays diagnosable from logs
    // without carrying raw CLI output anywhere user-visible. Only the first
    // non-empty stderr (else stdout) line is logged, truncated and with
    // emails, home-directory usernames, and token-like runs masked.
    private void LogRedactedNonZeroExit(SubscriptionCliProcessResult result, bool invocationError)
    {
        if (!Logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        var firstLine = FirstNonEmptyLine(result.StandardError)
            ?? FirstNonEmptyLine(result.StandardOutput);
        Logger.LogDebug(
            "{Provider} CLI exited with code {ExitCode} ({Category}); first output line (redacted): {FirstLine}",
            ProviderName,
            result.ExitCode,
            invocationError ? "invocation error" : "runtime failure",
            firstLine is null ? "<no output>" : RedactCliOutputLine(firstLine));
    }

    private static string? FirstNonEmptyLine(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return null;
    }

    private static string RedactCliOutputLine(string line)
    {
        var redacted = EmailRedactionRegex.Replace(line, "[email]");
        redacted = HomePathRedactionRegex.Replace(redacted, "[home]");
        redacted = TokenRedactionRegex.Replace(
            redacted,
            match => match.Value.Any(char.IsAsciiDigit) ? "[token]" : match.Value);
        return redacted.Length <= 200 ? redacted : redacted[..200] + "…";
    }

    private static void AddUsageRemainingDiagnostic(LlmResponse response, SubscriptionCliProcessResult result)
    {
        var output = string.Join('\n', result.StandardError, result.StandardOutput);
        var consumedPolarity = false;
        var match = UsageRemainingRegex.Match(output);
        if (!match.Success)
        {
            match = UsageConsumedRegex.Match(output);
            consumedPolarity = true;
            if (!match.Success)
            {
                return;
            }
        }

        if (!decimal.TryParse(
                match.Groups["percent"].Value,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var percent))
        {
            return;
        }

        var fraction = Math.Clamp(percent / 100m, 0m, 1m);
        var remainingFraction = consumedPolarity ? 1m - fraction : fraction;
        response.Diagnostics.Add(
            SubscriptionUsageRemainingDiagnosticPrefix
            + remainingFraction.ToString("0.####", CultureInfo.InvariantCulture));
    }

    protected ILogger Logger => _logger;
}

public sealed class ClaudeCodeLlmProvider : SubscriptionCliLlmProviderBase
{
    private static readonly SubscriptionCliProviderPack CliPack =
        ProviderPackCatalog.GetRequired(SubscriptionCliProviders.ClaudeCode).SubscriptionCli!;
    private readonly IOptions<SubscriptionCliProviderOptions> _options;
    private readonly Func<(bool IsAuthenticated, string? Detail)> _subscriptionSessionProbe;

    public ClaudeCodeLlmProvider(
        IOptions<SubscriptionCliProviderOptions> options,
        ISubscriptionCliProcessRunner runner,
        ILogger<ClaudeCodeLlmProvider> logger,
        Func<(bool IsAuthenticated, string? Detail)>? subscriptionSessionProbe = null)
        : base(options, runner, logger)
    {
        _options = options;
        _subscriptionSessionProbe = subscriptionSessionProbe
            ?? (() => ClaudeCodeAuthProbe.Probe());
    }

    public override string ProviderName => SubscriptionCliProviders.ClaudeCode;
    private protected override SubscriptionCliCatalogExchange? CatalogExchange { get; } = new(
        SubscriptionCliCatalogProtocol.ClaudeInitialize,
        ["--print", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--no-session-persistence", "--setting-sources", ""],
        ProviderPackCatalog.GetRequired(SubscriptionCliProviders.ClaudeCode).ModelPrefix);
    protected override string CommandName => CliPack.Command;
    protected override SubscriptionCliExecutableOptions ExecutableOptions => _options.Value.ClaudeCode;
    protected override IReadOnlyList<string> BuiltInModels => CliPack.BuiltInModels;

    protected override (bool IsAuthenticated, string? Detail) ProbeSubscriptionSessionWithoutCli()
        => _subscriptionSessionProbe();
    // Login-status arguments and the authenticated-session parser live in the
    // shared SubscriptionCliLoginProbe — one implementation for the runtime
    // provider stack and the setup/detection stack.

    protected override SubscriptionCliProcessRequest BuildRequest(
        string executable,
        string prompt,
        string routerModel,
        string? normalizedCliModel,
        Dictionary<string, object>? executionSettings)
    {
        var maxTurns = (ReadInt(executionSettings, "max_turns")
                        ?? Math.Max(1, _options.Value.ClaudeCode.MaxTurns))
            .ToString(CultureInfo.InvariantCulture);
        var args = CliPack.InferenceArguments
            .Select(argument => argument == "{maxTurns}" ? maxTurns : argument)
            .ToList();

        var selection = ModelReasoningSelection.Split(normalizedCliModel ?? "");
        normalizedCliModel = selection.Model;
        var effort = executionSettings?.GetValueOrDefault("reasoning_effort")?.ToString() ?? selection.Effort;
        if (!string.IsNullOrWhiteSpace(effort))
        {
            if (!CliPack.ReasoningEfforts.Contains(effort, StringComparer.Ordinal))
                throw new NotSupportedException("The selected Claude Code effort is not supported by this adapter.");
            args.Add("--effort"); args.Add(effort);
        }
        if (!string.IsNullOrWhiteSpace(normalizedCliModel))
        {
            args.Add(CliPack.ModelArgument);
            args.Add(normalizedCliModel);
        }

        if (_options.Value.ClaudeCode.MaxBudgetUsd is { } maxBudget)
        {
            args.Add("--max-budget-usd");
            args.Add(maxBudget.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // Print mode loads no setting sources (no project settings, hooks or MCP servers) and has no tools,
        // so it may run in the host's project folder; the model then never mistakes the scratch folder for it.
        return new SubscriptionCliProcessRequest(
            executable,
            args,
            prompt,
            GetWorkingDirectory(executionSettings),
            GetTimeout(executionSettings));
    }

}

public sealed class CodexCliLlmProvider : SubscriptionCliLlmProviderBase, ILegacyReasoningSettings
{
    private static readonly ProviderPackDocument Pack =
        ProviderPackCatalog.GetRequired(SubscriptionCliProviders.CodexCli);
    private static readonly SubscriptionCliProviderPack CliPack = Pack.SubscriptionCli!;
    private readonly IOptions<SubscriptionCliProviderOptions> _options;

    /// <summary>Internal seam for small, deterministic output-file overflow tests.</summary>
    internal int OutputFileCaptureLimitChars { get; set; } =
        DefaultSubscriptionCliProcessRunner.DefaultStandardOutputCaptureLimitChars;

    public CodexCliLlmProvider(
        IOptions<SubscriptionCliProviderOptions> options,
        ISubscriptionCliProcessRunner runner,
        ILogger<CodexCliLlmProvider> logger)
        : base(options, runner, logger)
    {
        _options = options;
    }

    public override string ProviderName => SubscriptionCliProviders.CodexCli;
    private protected override SubscriptionCliCatalogExchange? CatalogExchange { get; } = new(
        SubscriptionCliCatalogProtocol.CodexAppServer, ["app-server"], Pack.ModelPrefix);
    public override bool SupportsVision(string model)
    {
        var normalized = NormalizeCliModel(model);
        if (normalized is null) return false; // A moving/default alias is not model capability proof.
        var (exact, _) = SplitModelAndEffort(normalized);
        // Image input is a reviewed fact of the exact foundation model, read
        // from its provider pack, not a list of names compiled into the CLI.
        return CliPack.FoundationProviders.Any(provider =>
            ProviderPackCatalog.TryGetModelFacts(provider, exact, out var facts) && facts?.SupportsVision == true);
    }

    protected override ChatUsage? ReadReportedUsage(SubscriptionCliProcessResult result)
        => CodexCliReportedUsage.Read(result.StandardOutput);

    protected override string CommandName => CliPack.Command;
    protected override SubscriptionCliExecutableOptions ExecutableOptions => _options.Value.CodexCli;
    protected override IReadOnlyList<string> BuiltInModels => CliPack.BuiltInModels;

    // Login-status arguments and the ChatGPT-only session parser live in the
    // shared SubscriptionCliLoginProbe — one implementation for the runtime
    // provider stack and the setup/detection stack.

    protected override SubscriptionCliProcessRequest BuildRequest(
        string executable,
        string prompt,
        string routerModel,
        string? normalizedCliModel,
        Dictionary<string, object>? executionSettings)
    {
        var workDir = GetWorkingDirectory();
        var outputPath = Path.Combine(workDir, $"codex-last-message-{Guid.NewGuid():N}.txt");
        // The reviewed provider pack owns the volatile Codex syntax. Its
        // loader rejects templates that omit the required tool-disable,
        // read-only, no-rules, ephemeral, and never-approve controls.
        var args = CliPack.InferenceArguments
            .Select(argument => argument switch
            {
                "{workingDirectory}" => workDir,
                "{outputPath}" => outputPath,
                _ => argument,
            })
            .ToList();

        // `codex exec`'s default human-readable transcript mirrors the complete
        // stdin prompt to stderr. Long-running chats can therefore overflow the
        // bounded diagnostic capture even though --output-last-message contains
        // a complete answer. JSON event mode suppresses that human transcript;
        // the bounded output file remains the sole response artifact. This is
        // an adapter/capture control (like the stdin `-` sentinel below), not a
        // model-execution setting, so the reviewed provider-pack identity and
        // its existing quality proof remain unchanged.
        args.Add("--json");

        if (!string.IsNullOrWhiteSpace(normalizedCliModel))
        {
            // Discovered tier variants encode a reasoning effort as
            // "<model>@<effort>" (e.g. gpt-5.6-luna@high). The effort is
            // whitelisted before it reaches the TOML config override.
            var (model, _) = SplitModelAndEffort(normalizedCliModel);
            var effort = ResolveReasoningEffort(normalizedCliModel, executionSettings);
            if (effort is not null)
            {
                args.Add("-c");
                args.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    CliPack.ReasoningConfigFormat,
                    effort));
            }

            args.Add(CliPack.ModelArgument);
            args.Add(model);
        }

        if (executionSettings?.GetValueOrDefault(CliImageAttachmentLease.PathsSetting) is IReadOnlyList<string> imagePaths)
            foreach (var imagePath in imagePaths) { args.Add("--image"); args.Add(imagePath); }
        args.Add("-");

        return new SubscriptionCliProcessRequest(
            executable,
            args,
            prompt,
            workDir,
            GetTimeout(executionSettings),
            outputPath);
    }

    void ILegacyReasoningSettings.ApplyReasoning(Dictionary<string, object> settings, string model, LlmReasoningMode reasoning)
    {
        if (reasoning == LlmReasoningMode.Disabled)
        {
            // Background JSON and validation callers share a portable suppression
            // hint. Codex has no reviewed off switch: preserve its default (or
            // the selected @effort tier), as the legacy path did, and disclose
            // the omission instead of rejecting a healthy Scout-admitted route.
            var diagnostics = LlmReservedExecutionSettings.GetExecutionParameterDiagnostics(settings).ToList();
            diagnostics.Add(new("reasoning", LlmExecutionParameterDisposition.Omitted,
                "Codex CLI does not support disabling reasoning; the selected model's default or pinned effort is retained."));
            settings[LlmReservedExecutionSettings.ExecutionParameterDiagnostics] = diagnostics;
            return;
        }

        var effort = reasoning.ToString().ToLowerInvariant();
        if (!ModelRouteCapabilities.ReasoningEfforts(ProviderName, model).Contains(effort, StringComparer.Ordinal))
            throw new NotSupportedException($"The selected Codex CLI route does not support reasoning effort {effort}.");
        settings["reasoning_effort"] = effort;
        // Rejects an effort that conflicts with a model pinned to another tier (model@effort).
        _ = ResolveReasoningEffort(model, settings);
    }

    internal static string? ResolveReasoningEffort(string model, IReadOnlyDictionary<string, object>? executionSettings)
    {
        var pinned = SplitModelAndEffort(model).Effort;
        if (executionSettings?.GetValueOrDefault("reasoning_effort") is not { } raw) return pinned;
        var requested = raw.ToString()?.Trim().ToLowerInvariant();
        // Hidden-reasoning recovery can add this portable hint after translation.
        // Codex has no off switch; retain the admitted model's default or pin.
        if (requested is "none" or "disabled" or "false") return pinned;
        if (requested is null || !ModelRouteCapabilities.ReasoningEfforts(SubscriptionCliProviders.CodexCli, model).Contains(requested, StringComparer.Ordinal))
            throw new NotSupportedException("The selected Codex CLI route does not support the requested reasoning effort.");
        if (pinned is not null && requested != pinned)
            throw new NotSupportedException("The requested reasoning effort conflicts with the selected Codex CLI model tier.");
        return requested;
    }

    public static (string Model, string? Effort) SplitModelAndEffort(string normalizedCliModel)
    {
        var at = normalizedCliModel.LastIndexOf('@');
        if (at <= 0 || at == normalizedCliModel.Length - 1)
        {
            return (normalizedCliModel, null);
        }

        var effort = normalizedCliModel[(at + 1)..].Trim().ToLowerInvariant();
        return CliPack.ReasoningEfforts.Contains(effort, StringComparer.Ordinal)
            ? (normalizedCliModel[..at], effort)
            : (normalizedCliModel, null);
    }

    protected override async Task<string> ExtractContentAsync(
        SubscriptionCliProcessRequest request,
        SubscriptionCliProcessResult result)
    {
        if (!string.IsNullOrWhiteSpace(request.OutputPath) && File.Exists(request.OutputPath))
        {
            try
            {
                var fromFile = await DefaultSubscriptionCliProcessRunner.ReadBoundedFileAsync(
                        request.OutputPath,
                        OutputFileCaptureLimitChars)
                    .ConfigureAwait(false);
                if (fromFile.WasTruncated)
                {
                    throw new SubscriptionCliOutputLimitExceededException();
                }

                if (!string.IsNullOrWhiteSpace(fromFile.Text))
                {
                    return fromFile.Text.Trim();
                }
            }
            finally
            {
                TryDelete(request.OutputPath);
            }
        }

        return result.StandardOutput.Trim();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { }
    }
}

public sealed class GeminiCliLlmProvider : SubscriptionCliLlmProviderBase
{
    private static readonly SubscriptionCliProviderPack CliPack =
        ProviderPackCatalog.GetRequired(SubscriptionCliProviders.GeminiCli).SubscriptionCli!;
    private readonly IOptions<SubscriptionCliProviderOptions> _options;
    private readonly Func<(bool IsAuthenticated, string? Detail)> _subscriptionSessionProbe;

    public GeminiCliLlmProvider(
        IOptions<SubscriptionCliProviderOptions> options,
        ISubscriptionCliProcessRunner runner,
        ILogger<GeminiCliLlmProvider> logger,
        Func<(bool IsAuthenticated, string? Detail)>? subscriptionSessionProbe = null)
        : base(options, runner, logger)
    {
        _options = options;
        _subscriptionSessionProbe = subscriptionSessionProbe
            ?? (() => GeminiCliAuthProbe.Probe());
    }

    public override string ProviderName => SubscriptionCliProviders.GeminiCli;
    protected override string CommandName => CliPack.Command;
    protected override SubscriptionCliExecutableOptions ExecutableOptions => _options.Value.GeminiCli;

    // Gemini CLI has no `auth status`-style command; prove the session from
    // its on-disk auth state (settings.json + cached OAuth credentials).
    protected override (bool IsAuthenticated, string? Detail) ProbeSubscriptionSessionWithoutCli()
        => _subscriptionSessionProbe();
    protected override IReadOnlyList<string> BuiltInModels => CliPack.BuiltInModels;

    protected override SubscriptionCliProcessRequest BuildRequest(
        string executable,
        string prompt,
        string routerModel,
        string? normalizedCliModel,
        Dictionary<string, object>? executionSettings)
    {
        var args = CliPack.InferenceArguments
            .Select(argument => argument == "{prompt}" ? prompt : argument)
            .ToList();

        // Gemini CLI owns the moving model aliases (`pro`, `flash`, `auto`).
        // Passing aliases lets installed CLI versions resolve the right
        // current model instead of us baking stale concrete model IDs.
        var model = string.IsNullOrWhiteSpace(normalizedCliModel) ? null : normalizedCliModel;

        if (!string.IsNullOrWhiteSpace(model))
        {
            args.Add(CliPack.ModelArgument);
            args.Add(model);
        }

        return new SubscriptionCliProcessRequest(
            executable,
            args,
            StandardInput: null,
            WorkingDirectory: GetWorkingDirectory(),
            Timeout: GetTimeout(executionSettings));
    }

}

/// <summary>
/// A background subscription-CLI process was stopped so user-waiting work could
/// take the single Catalyst execution slot. Distinct from a timeout: nothing is
/// wrong with the CLI, the work was simply lower priority and is safe to drop
/// or retry on the next sweep.
/// </summary>
public sealed class SubscriptionCliPreemptedException : Exception
{
    public SubscriptionCliPreemptedException(string message) : base(message)
    {
    }
}
