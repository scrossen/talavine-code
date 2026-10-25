using System.Text.RegularExpressions;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Services;

// LlmFailureCategory itself lives in SwarmCore.Abstractions.LlmRouting so the
// typed category can travel with cooldown records, failover exceptions, and UI
// presenters without a dependency on this routing assembly.

/// <summary>
/// What to do about a failure of a given category. LiteLLM-style: the error
/// class decides whether the provider is penalized at all, how long it cools
/// down, and whether a human should hear about it — a content-policy refusal
/// is the request's fault and must never sideline a healthy provider, while a
/// dead credential should be paused for hours and surfaced, not silently
/// retried every few minutes.
/// </summary>
/// <param name="Category">The classified failure category.</param>
/// <param name="LegacyReason">Wire/log-compatible failure reason string used by
/// existing audit rows and consumers ("rate_limit", "timeout", ...).</param>
/// <param name="FailoverEligible">Whether trying a different route for this
/// request is likely to help.</param>
/// <param name="PenalizeProvider">Whether this failure is evidence against the
/// provider (vs the request or the model id).</param>
/// <param name="ProviderCooldown">How long to keep the provider out of
/// rotation; null means never cool down for this category.</param>
/// <param name="SurfaceToUser">Whether this failure needs human attention
/// (dead credential, exhausted paid quota) rather than silent retries.</param>
public sealed record LlmFailurePolicy(
    LlmFailureCategory Category,
    string LegacyReason,
    bool FailoverEligible,
    bool PenalizeProvider,
    TimeSpan? ProviderCooldown,
    bool SurfaceToUser);

/// <summary>
/// Central error-classification and per-category failure policy for LLM calls.
/// The category split matters most where the old string-sniffing conflated
/// states: quota/credit exhaustion is not a transient rate limit, and a
/// content-policy or invalid-request failure is not provider trouble.
/// </summary>
public static class LlmErrorPolicy
{
    public static readonly TimeSpan QuotaExhaustedCooldown = TimeSpan.FromHours(6);
    public static readonly TimeSpan AuthInvalidCooldown = TimeSpan.FromHours(2);
    public static readonly TimeSpan SubscriptionCliRuntimeFailureCooldown = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan TransientRuntimeFailureCooldown = TimeSpan.FromMinutes(5);

    // Status codes are only trusted when they appear as a whole token next to
    // a status-shaped word ("HTTP 429", "status code 404", "error: 401",
    // "call failed: 429") or at the very start of the message ("429 Too Many
    // Requests"). A bare digit run is never a status: OpenAI's context-length
    // error says "resulted in 130429 tokens" and Anthropic bodies carry
    // "request_id: req_0142901" — neither is a 429.
    private static readonly Regex AnchoredStatusCode = new(
        @"(?:^\s*|\b(?:status|http|error|code|failed|returned|response)\b\D{0,5})(?<code>\d{3})(?![\d.])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private static readonly string[] QuotaMarkers =
    [
        "insufficient_quota",
        "exceeded your current quota",
        "credit balance",
        "billing",
        "requestsperday",
        "per day",
        "perday",
        "daily quota",
        // Prepaid-balance exhaustion. HTTP 402's reason phrase, OpenRouter's
        // "requires more credits ... can only afford N" and DeepSeek's
        // "Insufficient Balance" all mean the account cannot pay for the
        // request; retrying the same call cannot succeed until someone adds
        // credit.
        "payment required",
        "payment_required",
        "insufficient credit",
        "insufficient_credit",
        "insufficient balance",
        "insufficient_balance",
        "more credits",
        "can only afford",
    ];

    private static readonly string[] ContentPolicyMarkers =
    [
        "content_policy",
        "content policy",
        "content_filter",
        "safety settings",
        "blocked by safety",
        "prohibited content",
        "policy violation",
    ];

    // The request did not fit the route's context window. Providers commonly
    // return these as HTTP 400 with token counts in the body (digit runs that
    // used to be mistaken for a status code). Ollama does not reject an
    // oversized prompt: it keeps the system messages plus the newest messages
    // that fit num_ctx and drops the rest. When that drops the user's
    // question, the Qwen-family chat renderer fails the request with HTTP 500
    // "no user query found in messages" — a message the host never sends
    // without a user turn, so it is the truncation symptom of an overflow.
    // Newer Ollama engines reject instead, with HTTP 400 "request (N tokens)
    // exceeds the available context size (M tokens)" (exceed_context_size_error).
    private static readonly string[] ContextOverflowMarkers =
    [
        "context length",
        "context_length_exceeded",
        "context window",
        "maximum context",
        "too many tokens",
        "prompt is too long",
        "no user query found in messages",
        "exceed_context_size_error",
        "exceeds the available context size",
    ];

    // Request-shape failures that providers commonly return as HTTP 400.
    private static readonly string[] InvalidRequestMarkers =
    [
        "invalid_request_error",
        "invalid request",
        "bad request",
        "badrequest",
    ];

    private static readonly string[] ModelNotFoundMarkers =
    [
        "model_not_found",
        "does not exist",
        "not_found",
        "notfound",
        "not found",
        "not supported for generatecontent",
        "no longer compatible",
        "incompatible",
    ];

    /// <summary>
    /// Extracts a structured HTTP status from an exception chain when a provider
    /// transport supplied one (<see cref="HttpRequestException.StatusCode"/>).
    /// </summary>
    public static int? TryGetHttpStatusCode(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: { } status })
            {
                return (int)status;
            }
        }

        return null;
    }

    /// <summary>
    /// Classifies a thrown provider failure, preferring the transport's
    /// structured HTTP status over the message text when one is available.
    /// </summary>
    public static LlmFailureCategory Classify(Exception? exception)
        => exception is LlmProviderHttpException typed ? typed.Category : exception is null
            ? LlmFailureCategory.ProviderError
            : Classify(exception.Message, TryGetHttpStatusCode(exception));

    public static LlmFailureCategory Classify(string? errorMessage)
        => Classify(errorMessage, httpStatusCode: null);

    /// <summary>
    /// Classifies a provider failure. When <paramref name="httpStatusCode"/> is
    /// supplied it decides the coarse category (the message text only refines
    /// it, e.g. a 429 whose body reports exhausted credit is QuotaExhausted);
    /// otherwise the message text is classified with status codes matched only
    /// as anchored whole tokens, never as bare digit runs.
    /// </summary>
    public static LlmFailureCategory Classify(string? errorMessage, int? httpStatusCode)
    {
        var lower = string.IsNullOrWhiteSpace(errorMessage)
            ? string.Empty
            : errorMessage.ToLowerInvariant();

        if (httpStatusCode is { } structuredStatus
            && ClassifyByStatus(structuredStatus, lower) is { } byStatus)
        {
            return byStatus;
        }

        if (lower.Length == 0)
        {
            return LlmFailureCategory.ProviderError;
        }

        // Our own tool-use providers' reply-quality failures first. These are
        // stable strings we authored (SubscriptionCliToolUseProvider); they
        // mean the provider responded but the reply could not be used, which
        // is evidence against the REPLY, not the provider.
        if (ContainsAny(lower,
                "malformed tool-use",
                "tool call expressed as text",
                "tool-call debris",
                "malformed tool-use json",
                "empty completion",
                "no required tool call",
                "empty response with no tool calls",
                "without a messagestoppedevent"))
        {
            return LlmFailureCategory.MalformedReply;
        }

        // Our own subscription-CLI providers' co-run decline (stable authored
        // string in SubscriptionCliLlmProviderBase): the launch was refused
        // before the CLI ran because another Talavine process held the
        // slot. This must never read as provider breakage.
        if (lower.Contains("cli launch was declined", StringComparison.Ordinal))
        {
            return LlmFailureCategory.LaunchDeclined;
        }

        // Round-trip our own persisted cooldown reasons whose original provider
        // message was null: RecordFailedResponseCooldownAsync stores
        // "<legacy_reason>: <Category> failure" (e.g. "provider_error:
        // AuthInvalid failure"). Re-classifying that string must recover the
        // real category — mapping it back to ProviderError made the auth latch
        // skip a genuinely dead key when it re-parsed a persisted reason.
        //
        // NOTE: new cooldown rows also persist the category TYPED
        // (LlmProviderCooldown.Category), which typed-first consumers read
        // before ever string-parsing. These string round-trip hacks stay only
        // for legacy rows written before that column existed.
        if (lower.Contains("authinvalid failure", StringComparison.Ordinal))
        {
            return LlmFailureCategory.AuthInvalid;
        }

        if (lower.Contains("quotaexhausted failure", StringComparison.Ordinal))
        {
            return LlmFailureCategory.QuotaExhausted;
        }

        // Quota/credit exhaustion first: these messages usually also contain
        // 429/TooManyRequests markers, and misreading them as a transient rate
        // limit is exactly the bug this table exists to prevent.
        if (IsQuotaExhausted(lower))
        {
            return LlmFailureCategory.QuotaExhausted;
        }

        // Content-policy and request-shape failures are the request's fault.
        // They are checked BEFORE any status-code match because their bodies
        // routinely embed token counts ("resulted in 130429 tokens", "40404
        // tokens") that a substring scan for "429"/"404" misread as a rate
        // limit (60-minute provider cooldown) or a missing model (self-heal
        // rewriting the preference).
        if (ContainsAny(lower, ContentPolicyMarkers))
        {
            return LlmFailureCategory.ContentPolicy;
        }

        if (ContainsAny(lower, ContextOverflowMarkers))
        {
            return LlmFailureCategory.ContextOverflow;
        }

        if (ContainsAny(lower, InvalidRequestMarkers))
        {
            return LlmFailureCategory.InvalidRequest;
        }

        var anchoredStatus = TryExtractAnchoredStatusCode(errorMessage!);

        if (anchoredStatus == 402)
        {
            return LlmFailureCategory.QuotaExhausted;
        }

        if (ContainsAny(lower,
                "rate limit",
                "rate_limit",
                "ratelimit",
                "too many requests",
                "toomanyrequests",
                "resource_exhausted",
                "usage limit",
                "usage_limit",
                "quota")
            || anchoredStatus == 429)
        {
            return LlmFailureCategory.RateLimit;
        }

        if (ContainsAny(lower,
                "invalid api key",
                "invalid x-api-key",
                "invalid_api_key",
                "incorrect api key",
                "unauthorized",
                "authentication_error",
                "authentication failed",
                "api key not valid",
                "key has been revoked",
                // Stable subscription-CLI probe outcomes. These are an
                // authentication boundary, not a generic provider/runtime
                // failure and certainly not evidence of exhausted usage.
                "no authenticated subscription session is ready",
                "no cached subscription oauth credentials",
                "do not contain a subscription oauth session",
                "does not have a verified subscription session",
                "cached oauth credential is missing",
                "cached oauth credentials are expired or invalid",
                "oauth credentials do not grant claude code subscription sessions",
                "credentials could not be read. run `claude` to repair the subscription sign-in",
                // Claude Code's run-time refusal once its refresh token is no
                // longer accepted ("Failed to authenticate: OAuth session
                // expired and could not be refreshed"). Its transient sibling,
                // "Failed to refresh OAuth token: another Claude Code process
                // is refreshing it", is deliberately not matched.
                "failed to authenticate",
                "oauth session expired")
            || anchoredStatus is 401 or 403)
        {
            return LlmFailureCategory.AuthInvalid;
        }

        if (ContainsAny(lower, ModelNotFoundMarkers) || anchoredStatus == 404)
        {
            return LlmFailureCategory.ModelNotFound;
        }

        if (anchoredStatus is 400 or 413 or 422)
        {
            return LlmFailureCategory.InvalidRequest;
        }

        if (ContainsAny(lower,
                "timeout",
                "timed out",
                "temporarily unavailable",
                "overloaded",
                "network",
                "connection")
            || anchoredStatus is 408 or 502 or 503 or 504 or 529)
        {
            return LlmFailureCategory.Timeout;
        }

        return LlmFailureCategory.ProviderError;
    }

    /// <summary>
    /// Structured-status classification. The status decides the coarse bucket;
    /// the (lower-cased) body only refines it where providers overload a status
    /// (Anthropic reports exhausted credit as HTTP 400, OpenAI reports it as a
    /// 429 with an insufficient_quota body). Returns null for statuses whose
    /// meaning is not stable across providers (e.g. a bare 500), which fall
    /// through to the message-text path.
    /// </summary>
    private static LlmFailureCategory? ClassifyByStatus(int status, string lower)
        => status switch
        {
            429 => IsQuotaExhausted(lower)
                ? LlmFailureCategory.QuotaExhausted
                : LlmFailureCategory.RateLimit,
            401 => LlmFailureCategory.AuthInvalid,
            // Payment Required: the account's balance cannot cover the request.
            402 => LlmFailureCategory.QuotaExhausted,
            403 => ContainsAny(lower, ContentPolicyMarkers)
                ? LlmFailureCategory.ContentPolicy
                : LlmFailureCategory.AuthInvalid,
            404 => LlmFailureCategory.ModelNotFound,
            400 or 413 or 422 => IsQuotaExhausted(lower)
                ? LlmFailureCategory.QuotaExhausted
                : ContainsAny(lower, ContentPolicyMarkers)
                    ? LlmFailureCategory.ContentPolicy
                    : ContainsAny(lower, "model_not_found", "does not exist", "not supported for generatecontent")
                        ? LlmFailureCategory.ModelNotFound
                        : ContainsAny(lower, ContextOverflowMarkers)
                            ? LlmFailureCategory.ContextOverflow
                            : LlmFailureCategory.InvalidRequest,
            408 or 502 or 503 or 504 or 529 => LlmFailureCategory.Timeout,
            _ => null,
        };

    private static bool IsQuotaExhausted(string lower)
        => ContainsAny(lower, QuotaMarkers)
           || (lower.Contains("quota", StringComparison.Ordinal)
               && ContainsAny(lower, "exceed", "exhaust", "daily"));

    private static int? TryExtractAnchoredStatusCode(string message)
    {
        try
        {
            var match = AnchoredStatusCode.Match(message);
            return match.Success
                   && int.TryParse(match.Groups["code"].Value, out var code)
                   && code is >= 400 and <= 599
                ? code
                : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// Maps an already-computed legacy reason string back to a category when no
    /// error message text is available for direct classification.
    /// </summary>
    public static LlmFailureCategory ClassifyLegacyReason(string? reason)
        => reason?.Trim().ToLowerInvariant() switch
        {
            "rate_limit" => LlmFailureCategory.RateLimit,
            "timeout" => LlmFailureCategory.Timeout,
            "model_not_found" => LlmFailureCategory.ModelNotFound,
            "launch_declined" => LlmFailureCategory.LaunchDeclined,
            _ => LlmFailureCategory.ProviderError,
        };

    public static LlmFailurePolicy Resolve(
        LlmFailureCategory category,
        bool isSubscriptionCli,
        string? errorMessage = null)
        => Resolve(category, isSubscriptionCli, errorMessage, DateTimeOffset.UtcNow);

    /// <summary>Clock-explicit form: date and wall-clock retry hints are measured from <paramref name="utcNow"/>.</summary>
    internal static LlmFailurePolicy Resolve(
        LlmFailureCategory category,
        bool isSubscriptionCli,
        string? errorMessage,
        DateTimeOffset utcNow)
        => category switch
        {
            LlmFailureCategory.RateLimit => new LlmFailurePolicy(
                category,
                "rate_limit",
                FailoverEligible: true,
                PenalizeProvider: true,
                ProviderCooldown: RateLimitRetryAfterParser.ParseOrDefault(errorMessage, utcNow),
                SurfaceToUser: false),
            LlmFailureCategory.QuotaExhausted => new LlmFailurePolicy(
                category,
                // Wire-compatible with existing consumers that only know
                // "rate_limit"; the policy object carries the real category.
                "rate_limit",
                FailoverEligible: true,
                PenalizeProvider: true,
                ProviderCooldown: Max(
                    QuotaExhaustedCooldown,
                    RateLimitRetryAfterParser.TryParse(errorMessage, utcNow)),
                SurfaceToUser: true),
            LlmFailureCategory.AuthInvalid => new LlmFailurePolicy(
                category,
                "provider_error",
                FailoverEligible: true,
                PenalizeProvider: true,
                ProviderCooldown: AuthInvalidCooldown,
                SurfaceToUser: true),
            LlmFailureCategory.ContentPolicy => new LlmFailurePolicy(
                category,
                "provider_error",
                FailoverEligible: true,
                PenalizeProvider: false,
                ProviderCooldown: null,
                SurfaceToUser: false),
            LlmFailureCategory.InvalidRequest => new LlmFailurePolicy(
                category,
                "provider_error",
                FailoverEligible: true,
                PenalizeProvider: false,
                ProviderCooldown: null,
                SurfaceToUser: false),
            // The request's size against this route's window. Cooling the
            // provider down for it sidelined a whole LAN node (every model on
            // it) because one prompt outgrew one model's num_ctx.
            LlmFailureCategory.ContextOverflow => new LlmFailurePolicy(
                category,
                "provider_error",
                FailoverEligible: true,
                PenalizeProvider: false,
                ProviderCooldown: null,
                SurfaceToUser: false),
            LlmFailureCategory.ModelNotFound => new LlmFailurePolicy(
                category,
                "model_not_found",
                FailoverEligible: true,
                PenalizeProvider: false,
                ProviderCooldown: null,
                SurfaceToUser: false),
            LlmFailureCategory.MalformedReply => new LlmFailurePolicy(
                category,
                // Wire-compatible with consumers that only know provider_error;
                // the policy object carries the real category.
                "provider_error",
                FailoverEligible: true,
                PenalizeProvider: false,
                ProviderCooldown: null,
                SurfaceToUser: false),
            LlmFailureCategory.LaunchDeclined => new LlmFailurePolicy(
                category,
                // Distinct wire reason so audit rows can tell a co-run decline
                // from real provider trouble; consumers that only branch on the
                // known reasons treat it like any other non-retryable-exhaustion
                // failure and simply fall back.
                "launch_declined",
                FailoverEligible: true,
                PenalizeProvider: false,
                ProviderCooldown: null,
                SurfaceToUser: false),
            LlmFailureCategory.Timeout => new LlmFailurePolicy(
                category,
                "timeout",
                FailoverEligible: true,
                PenalizeProvider: true,
                ProviderCooldown: isSubscriptionCli
                    ? SubscriptionCliRuntimeFailureCooldown
                    : TransientRuntimeFailureCooldown,
                SurfaceToUser: false),
            _ => new LlmFailurePolicy(
                LlmFailureCategory.ProviderError,
                "provider_error",
                FailoverEligible: true,
                // Historic behavior: a bare provider error cools down only
                // subscription CLIs (their failures tend to persist for a
                // process lifetime, e.g. a broken flag after a CLI upgrade).
                PenalizeProvider: isSubscriptionCli,
                ProviderCooldown: isSubscriptionCli
                    ? SubscriptionCliRuntimeFailureCooldown
                    : null,
                SurfaceToUser: false),
        };

    private static TimeSpan Max(TimeSpan baseline, TimeSpan? candidate)
        => candidate is { } value && value > baseline ? value : baseline;

    private static bool ContainsAny(string haystack, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (haystack.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
