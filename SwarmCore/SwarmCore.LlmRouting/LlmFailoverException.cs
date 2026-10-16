using System;
using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting;

/// <summary>
/// One bounded physical route considered by an automatic failover session. The
/// execution channel deliberately keeps direct API and subscription CLI capacity
/// separate even when both routes belong to the same model vendor.
/// </summary>
public sealed record LlmFailoverAttempt(
    string Provider,
    string Model,
    string ExecutionChannel,
    LlmFailureCategory? Category,
    string Error,
    bool WasExecuted);

/// <summary>
/// Thrown when an LLM request fails on both the primary and fallback provider/model.
/// Intended to be user-visible (via upstream error surfaces) while still carrying the inner exceptions.
/// </summary>
public sealed class LlmFailoverException : Exception
{
    public string FunctionType { get; }
    public string PromptId { get; }
    public LlmModelSelection Primary { get; }
    public LlmModelSelection Fallback { get; }
    public string PrimaryError { get; }
    public string FallbackError { get; }

    /// <summary>
    /// Typed classification of the primary failure, when the thrower knew it.
    /// Null when the exception predates typed categories or the reason didn't
    /// map to a known category — consumers must then fall back to parsing
    /// <see cref="PrimaryError"/> (which embeds the legacy "(reason)" marker).
    /// </summary>
    public LlmFailureCategory? PrimaryCategory { get; }

    /// <summary>
    /// Typed classification of the fallback failure, when the thrower knew it.
    /// Same null semantics as <see cref="PrimaryCategory"/>, with
    /// <see cref="FallbackError"/> as the string fallback.
    /// </summary>
    public LlmFailureCategory? FallbackCategory { get; }

    /// <summary>
    /// Ordered bounded attempt detail when the router used a proof-bound survival
    /// set. Legacy two-route throwers leave this empty; the compatibility
    /// Primary/Fallback properties above remain authoritative for old consumers.
    /// </summary>
    public IReadOnlyList<LlmFailoverAttempt> Attempts { get; }

    public LlmFailoverException(
        string functionType,
        string promptId,
        LlmModelSelection primary,
        LlmModelSelection fallback,
        string primaryError,
        string fallbackError,
        Exception? primaryException = null,
        Exception? fallbackException = null,
        LlmFailureCategory? primaryCategory = null,
        LlmFailureCategory? fallbackCategory = null,
        IReadOnlyList<LlmFailoverAttempt>? attempts = null)
        : base(BuildMessage(functionType, promptId, primary, fallback, primaryError, fallbackError), primaryException)
    {
        FunctionType = functionType;
        PromptId = promptId;
        Primary = primary;
        Fallback = fallback;
        PrimaryError = primaryError;
        FallbackError = fallbackError;
        PrimaryCategory = primaryCategory;
        FallbackCategory = fallbackCategory;
        Attempts = attempts?.ToArray() ?? [];

        // Preserve the fallback exception as additional context when present.
        // Exception.Data travels into logs and user-visible error surfaces, so
        // the provider message is redacted the same way durable diagnostics are.
        if (fallbackException is not null)
        {
            Data["FallbackExceptionType"] = fallbackException.GetType().FullName ?? "";
            Data["FallbackExceptionMessage"] =
                RoutingDiagnosticSanitizer.Sanitize(fallbackException.Message, 2_000) ?? string.Empty;
        }
    }

    private static string BuildMessage(
        string functionType,
        string promptId,
        LlmModelSelection primary,
        LlmModelSelection fallback,
        string primaryError,
        string fallbackError)
    {
        return
            $"LLM routing failed for function '{functionType}' (request {promptId}). " +
            $"Primary {primary.Provider}/{primary.Model} failed: {primaryError}. " +
            $"Fallback {fallback.Provider}/{fallback.Model} failed: {fallbackError}.";
    }
}

