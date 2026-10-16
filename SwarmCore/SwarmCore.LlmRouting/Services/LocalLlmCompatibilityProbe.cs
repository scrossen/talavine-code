using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SwarmCore.Abstractions.Json;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.LlmRouting.Configuration;

namespace SwarmCore.LlmRouting.Services;

public sealed class LocalLlmCompatibilityProbe : ILocalLlmCompatibilityProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan DefaultRuntimeAdmissionTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan MaximumRuntimeAdmissionTimeout = TimeSpan.FromMinutes(15);

    private readonly ILocalLlmRuntimeManager _localRuntime;
    private readonly ILogger<LocalLlmCompatibilityProbe> _logger;
    private readonly ILlmProviderRegistry? _providerRegistry;
    private readonly IServiceScopeFactory? _auditScopes;

    public LocalLlmCompatibilityProbe(
        IHttpClientFactory httpClientFactory,
        ILocalLlmRuntimeManager localRuntime,
        IOptions<OllamaProviderOptions> ollamaOptions,
        ILogger<LocalLlmCompatibilityProbe> logger,
        ILlmProviderRegistry? providerRegistry = null,
        IServiceScopeFactory? auditScopes = null)
    {
        _ = httpClientFactory; // Retained constructor compatibility; provider adapters own transport.
        _localRuntime = localRuntime;
        _ = ollamaOptions; // Retained constructor compatibility; provider registry owns endpoint selection.
        _logger = logger;
        _providerRegistry = providerRegistry;
        _auditScopes = auditScopes;
    }

    public async Task<LocalLlmCompatibilityProbeResult> ProbeJsonContractAsync(
        LocalLlmCompatibilityProbeRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settings = NormalizeJsonContractSettings(request.ExecutionSettings, request.TimeoutSeconds, request.KeepAlive);
        var result = new LocalLlmCompatibilityProbeResult
        {
            Provider = request.Provider,
            Model = request.Model,
            Attempt = string.IsNullOrWhiteSpace(request.Attempt) ? "json-contract" : request.Attempt,
            ExecutionContractId = "",
            ExecutionSettings = settings,
        };

        using var runtimeSession = await AcquireRuntimeSessionAsync(request, ct).ConfigureAwait(false);
        using var runtimeActivation = runtimeSession.Activate();
        return await ProbeRegisteredProviderAsync(
                request,
                result,
                runtimeSession.PreemptionToken,
                ct)
            .ConfigureAwait(false);
    }

    private async Task<LocalLlmCompatibilityProbeResult> ProbeRegisteredProviderAsync(
        LocalLlmCompatibilityProbeRequest request,
        LocalLlmCompatibilityProbeResult result,
        CancellationToken runtimePreemption,
        CancellationToken ct)
    {
        var provider = _providerRegistry?.GetProvider(request.Provider);
        if (provider is null)
        {
            result.Status = "unsupported_provider";
            result.Issue = $"No registered provider adapter is available for {request.Provider}.";
            return result;
        }

        if (provider is not ILlmProviderExecutionContract contract)
        {
            result.Status = "unsupported_contract";
            result.Issue = $"{request.Provider} has no typed execution contract.";
            return result;
        }

        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct, runtimePreemption);
        probeCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 1, 900)));
        LlmProviderExecutionTranslation translation;
        try
        {
            var generationParameters = contract.ExecutionContract.Capabilities.Reasoning
                ? request.GenerationParameters
                : request.GenerationParameters with { Reasoning = LlmReasoningMode.ProviderDefault };
            translation = contract.TranslateExecution(
                request.Model,
                generationParameters,
                new LlmRequestControl
                {
                    CancellationToken = probeCts.Token,
                    RequireExactModel = true,
                    ExpectedArtifactDigest = request.ExpectedArtifactDigest,
                    PrivacyClass = LlmPrivacyClass.LocalOnly,
                    Interactivity = LlmInteractivity.Background,
                },
                request.ExecutionSettings);
        }
        catch (LlmExecutionParameterRejectedException ex)
        {
            result.Status = "contract_rejected";
            result.ParameterDiagnostics.AddRange(ex.Diagnostics);
            result.Issue = ex.Message;
            return result;
        }

        result.ExecutionContractId = contract.ExecutionContract.ExecutionContractId;
        result.ParameterDiagnostics.AddRange(translation.Diagnostics);
        var sw = Stopwatch.StartNew();
        try
        {
            using var auditScope = _auditScopes?.CreateScope();
            var response = await ModelScoutExecutionAdmission.ExecuteLocalResearchAsync(
                auditScope?.ServiceProvider.GetService<ILlmRoutingAuditStore>(), "local-json-calibration", request.Provider, request.Model,
                TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 1, 900)), probeCts.Token,
                token => provider.GenerateResponseAsync(request.ExpectedJsonPrompt, request.Model,
                    new Dictionary<string, object>(translation.WireSettings, StringComparer.OrdinalIgnoreCase)
                    {
                        [LlmReservedExecutionSettings.RequestCancellationToken] = token,
                    }).WaitAsync(token)).ConfigureAwait(false);
            sw.Stop();
            result.LatencyMs = sw.Elapsed.TotalMilliseconds;
            result.OutputTokens = response.OutputTokens;
            result.ResponsePreview = Preview(response.Content, 400);
            result.Compatible = response.WasSuccessful && IsValidJsonObject(response.Content, out _);
            result.Status = result.Compatible ? "compatible" : "incompatible";
            result.Issue = result.Compatible ? null : response.ErrorMessage ?? "Provider response failed the JSON contract.";
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (runtimePreemption.IsCancellationRequested)
        {
            throw new LocalLlmRuntimePreemptedException(
                $"Local compatibility probing for {request.Provider}/{request.Model} yielded to a user-waiting request.",
                runtimePreemption);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            result.LatencyMs = sw.Elapsed.TotalMilliseconds;
            result.Status = "timed_out";
            result.Issue = $"compatibility probe exceeded {request.TimeoutSeconds} seconds";
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            result.LatencyMs = sw.Elapsed.TotalMilliseconds;
            result.Status = "runtime_failed";
            result.Issue = ex.Message;
            return result;
        }
    }

    private async Task<ILocalLlmRuntimeExclusiveSession> AcquireRuntimeSessionAsync(
        LocalLlmCompatibilityProbeRequest request,
        CancellationToken ct)
    {
        var admissionTimeout = NormalizeRuntimeAdmissionTimeout(request.RuntimeAdmissionTimeout);
        var runtimeRequest = new LocalLlmRuntimeRequest(
            request.Provider,
            request.Model,
            new LlmCallContext(LlmInteractivity.Background, LlmPrivacyClass.LocalOnly),
            LocalLlmWorkloadKind.Text);
        var activity = new LocalLlmRuntimeActivity(
            request.Provider,
            request.Model,
            LocalLlmWorkloadKind.Text,
            LocalLlmRuntimeActivityKind.Calibration,
            "Checking local JSON compatibility",
            DateTimeOffset.UtcNow);
        using var admissionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        admissionCts.CancelAfter(admissionTimeout);
        try
        {
            return await _localRuntime.AcquireExclusiveActivityAsync(
                    runtimeRequest,
                    activity,
                    admissionCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested
                                                  && admissionCts.IsCancellationRequested)
        {
            throw new LocalLlmRuntimePreemptedException(
                $"Local AI remained busy for {admissionTimeout.TotalMinutes:0.#} minutes; compatibility probing was deferred.",
                admissionCts.Token);
        }
    }

    private static TimeSpan NormalizeRuntimeAdmissionTimeout(TimeSpan? configured)
    {
        if (configured is null || configured <= TimeSpan.Zero)
        {
            return DefaultRuntimeAdmissionTimeout;
        }

        return configured > MaximumRuntimeAdmissionTimeout
            ? MaximumRuntimeAdmissionTimeout
            : configured.Value;
    }

    public static Dictionary<string, object> NormalizeJsonContractSettings(
        IReadOnlyDictionary<string, object>? settings,
        int timeoutSeconds,
        string keepAlive = "30m")
    {
        var normalized = settings is null
            ? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object>(settings, StringComparer.OrdinalIgnoreCase);
        var boundedTimeout = Math.Clamp(timeoutSeconds, 30, 3600);
        normalized["ollama_headers_timeout_seconds"] = boundedTimeout;
        normalized["ollama_queue_wait_seconds"] = boundedTimeout;
        return normalized;
    }

    public static bool IsTransientRuntimeIssue(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("empty_response", StringComparison.OrdinalIgnoreCase)
               || message.Contains("thinking_channel_without_content", StringComparison.OrdinalIgnoreCase)
               || message.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
               || message.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
               || message.Contains("connection aborted", StringComparison.OrdinalIgnoreCase)
               || message.Contains("temporarily unavailable", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Ollama is not reachable", StringComparison.OrdinalIgnoreCase)
               || message.Contains("No fallback route configured", StringComparison.OrdinalIgnoreCase)
                  && message.Contains("reason=empty_response", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidJsonObject(string content, out string issue)
    {
        if (JsonRepair.TryNormalize(content, expectArray: false, out _))
        {
            issue = "";
            return true;
        }

        issue = "invalid_json_response: response could not be normalized to a JSON object";
        return false;
    }

    private static string Preview(string value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxChars
            ? trimmed
            : trimmed[..maxChars] + "...";
    }

}
