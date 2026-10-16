using SwarmCore.LlmRouting.Providers;

namespace SwarmCore.LlmRouting.Services;

public enum SubscriptionCliExecutableReadinessStatus
{
    Ready,
    ExecutionFailed,
    InfrastructureBlocked
}

public sealed record SubscriptionCliExecutableReadinessResult(
    SubscriptionCliExecutableReadinessStatus Status,
    string? Executable = null,
    bool DeadlineExpired = false);

/// <summary>
/// Local authentication metadata proves a session, not that an installed shim
/// can launch. Only a bounded, complete version response selects a candidate.
/// The sweep contains no model request and never exposes process diagnostics.
/// </summary>
public static class SubscriptionCliExecutableReadiness
{
    public static async Task<SubscriptionCliExecutableReadinessResult> ProbeAsync(
        ISubscriptionCliProcessRunner runner,
        string providerName,
        IReadOnlyList<string> candidates,
        string workingDirectory,
        TimeSpan timeout,
        SubscriptionCliProcessPriority priority,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await RunBoundedAsync(async token =>
            {
                var infrastructureBlocked = false;
                foreach (var executable in candidates)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var result = await runner.RunAsync(new SubscriptionCliProcessRequest(
                            executable, ["--version"], StandardInput: null,
                            WorkingDirectory: workingDirectory, Timeout: timeout,
                            ProviderName: providerName,
                            Operation: SubscriptionCliProcessOperation.ExecutableReadiness,
                            Priority: priority), token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if (result.OutputWasTruncated)
                        {
                            infrastructureBlocked = true;
                        }
                        else if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput))
                        {
                            return new SubscriptionCliExecutableReadinessResult(
                                SubscriptionCliExecutableReadinessStatus.Ready, executable);
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception failure) when (failure is SubscriptionCliSafeStopException
                        or SubscriptionCliPreemptedException or TimeoutException)
                    {
                        infrastructureBlocked = true;
                    }
                    catch (Exception)
                    {
                        // An unlaunchable candidate is not a login verdict. Never
                        // retain exceptions which can contain paths/account data.
                    }
                }
                token.ThrowIfCancellationRequested();
                return new SubscriptionCliExecutableReadinessResult(infrastructureBlocked
                    ? SubscriptionCliExecutableReadinessStatus.InfrastructureBlocked
                    : SubscriptionCliExecutableReadinessStatus.ExecutionFailed);
            }, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SubscriptionCliExecutableReadinessResult(
                SubscriptionCliExecutableReadinessStatus.InfrastructureBlocked,
                DeadlineExpired: true);
        }
    }

    internal static async Task<T> RunBoundedAsync<T>(
        Func<CancellationToken, Task<T>> evaluate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        // Offload the whole invocation, including a noncooperative synchronous
        // prefix. Late work returns evidence only; caller publishes any cache.
        var evaluation = Task.Run(() =>
        {
            // The queued invocation can start after a sub-millisecond slice has
            // expired. Do not enter an eager runner with an expired token.
            deadline.Token.ThrowIfCancellationRequested();
            return evaluate(deadline.Token);
        }, CancellationToken.None);
        try
        {
            var result = await evaluation.WaitAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            if (evaluation.IsCompleted)
            {
                _ = evaluation.Exception;
                deadline.Dispose();
            }
            else
            {
                // Noncooperative runners can finish late. Observe their faults
                // and retain the linked token source until their work exits.
                _ = evaluation.ContinueWith(completed =>
                {
                    _ = completed.Exception;
                    deadline.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }
}
