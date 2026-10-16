using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace SwarmCore.LlmRouting.ToolUse;

/// <summary>
/// Bounded retry for provider HTTP calls. Rate limits (429), request timeouts (408), and
/// server errors (5xx, including Anthropic "overloaded") are retried a few times with
/// jittered exponential backoff, honouring <c>Retry-After</c> when the provider sends one,
/// within a total wait budget so a dead or throttled route still fails over to another lane
/// quickly. Everything else (4xx request errors, auth, content policy) is returned at once so
/// the caller can surface it. A refused connection or a DNS failure is not retried: the
/// route is down, and the orchestrator's lane hand-off is the right recovery.
/// </summary>
public static class ToolUseHttpRetry
{
    public const int DefaultMaxAttempts = 4;

    /// <summary>Upper bound on the time spent waiting between attempts for one call.</summary>
    public static readonly TimeSpan DefaultTotalWaitBudget = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    public static bool IsRetryable(HttpStatusCode status)
        => status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout
           || (int)status >= 500 && (int)status <= 599;

    /// <summary>Transport failures worth a retry: anything except "the route is not there".</summary>
    public static bool IsRetryableTransport(HttpRequestException ex)
        => ex.HttpRequestError is not (HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError);

    /// <summary>
    /// Sends the request built by <paramref name="createRequest"/> (a fresh message per attempt, since
    /// a sent <see cref="HttpRequestMessage"/> cannot be reused). Returns the first non-retryable
    /// response, a successful response, or the last failing response once attempts or the wait
    /// budget are exhausted. Non-retryable transport exceptions propagate.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient http,
        Func<HttpRequestMessage> createRequest,
        HttpCompletionOption completion,
        ILogger logger,
        string providerName,
        CancellationToken ct,
        int maxAttempts = DefaultMaxAttempts,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? totalWaitBudget = null)
    {
        maxAttempts = Math.Max(1, maxAttempts);
        delay ??= Task.Delay;
        var budget = totalWaitBudget ?? DefaultTotalWaitBudget;
        var waited = TimeSpan.Zero;
        HttpResponseMessage? last = null;
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            HttpResponseMessage response;
            try
            {
                using var request = createRequest();
                response = await http.SendAsync(request, completion, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < maxAttempts && IsRetryableTransport(ex) && Fits(waited, Backoff(attempt, null), budget, out var wait))
            {
                logger.LogWarning(ex, "{Provider} call failed to send (attempt {Attempt}/{Max}); retrying in {Delay:0.0}s.", providerName, attempt, maxAttempts, wait.TotalSeconds);
                waited += wait;
                await delay(wait, ct).ConfigureAwait(false);
                continue;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < maxAttempts && Fits(waited, Backoff(attempt, null), budget, out var wait))
            {
                logger.LogWarning("{Provider} call timed out (attempt {Attempt}/{Max}); retrying in {Delay:0.0}s.", providerName, attempt, maxAttempts, wait.TotalSeconds);
                waited += wait;
                await delay(wait, ct).ConfigureAwait(false);
                continue;
            }

            if (response.IsSuccessStatusCode || !IsRetryable(response.StatusCode) || attempt >= maxAttempts)
            {
                last?.Dispose();
                return response;
            }

            var pause = Backoff(attempt, ReadRetryAfter(response));
            if (!Fits(waited, pause, budget, out pause))
            {
                // Waiting longer than the budget would stall the lane; hand the failing response back so the caller fails over.
                last?.Dispose();
                return response;
            }
            logger.LogWarning("{Provider} returned {Status} (attempt {Attempt}/{Max}); retrying in {Delay:0.0}s.", providerName, (int)response.StatusCode, attempt, maxAttempts, pause.TotalSeconds);
            last?.Dispose();
            last = response;
            waited += pause;
            await delay(pause, ct).ConfigureAwait(false);
        }
    }

    private static bool Fits(TimeSpan waited, TimeSpan proposed, TimeSpan budget, out TimeSpan wait)
    {
        wait = proposed;
        return waited + proposed <= budget;
    }

    /// <summary>Exponential backoff with jitter: 1-2s, 2-4s, 4-8s..., capped, or the provider's Retry-After when longer.</summary>
    internal static TimeSpan Backoff(int attempt, TimeSpan? retryAfter)
    {
        var baseSeconds = Math.Min(MaxDelay.TotalSeconds, Math.Pow(2, Math.Max(0, attempt - 1)));
        var jittered = TimeSpan.FromSeconds(baseSeconds * (1 + Random.Shared.NextDouble()));
        if (jittered > MaxDelay) jittered = MaxDelay;
        if (retryAfter is { } ra && ra > TimeSpan.Zero)
            return ra > MaxDelay ? MaxDelay : ra > jittered ? ra : jittered;
        return jittered;
    }

    internal static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null)
        {
            // Some providers send a milliseconds value in a custom header.
            if (response.Headers.TryGetValues("retry-after-ms", out var ms) && double.TryParse(ms.FirstOrDefault(), out var millis) && millis > 0)
                return TimeSpan.FromMilliseconds(millis);
            return null;
        }
        if (header.Delta is { } delta) return delta;
        if (header.Date is { } date)
        {
            var until = date - DateTimeOffset.UtcNow;
            return until > TimeSpan.Zero ? until : null;
        }
        return null;
    }
}
