using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Abstractions.Serialization;
using SwarmCore.LlmRouting;
using SwarmCore.LlmRouting.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SwarmCore.LlmRouting.Providers;

/// <summary>
/// Ollama LLM provider for local inference.
/// Supports chat completions, vision, tool use, and embeddings via Ollama's native API.
/// Does not require an API key - just needs Ollama running locally.
/// </summary>
public partial class OllamaLlmProvider : ILlmProvider, IOllamaModelWarmup, ILlmProviderRuntimeReadiness, ILlmProviderExecutionContract, IToolUseProviderSource
{
    private readonly OllamaExecutionContract _executionContract = new();
    private readonly ILogger<OllamaLlmProvider> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OllamaProviderOptions _options;
    private readonly ILocalLlmRuntimeManager? _localRuntimeManager;
    private readonly List<OllamaNodeState> _nodes;
    private List<string>? _cachedModels;
    private string? _detectedDefaultModel;
    private string? _detectedEmbeddingModel;
    private DateTime _lastModelCheck = DateTime.MinValue;
    private readonly TimeSpan _modelCacheDuration = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _embeddingModelInstallLock = new(1, 1);
    private readonly ConcurrentDictionary<string, ArtifactDigestCacheEntry> _artifactDigestCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _visionCapabilityCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _cpuGrammarRecoveryUntil =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CpuGrammarRecoveryLifetime = TimeSpan.FromMinutes(10);
    // Windows this adapter grew because a request did not fit, by model. Every
    // request for that model keeps the window: a request without num_ctx makes
    // Ollama reload the model back at its default, then the next large request
    // reloads it again.
    private readonly ConcurrentDictionary<string, ContextWindowGrant> _contextWindowGrants =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _contextWindowGrowthBlockedUntil =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ContextWindowGrantIdleLifetime = TimeSpan.FromHours(6);
    private static readonly TimeSpan ContextWindowGrowthBlockLifetime = TimeSpan.FromMinutes(30);
    private const int ContextWindowGrantCapacity = 128;
    // Metadata a request depends on (its LocalOnly locality proof, the window
    // it needs) gets longer than discovery's two seconds: a daemon busy with
    // CPU inference answers /api/show slowly, and a missed proof blocks the call.
    private static readonly TimeSpan RequestMetadataTimeout = TimeSpan.FromSeconds(10);
    private readonly ILlmRouteContextWindowObservations? _contextWindows;

    private const int MaxRetryAttempts = 2; // 3 total attempts
    private const int MaxEmbeddingPromptChars = 3000;
    private const int FallbackEmbeddingPromptChars = 1500;
    private const int ArtifactDigestCacheCapacity = 128;
    // Positive observations are retained only as bounded diagnostics. Pinned
    // embeddings re-read /api/tags before every inference because an external
    // same-tag pull can replace the artifact without notifying this process.
    private static readonly TimeSpan ArtifactDigestCacheTtl = TimeSpan.Zero;

    /// <summary>
    /// "Ollama" for the built-in adapter. A LAN-node subclass overrides this with
    /// its <c>Ollama@node</c> name so the router, cooldowns, and Model Scout treat
    /// each computer as its own provider while sharing this implementation.
    /// </summary>
    public virtual string ProviderName => OllamaProviderFamily.LocalProviderName;
    public LlmProviderExecutionContractDescriptor ExecutionContract => _executionContract.Descriptor;
    public LlmProviderExecutionTranslation TranslateExecution(
        string model,
        LlmGenerationParameters parameters,
        LlmRequestControl control,
        IReadOnlyDictionary<string, object>? providerExperimentalSettings = null)
        => _executionContract.Translate(parameters, control, providerExperimentalSettings);
    // GenerateStreamingResponseAsync below throws NotSupportedException; the
    // router only consults this flag to decide whether to call it. Advertising
    // streaming here sent every router streaming request into that throw (and
    // then into failover) instead of the router's non-streaming path, which
    // delivers the completed reply as one chunk. Flip this back to true only
    // together with a real streaming implementation.
    public bool SupportsStreaming => false;
    public bool SupportsEmbeddings => true;
    public string? DefaultEmbeddingModel => EmbeddingModelDefaults.Model;

    public OllamaLlmProvider(
        ILogger<OllamaLlmProvider> logger,
        IHttpClientFactory httpClientFactory,
        IOptions<OllamaProviderOptions> options,
        ILocalLlmRuntimeManager? localRuntimeManager = null,
        ILlmRouteContextWindowObservations? contextWindows = null)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _localRuntimeManager = localRuntimeManager;
        _contextWindows = contextWindows;
        _nodes = BuildNodeStates(_options);

        // Provider construction is a routine startup diagnostic. Information-level
        // logs are mirrored into the app activity strip, where this otherwise looks
        // like user work even when Ollama was not selected or installed.
        _logger.LogDebug(
            "Ollama provider initialized with {Count} node(s): {Nodes}",
            _nodes.Count,
            string.Join(", ", _nodes.Select(n => $"{n.Name}@{n.Endpoint} cap={n.MaxConcurrentRequests}")));
    }

    public bool SupportsModel(string model)
    {
        // Ollama can support any model that's pulled locally
        return true;
    }

    public bool SupportsVision(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;

        var normalizedModel = model.Trim();
        if (_visionCapabilityCache.TryGetValue(normalizedModel, out var advertisedVision))
        {
            // Ollama's /api/show capabilities describe the installed artifact and
            // are authoritative. This handles new multimodal families without an
            // app update and prevents a misleading model name from creating a
            // false-positive vision route.
            return advertisedVision;
        }

        // Cold-start / older-Ollama fallback. Model discovery refreshes the live
        // capability cache before Model Scout and the chat picker consume it.
        return VisionModelFamilies.SupportsVision(ProviderName, normalizedModel)
               || normalizedModel.Contains("vision", StringComparison.OrdinalIgnoreCase);
    }

    public Task<List<string>> GetAvailableModelsAsync()
        => GetAvailableModelsAsync(CancellationToken.None);

    public async Task<List<string>> GetAvailableModelsAsync(CancellationToken ct)
    {
        // Return cached models if still valid
        if (_cachedModels != null && DateTime.UtcNow - _lastModelCheck < _modelCacheDuration)
        {
            return _cachedModels;
        }

        try
        {
            var merged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in GetOrderedNodesForRouting(includeCoolingDownNodes: true))
            {
                var (success, models, error) = await GetModelsFromNodeAsync(
                    node,
                    TimeSpan.FromSeconds(5),
                    ct).ConfigureAwait(false);
                if (!success)
                {
                    node.MarkFailure(error ?? "model list probe failed");
                    _logger.LogDebug("Ollama node {Node} tags probe failed: {Error}", node.Name, error);
                    continue;
                }

                node.MarkSuccess();
                foreach (var m in models)
                {
                    merged.Add(m);
                }

                await RefreshVisionCapabilitiesAsync(node, models, ct).ConfigureAwait(false);
            }

            if (merged.Count == 0)
            {
                if (_cachedModels != null)
                {
                    _logger.LogWarning("Failed to refresh Ollama model list from all nodes; keeping cached list of {Count} model(s)", _cachedModels.Count);
                    return _cachedModels;
                }

                // An empty local-model list is the normal state on a fresh install where
                // the user chose cloud AI (or has not installed Ollama yet). Keep this as
                // a diagnostic instead of surfacing a warning in the user activity feed.
                _logger.LogDebug("No Ollama models detected across all configured nodes");
                return new List<string>();
            }

            _cachedModels = merged.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            _lastModelCheck = DateTime.UtcNow;

            // Auto-detect default model from preferred list
            _detectedDefaultModel = _options.PreferredModels
                .FirstOrDefault(pm => _cachedModels.Any(m =>
                    m.StartsWith(pm, StringComparison.OrdinalIgnoreCase)));

            _detectedEmbeddingModel = _cachedModels.FirstOrDefault(EmbeddingModelDefaults.IsPinnedModel);

            _logger.LogInformation(
                "Ollama has {Count} merged model(s) across {Nodes} node(s). Default: {Default}, Embedding: {Embedding}",
                _cachedModels.Count,
                _nodes.Count,
                _detectedDefaultModel ?? "none detected",
                _detectedEmbeddingModel ?? "none detected");

            return _cachedModels;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Ollama models");
            return _cachedModels ?? new List<string>();
        }
    }

    public async Task<LlmResponse> GenerateResponseAsync(
        string prompt,
        string model,
        Dictionary<string, object>? executionSettings = null)
    {
        var physicalBudget = LlmReservedExecutionSettings.GetPhysicalRequestBudget(executionSettings);
        var requestCancellation =
            LlmReservedExecutionSettings.GetRequestCancellationToken(executionSettings);
        requestCancellation.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        var effectiveModel = await GetEffectiveModelAsync(model).ConfigureAwait(false);
        var localOnly = LlmReservedExecutionSettings.GetRequestPrivacyClass(executionSettings)
            == LlmPrivacyClass.LocalOnly;
        if (localOnly
            && OllamaModelLocalityPolicy.IsCloudBacked(effectiveModel))
        {
            stopwatch.Stop();
            return CreateErrorResponse(
                effectiveModel,
                $"Ollama cloud-backed model '{effectiveModel}' cannot satisfy LocalOnly privacy.",
                (int)stopwatch.ElapsedMilliseconds);
        }
        var expectedArtifactDigest =
            LlmReservedExecutionSettings.GetExpectedModelArtifactDigest(executionSettings);
        var autoPullAttempted = false;

        // Ollama caches the KV prefix implicitly (longest-prefix slot match),
        // so it needs no explicit cache-control blocks. Strip the shared marker
        // while retaining the prefix length for serving-health diagnostics.
        var (cachePrefix, _) = SwarmCore.Abstractions.LlmRouting.PromptCaching.Split(prompt);
        prompt = SwarmCore.Abstractions.LlmRouting.PromptCaching.Strip(prompt);

        // Transport failures retain their retry policy. A successful response
        // with an untried wire-format repair is a different state transition,
        // so network retries must not consume that recovery opportunity.
        var cpuGrammarRecoveryAttempted = false;
        var contextWindowResized = false;
        var retryDelayNeeded = false;
        for (int attempt = 0; attempt <= MaxRetryAttempts;)
        {
            if (physicalBudget?.Exhausted == true)
                return CreateErrorResponse(effectiveModel, "Physical inference request limit exhausted; no retry was submitted.", (int)stopwatch.ElapsedMilliseconds);
            using var lease = await AcquireNodeLeaseAsync(executionSettings, requestCancellation).ConfigureAwait(false);
            var node = lease.Node;

            try
            {
                if (localOnly)
                {
                    var localityError = await GetLocalOnlyRejectionAsync(
                            node,
                            effectiveModel,
                            requestCancellation)
                        .ConfigureAwait(false);
                    if (localityError is not null)
                    {
                        stopwatch.Stop();
                        return CreateErrorResponse(
                            effectiveModel,
                            localityError,
                            (int)stopwatch.ElapsedMilliseconds);
                    }
                }

                if (expectedArtifactDigest is not null)
                {
                    var digestCheck = await GetInstalledArtifactDigestAsync(
                        node,
                        effectiveModel,
                        requestCancellation,
                        forceRefresh: true).ConfigureAwait(false);
                    if (!digestCheck.Success
                        || !string.Equals(
                            EmbeddingModelIdentity.NormalizeDigest(expectedArtifactDigest),
                            EmbeddingModelIdentity.NormalizeDigest(digestCheck.Digest),
                            StringComparison.Ordinal))
                    {
                        stopwatch.Stop();
                        return CreateErrorResponse(
                            effectiveModel,
                            $"Ollama text artifact mismatch for '{effectiveModel}': expected {expectedArtifactDigest}, observed {digestCheck.Digest ?? "missing"}. Inference was blocked.",
                            (int)stopwatch.ElapsedMilliseconds);
                    }
                }

                if (retryDelayNeeded)
                {
                    retryDelayNeeded = false;
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)); // 4s, 8s
                    _logger.LogWarning(
                        "Ollama retry {Attempt}/{MaxAttempts} for {Model} on {Node} after {Delay}s delay",
                        attempt, MaxRetryAttempts, effectiveModel, node.Name, delay.TotalSeconds);
                    await Task.Delay(delay, requestCancellation).ConfigureAwait(false);
                    stopwatch.Restart();
                }

                using var client = CreateHttpClient();

                var request = new OllamaChatRequest
                {
                    Model = effectiveModel,
                    Messages = new List<OllamaMessage>
                    {
                        new() { Role = "user", Content = prompt }
                    },
                    Stream = UseStreaming
                };

                ApplyExecutionSettings(request, executionSettings);
                var usingCpuGrammarRecovery = ApplyRememberedCpuGrammarRecovery(node, request, executionSettings)
                    || cpuGrammarRecoveryAttempted;
                ApplyContextWindow(request);

                var json = JsonSerializer.Serialize(request);
                if (physicalBudget is not null && !physicalBudget.TryConsume())
                    return CreateErrorResponse(effectiveModel, "Physical inference request limit exhausted; no retry was submitted.", (int)stopwatch.ElapsedMilliseconds);

                if (request.Stream)
                {
                    // ── Streaming path: read chunks, track node activity, detect hangs ──
                    using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{node.Endpoint}/api/chat")
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };

                    using var headersCts = CancellationTokenSource.CreateLinkedTokenSource(requestCancellation);
                    headersCts.CancelAfter(TimeSpan.FromSeconds(
                        ResolveHeadersTimeoutSeconds(executionSettings)));
                    using var response = await client.SendAsync(
                        httpRequest, HttpCompletionOption.ResponseHeadersRead, headersCts.Token).ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        var responseContent = await response.Content
                            .ReadAsStringAsync(requestCancellation)
                            .ConfigureAwait(false);
                        stopwatch.Stop();

                        if (physicalBudget?.Exhausted == true)
                            return CreateErrorResponse(effectiveModel, BuildApiError(response.StatusCode, responseContent), (int)stopwatch.ElapsedMilliseconds);

                        if (IsModelNotFoundResponse(response.StatusCode, responseContent)
                            && _options.AutoPullMissingModels
                            && LocalModelArtifactCompatibility.IsCompatibleWithCurrentProcess(effectiveModel)
                            && !autoPullAttempted)
                        {
                            autoPullAttempted = true;
                            _logger.LogWarning(
                                "Ollama model {Model} not found on {Node}; attempting automatic pull",
                                effectiveModel, node.Name);

                            var pulled = await EnsureModelAsync(
                                effectiveModel,
                                node.Name,
                                requestCancellation).ConfigureAwait(false);
                            if (pulled)
                            {
                                _logger.LogInformation(
                                    "Ollama automatic pull succeeded for {Model}; retrying request",
                                    effectiveModel);
                                continue;
                            }
                        }

                        var error = BuildApiError(response.StatusCode, responseContent);
                        var overflow = await ResolveContextOverflowAsync(
                                node, request, prompt, imageCount: 0, response.StatusCode, responseContent,
                                contextWindowResized, requestCancellation)
                            .ConfigureAwait(false);
                        if (overflow.Retry)
                        {
                            contextWindowResized = true;
                            stopwatch.Start();
                            continue;
                        }
                        if (overflow.Error is { } contextError)
                        {
                            return CreateErrorResponse(effectiveModel, contextError, (int)stopwatch.ElapsedMilliseconds);
                        }

                        if (IsGrammarRuntimeError(response.StatusCode, responseContent))
                        {
                            var recovered = cpuGrammarRecoveryAttempted ? null : PrepareCpuGrammarRecovery(request, executionSettings);
                            if (recovered is not null)
                            {
                                cpuGrammarRecoveryAttempted = true;
                                executionSettings = recovered;
                                _logger.LogWarning(
                                    "Ollama grammar runtime error for {Model} on {Node}; retrying once on CPU with the same JSON contract",
                                    effectiveModel, node.Name);
                                stopwatch.Start();
                                continue;
                            }
                            _cpuGrammarRecoveryUntil.TryRemove(CpuGrammarRecoveryKey(node, request, executionSettings), out _);
                            _logger.LogWarning(
                                "Ollama grammar runtime error for {Model} on {Node}; CPU recovery failed or placement is explicit, stopping unchanged retries",
                                effectiveModel, node.Name);
                            return CreateErrorResponse(effectiveModel, error, (int)stopwatch.ElapsedMilliseconds);
                        }

                        if (IsPermanentModelErrorResponse(response.StatusCode, responseContent))
                        {
                            node.MarkFailure(error);
                            _logger.LogError("Ollama API model error on {Node}: {StatusCode} - {Content}", node.Name, response.StatusCode, responseContent);
                            return CreateErrorResponse(effectiveModel, error, (int)stopwatch.ElapsedMilliseconds);
                        }

                        if (IsTransientStatusCode(response.StatusCode) && attempt < MaxRetryAttempts)
                        {
                            node.MarkFailure(error);
                            _logger.LogWarning(
                                "Ollama transient API error on {Node}: {Status} for {Model}; retrying another node/attempt",
                                node.Name, response.StatusCode, effectiveModel);
                            attempt++;
                            retryDelayNeeded = true;
                            continue;
                        }

                        _logger.LogError("Ollama API error on {Node}: {StatusCode} - {Content}", node.Name, response.StatusCode, responseContent);
                        return CreateErrorResponse(effectiveModel, error, (int)stopwatch.ElapsedMilliseconds);
                    }

                    // Success — stream the body, tracking activity per chunk
                    var (streamedContent, streamedThinking, finalChunk) =
                        await ReadStreamingResponseAsync(response, node, requestCancellation).ConfigureAwait(false);
                    var promptEval = finalChunk?.PromptEvalCount;
                    var eval = finalChunk?.EvalCount;
                    if (string.IsNullOrWhiteSpace(streamedContent) && physicalBudget?.Exhausted == true)
                        return CreateErrorResponse(effectiveModel, "Model returned no visible response; the physical request limit prevents an automatic retry.", (int)stopwatch.ElapsedMilliseconds);
                    if (string.IsNullOrWhiteSpace(streamedContent)
                        && (!string.IsNullOrWhiteSpace(streamedThinking) || eval > 0))
                    {
                        executionSettings = ForceOllamaThinkingOff(executionSettings);
                        executionSettings["ollama_stream"] = false;
                        node.MarkFailure("empty streamed content with Ollama thinking/output tokens");
                        _logger.LogWarning(
                            "Ollama streamed empty content for {Model} on {Node} with {EvalCount} output tokens; retrying non-streaming with think=false",
                            effectiveModel,
                            node.Name,
                            eval ?? 0);
                        continue;
                    }

                    if (expectedArtifactDigest is not null)
                    {
                        var postDigest = await GetInstalledArtifactDigestAsync(
                            node,
                            effectiveModel,
                            requestCancellation,
                            forceRefresh: true).ConfigureAwait(false);
                        if (!postDigest.Success
                            || !string.Equals(
                                EmbeddingModelIdentity.NormalizeDigest(expectedArtifactDigest),
                                EmbeddingModelIdentity.NormalizeDigest(postDigest.Digest),
                                StringComparison.Ordinal))
                        {
                            stopwatch.Stop();
                            return CreateErrorResponse(
                                effectiveModel,
                                $"Ollama text artifact changed during inference for '{effectiveModel}': expected {expectedArtifactDigest}, observed {postDigest.Digest ?? "missing"}. The response was discarded.",
                                (int)stopwatch.ElapsedMilliseconds);
                        }
                    }

                    if (usingCpuGrammarRecovery
                        && !CompleteCpuGrammarRecovery(node, request, executionSettings, streamedContent, out var grammarError))
                    {
                        return CreateErrorResponse(effectiveModel, grammarError!, (int)stopwatch.ElapsedMilliseconds);
                    }

                    node.MarkSuccess();
                    stopwatch.Stop();

                    return new LlmResponse
                    {
                        Content = streamedContent,
                        Provider = ProviderName,
                        Model = effectiveModel,
                        UsageMeasurementComplete = finalChunk?.Done == true && promptEval is >= 0 && eval is >= 0,
                        InputTokens = promptEval ?? EstimateTokens(prompt),
                        OutputTokens = eval ?? EstimateTokens(streamedContent),
                        TotalTokens = (promptEval ?? 0) + (eval ?? 0),
                        ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
                        PrefillDurationMs = NsToMs(finalChunk?.PromptEvalDuration),
                        DecodeDurationMs = NsToMs(finalChunk?.EvalDuration),
                        ModelLoadDurationMs = NsToMs(finalChunk?.LoadDuration),
                        DeclaredCachePrefixChars = cachePrefix?.Length ?? 0,
                        WasSuccessful = true,
                        ReasoningContent = streamedThinking,
                        HiddenReasoningSuppressionRequested = request.Think == false,
                        Diagnostics = BuildResponseDiagnostics(streamedContent, streamedThinking, eval, usingCpuGrammarRecovery, contextWindowResized)
                    };
                }
                else
                {
                    // ── Non-streaming fallback (StreamInactivityTimeoutSeconds == 0) ──
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    var response = await client.PostAsync(
                            $"{node.Endpoint}/api/chat",
                            content,
                            requestCancellation)
                        .ConfigureAwait(false);
                    var responseContent = await response.Content
                        .ReadAsStringAsync(requestCancellation)
                        .ConfigureAwait(false);

                    stopwatch.Stop();

                    if (response.IsSuccessStatusCode)
                    {
                        // Change the wire request once; repeated think=false output
                        // is evidence that this recovery setting did not help.
                        var chatResponse = JsonSerializer.Deserialize<OllamaChatResponse>(responseContent, SwarmJsonOptions.ExternalApi);
                        var responseText = chatResponse?.Message?.Content ?? "";
                        if (string.IsNullOrWhiteSpace(responseText) && physicalBudget?.Exhausted == true)
                            return CreateErrorResponse(effectiveModel, "Model returned no visible response; the physical request limit prevents an automatic retry.", (int)stopwatch.ElapsedMilliseconds);
                        if (string.IsNullOrWhiteSpace(responseText)
                            && !string.IsNullOrWhiteSpace(chatResponse?.Message?.Thinking)
                            && request.Think != false)
                        {
                            executionSettings = ForceOllamaThinkingOff(executionSettings);
                            node.MarkFailure("empty content with Ollama thinking output");
                            _logger.LogWarning(
                                "Ollama returned thinking output but empty content for {Model} on {Node}; retrying with think=false",
                                effectiveModel,
                                node.Name);
                            continue;
                        }

                        if (expectedArtifactDigest is not null)
                        {
                            var postDigest = await GetInstalledArtifactDigestAsync(
                                node,
                                effectiveModel,
                                requestCancellation,
                                forceRefresh: true).ConfigureAwait(false);
                            if (!postDigest.Success
                                || !string.Equals(
                                    EmbeddingModelIdentity.NormalizeDigest(expectedArtifactDigest),
                                    EmbeddingModelIdentity.NormalizeDigest(postDigest.Digest),
                                    StringComparison.Ordinal))
                            {
                                return CreateErrorResponse(
                                    effectiveModel,
                                    $"Ollama text artifact changed during inference for '{effectiveModel}': expected {expectedArtifactDigest}, observed {postDigest.Digest ?? "missing"}. The response was discarded.",
                                    (int)stopwatch.ElapsedMilliseconds);
                            }
                        }

                        if (usingCpuGrammarRecovery
                            && !CompleteCpuGrammarRecovery(node, request, executionSettings, responseText, out var grammarError))
                        {
                            return CreateErrorResponse(effectiveModel, grammarError!, (int)stopwatch.ElapsedMilliseconds);
                        }

                        node.MarkSuccess();

                        return new LlmResponse
                        {
                            Content = responseText,
                            Provider = ProviderName,
                            Model = effectiveModel,
                            UsageMeasurementComplete = chatResponse?.Done == true && chatResponse.PromptEvalCount is >= 0 && chatResponse.EvalCount is >= 0,
                            InputTokens = chatResponse?.PromptEvalCount ?? EstimateTokens(prompt),
                            OutputTokens = chatResponse?.EvalCount ?? EstimateTokens(responseText),
                            TotalTokens = (chatResponse?.PromptEvalCount ?? 0) + (chatResponse?.EvalCount ?? 0),
                            ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
                            PrefillDurationMs = NsToMs(chatResponse?.PromptEvalDuration),
                            DecodeDurationMs = NsToMs(chatResponse?.EvalDuration),
                            ModelLoadDurationMs = NsToMs(chatResponse?.LoadDuration),
                            DeclaredCachePrefixChars = cachePrefix?.Length ?? 0,
                            WasSuccessful = true,
                            ReasoningContent = chatResponse?.Message?.Thinking,
                            HiddenReasoningSuppressionRequested = request.Think == false,
                            Diagnostics = BuildResponseDiagnostics(responseText, chatResponse?.Message?.Thinking, chatResponse?.EvalCount, usingCpuGrammarRecovery, contextWindowResized)
                        };
                    }

                    if (physicalBudget?.Exhausted == true)
                            return CreateErrorResponse(effectiveModel, BuildApiError(response.StatusCode, responseContent), (int)stopwatch.ElapsedMilliseconds);

                        if (IsModelNotFoundResponse(response.StatusCode, responseContent)
                        && _options.AutoPullMissingModels
                        && LocalModelArtifactCompatibility.IsCompatibleWithCurrentProcess(effectiveModel)
                        && !autoPullAttempted)
                    {
                        autoPullAttempted = true;
                        _logger.LogWarning(
                            "Ollama model {Model} not found on {Node}; attempting automatic pull",
                            effectiveModel, node.Name);

                        var pulled = await EnsureModelAsync(
                            effectiveModel,
                            node.Name,
                            requestCancellation).ConfigureAwait(false);
                        if (pulled)
                        {
                            _logger.LogInformation(
                                "Ollama automatic pull succeeded for {Model}; retrying request",
                                effectiveModel);
                            continue;
                        }
                    }

                    var error = BuildApiError(response.StatusCode, responseContent);
                    var overflow = await ResolveContextOverflowAsync(
                            node, request, prompt, imageCount: 0, response.StatusCode, responseContent,
                            contextWindowResized, requestCancellation)
                        .ConfigureAwait(false);
                    if (overflow.Retry)
                    {
                        contextWindowResized = true;
                        stopwatch.Start();
                        continue;
                    }
                    if (overflow.Error is { } contextError)
                    {
                        return CreateErrorResponse(effectiveModel, contextError, (int)stopwatch.ElapsedMilliseconds);
                    }

                    if (IsGrammarRuntimeError(response.StatusCode, responseContent))
                    {
                        var recovered = cpuGrammarRecoveryAttempted ? null : PrepareCpuGrammarRecovery(request, executionSettings);
                        if (recovered is not null)
                        {
                            cpuGrammarRecoveryAttempted = true;
                            executionSettings = recovered;
                            _logger.LogWarning(
                                "Ollama grammar runtime error for {Model} on {Node}; retrying once on CPU with the same JSON contract",
                                effectiveModel, node.Name);
                            stopwatch.Start();
                            continue;
                        }
                        _cpuGrammarRecoveryUntil.TryRemove(CpuGrammarRecoveryKey(node, request, executionSettings), out _);
                        _logger.LogWarning(
                            "Ollama grammar runtime error for {Model} on {Node}; CPU recovery failed or placement is explicit, stopping unchanged retries",
                            effectiveModel, node.Name);
                        return CreateErrorResponse(effectiveModel, error, (int)stopwatch.ElapsedMilliseconds);
                    }

                    if (IsPermanentModelErrorResponse(response.StatusCode, responseContent))
                    {
                        node.MarkFailure(error);
                        _logger.LogError("Ollama API model error on {Node}: {StatusCode} - {Content}", node.Name, response.StatusCode, responseContent);
                        return CreateErrorResponse(effectiveModel, error, (int)stopwatch.ElapsedMilliseconds);
                    }

                    if (IsTransientStatusCode(response.StatusCode) && attempt < MaxRetryAttempts)
                    {
                        node.MarkFailure(error);
                        _logger.LogWarning(
                            "Ollama transient API error on {Node}: {Status} for {Model}; retrying another node/attempt",
                            node.Name, response.StatusCode, effectiveModel);
                        attempt++;
                        retryDelayNeeded = true;
                        continue;
                    }

                    _logger.LogError("Ollama API error on {Node}: {StatusCode} - {Content}", node.Name, response.StatusCode, responseContent);
                    return CreateErrorResponse(effectiveModel, error, (int)stopwatch.ElapsedMilliseconds);
                }
            }
            catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
            {
                // Runtime preemption and caller cancellation are scheduling
                // outcomes. Do not retry them or poison node health.
                throw;
            }
            catch (Exception ex) when (IsTransientError(ex) && attempt < MaxRetryAttempts)
            {
                if (physicalBudget?.Exhausted == true)
                    return CreateErrorResponse(effectiveModel, ex.Message, (int)stopwatch.ElapsedMilliseconds);
                node.MarkFailure(ex.Message);
                _logger.LogWarning(
                    "Ollama transient error on {Node} (attempt {Attempt}/{MaxAttempts}): {Message}",
                    node.Name, attempt + 1, MaxRetryAttempts + 1, ex.Message);
                attempt++;
                retryDelayNeeded = true;
                // Loop continues to next attempt
            }
            catch
            {
                node.MarkFailure("unhandled exception");
                throw;
            }
            // Non-transient errors or final-attempt transient errors propagate to LlmRouter for fallback
        }

        // Should not reach here — final attempt exception propagates via the missing catch filter
        stopwatch.Stop();
        throw new TimeoutException($"Ollama failed after {MaxRetryAttempts + 1} attempts for model {effectiveModel}");
    }

    public async Task<LlmResponse> GenerateVisionResponseAsync(
        string prompt,
        string imageBase64,
        string model,
        Dictionary<string, object>? executionSettings = null)
    {
        var physicalBudget = LlmReservedExecutionSettings.GetPhysicalRequestBudget(executionSettings);
        var requestCancellation =
            LlmReservedExecutionSettings.GetRequestCancellationToken(executionSettings);
        requestCancellation.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        var autoPullAttempted = false;

        // Find a vision-capable model
        var effectiveModel = model;
        if (string.IsNullOrEmpty(effectiveModel) || !SupportsVision(effectiveModel))
        {
            var models = await GetAvailableModelsAsync(requestCancellation).ConfigureAwait(false);
            effectiveModel = models.FirstOrDefault(m =>
                SupportsVision(m)
                && LocalModelArtifactCompatibility.IsCompatibleWithCurrentProcess(m));

            if (string.IsNullOrEmpty(effectiveModel))
            {
                return CreateErrorResponse(model, "No vision-capable model available. Pull llama3.2-vision with: ollama pull llama3.2-vision", 0);
            }
        }

        var localOnly = LlmReservedExecutionSettings.GetRequestPrivacyClass(executionSettings)
            == LlmPrivacyClass.LocalOnly;
        if (localOnly
            && OllamaModelLocalityPolicy.IsCloudBacked(effectiveModel))
        {
            stopwatch.Stop();
            return CreateErrorResponse(
                effectiveModel,
                $"Ollama cloud-backed model '{effectiveModel}' cannot satisfy LocalOnly privacy.",
                (int)stopwatch.ElapsedMilliseconds);
        }

        if (!LocalModelArtifactCompatibility.IsCompatibleWithCurrentProcess(effectiveModel))
        {
            return CreateErrorResponse(
                effectiveModel,
                $"Ollama model artifact '{effectiveModel}' is not compatible with this computer.",
                0);
        }

        // Clean base64 if it has data URI prefix
        var cleanBase64 = imageBase64;
        if (imageBase64.Contains(","))
        {
            cleanBase64 = imageBase64.Split(',').Last();
        }

        // Transport failures retain their retry policy. A successful response
        // with an untried wire-format repair is a different state transition,
        // so network retries must not consume that recovery opportunity.
        var cpuGrammarRecoveryAttempted = false;
        var contextWindowResized = false;
        var retryDelayNeeded = false;
        for (int attempt = 0; attempt <= MaxRetryAttempts;)
        {
            if (physicalBudget?.Exhausted == true)
                return CreateErrorResponse(effectiveModel, "Physical inference request limit exhausted; no retry was submitted.", (int)stopwatch.ElapsedMilliseconds);
            using var lease = await AcquireNodeLeaseAsync(executionSettings, requestCancellation).ConfigureAwait(false);
            var node = lease.Node;

            try
            {
                if (localOnly)
                {
                    var localityError = await GetLocalOnlyRejectionAsync(
                            node,
                            effectiveModel,
                            requestCancellation)
                        .ConfigureAwait(false);
                    if (localityError is not null)
                    {
                        stopwatch.Stop();
                        return CreateErrorResponse(
                            effectiveModel,
                            localityError,
                            (int)stopwatch.ElapsedMilliseconds);
                    }
                }

                if (retryDelayNeeded)
                {
                    retryDelayNeeded = false;
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                    _logger.LogWarning(
                        "Ollama vision retry {Attempt}/{MaxAttempts} for {Model} on {Node} after {Delay}s delay",
                        attempt, MaxRetryAttempts, effectiveModel, node.Name, delay.TotalSeconds);
                    await Task.Delay(delay, requestCancellation).ConfigureAwait(false);
                    stopwatch.Restart();
                }

                // Vision generation gets the full generation timeout — the 30s-capped
                // embedding timeout starved big local vision models (first-load + prompt
                // eval on a 27B model easily exceeds 30s).
                using var client = CreateHttpClient();

                var request = new OllamaChatRequest
                {
                    Model = effectiveModel,
                    Messages = new List<OllamaMessage>
                    {
                        new()
                        {
                            Role = "user",
                            Content = prompt,
                            Images = new List<string> { cleanBase64 }
                        }
                    },
                    Stream = UseStreaming
                };

                ApplyExecutionSettings(request, executionSettings);
                var usingCpuGrammarRecovery = ApplyRememberedCpuGrammarRecovery(node, request, executionSettings)
                    || cpuGrammarRecoveryAttempted;
                ApplyContextWindow(request);
                var json = JsonSerializer.Serialize(request);
                if (physicalBudget is not null && !physicalBudget.TryConsume())
                    return CreateErrorResponse(effectiveModel, "Physical inference request limit exhausted; no retry was submitted.", (int)stopwatch.ElapsedMilliseconds);

                if (request.Stream)
                {
                    using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{node.Endpoint}/api/chat")
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };

                    using var headersCts = CancellationTokenSource.CreateLinkedTokenSource(requestCancellation);
                    headersCts.CancelAfter(TimeSpan.FromSeconds(
                        ResolveHeadersTimeoutSeconds(executionSettings)));
                    using var response = await client.SendAsync(
                        httpRequest, HttpCompletionOption.ResponseHeadersRead, headersCts.Token).ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        var responseContent = await response.Content
                            .ReadAsStringAsync(requestCancellation)
                            .ConfigureAwait(false);
                        stopwatch.Stop();

                        if (physicalBudget?.Exhausted == true)
                            return CreateErrorResponse(effectiveModel, BuildApiError(response.StatusCode, responseContent), (int)stopwatch.ElapsedMilliseconds);

                        if (IsModelNotFoundResponse(response.StatusCode, responseContent)
                            && _options.AutoPullMissingModels
                            && LocalModelArtifactCompatibility.IsCompatibleWithCurrentProcess(effectiveModel)
                            && !autoPullAttempted)
                        {
                            autoPullAttempted = true;
                            _logger.LogWarning(
                                "Ollama vision model {Model} not found on {Node}; attempting automatic pull",
                                effectiveModel, node.Name);

                            var pulled = await EnsureModelAsync(
                                effectiveModel,
                                node.Name,
                                requestCancellation).ConfigureAwait(false);
                            if (pulled)
                            {
                                _logger.LogInformation(
                                    "Ollama automatic pull succeeded for vision model {Model}; retrying request",
                                    effectiveModel);
                                continue;
                            }
                        }

                        var error = BuildApiError(response.StatusCode, responseContent);
                        var overflow = await ResolveContextOverflowAsync(
                                node, request, prompt, imageCount: 1, response.StatusCode, responseContent,
                                contextWindowResized, requestCancellation)
                            .ConfigureAwait(false);
                        if (overflow.Retry)
                        {
                            contextWindowResized = true;
                            stopwatch.Start();
                            continue;
                        }
                        if (overflow.Error is { } contextError)
                        {
                            return CreateErrorResponse(effectiveModel, contextError, (int)stopwatch.ElapsedMilliseconds);
                        }

                        if (IsGrammarRuntimeError(response.StatusCode, responseContent))
                        {
                            var recovered = cpuGrammarRecoveryAttempted ? null : PrepareCpuGrammarRecovery(request, executionSettings);
                            if (recovered is not null)
                            {
                                cpuGrammarRecoveryAttempted = true;
                                executionSettings = recovered;
                                _logger.LogWarning(
                                    "Ollama grammar runtime error for {Model} on {Node}; retrying once on CPU with the same JSON contract",
                                    effectiveModel, node.Name);
                                stopwatch.Start();
                                continue;
                            }
                            _cpuGrammarRecoveryUntil.TryRemove(CpuGrammarRecoveryKey(node, request, executionSettings), out _);
                            _logger.LogWarning(
                                "Ollama grammar runtime error for {Model} on {Node}; CPU recovery failed or placement is explicit, stopping unchanged retries",
                                effectiveModel, node.Name);
                            return CreateErrorResponse(effectiveModel, error, (int)stopwatch.ElapsedMilliseconds);
                        }

                        if (IsPermanentModelErrorResponse(response.StatusCode, responseContent))
                        {
                            node.MarkFailure(error);
                            _logger.LogError("Ollama vision API model error on {Node}: {StatusCode} - {Content}", node.Name, response.StatusCode, responseContent);
                            return CreateErrorResponse(effectiveModel, error, (int)stopwatch.ElapsedMilliseconds);
                        }

                        if (IsTransientStatusCode(response.StatusCode) && attempt < MaxRetryAttempts)
                        {
                            node.MarkFailure(error);
                            _logger.LogWarning(
                                "Ollama vision transient API error on {Node}: {Status} for {Model}; retrying",
                                node.Name, response.StatusCode, effectiveModel);
                            attempt++;
                            retryDelayNeeded = true;
                            continue;
                        }

                        return CreateErrorResponse(effectiveModel, error, (int)stopwatch.ElapsedMilliseconds);
                    }

                    var (streamedContent, streamedThinking, visionFinalChunk) =
                        await ReadStreamingResponseAsync(response, node, requestCancellation).ConfigureAwait(false);
                    var promptEval = visionFinalChunk?.PromptEvalCount;
                    var eval = visionFinalChunk?.EvalCount;
                    if (string.IsNullOrWhiteSpace(streamedContent) && physicalBudget?.Exhausted == true)
                        return CreateErrorResponse(effectiveModel, "Model returned no visible response; the physical request limit prevents an automatic retry.", (int)stopwatch.ElapsedMilliseconds);
                    if (string.IsNullOrWhiteSpace(streamedContent)
                        && (!string.IsNullOrWhiteSpace(streamedThinking) || eval > 0))
                    {
                        executionSettings = ForceOllamaThinkingOff(executionSettings);
                        executionSettings["ollama_stream"] = false;
                        node.MarkFailure("empty streamed vision content with Ollama thinking/output tokens");
                        _logger.LogWarning(
                            "Ollama streamed empty vision content for {Model} on {Node} with {EvalCount} output tokens; retrying non-streaming with think=false",
                            effectiveModel,
                            node.Name,
                            eval ?? 0);
                        continue;
                    }

                    if (usingCpuGrammarRecovery
                        && !CompleteCpuGrammarRecovery(node, request, executionSettings, streamedContent, out var grammarError))
                    {
                        return CreateErrorResponse(effectiveModel, grammarError!, (int)stopwatch.ElapsedMilliseconds);
                    }

                    node.MarkSuccess();
                    stopwatch.Stop();

                    return new LlmResponse
                    {
                        Content = streamedContent,
                        Provider = ProviderName,
                        Model = effectiveModel,
                        UsageMeasurementComplete = visionFinalChunk?.Done == true && promptEval is >= 0 && eval is >= 0,
                        InputTokens = promptEval ?? 0,
                        OutputTokens = eval ?? 0,
                        TotalTokens = (promptEval ?? 0) + (eval ?? 0),
                        ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
                        WasSuccessful = true,
                        ReasoningContent = streamedThinking,
                        HiddenReasoningSuppressionRequested = request.Think == false,
                        Diagnostics = BuildResponseDiagnostics(streamedContent, streamedThinking, eval, usingCpuGrammarRecovery, contextWindowResized)
                    };
                }
                else
                {
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    var response = await client.PostAsync(
                            $"{node.Endpoint}/api/chat",
                            content,
                            requestCancellation)
                        .ConfigureAwait(false);
                    var responseContent = await response.Content
                        .ReadAsStringAsync(requestCancellation)
                        .ConfigureAwait(false);

                    stopwatch.Stop();

                    if (response.IsSuccessStatusCode)
                    {
                        // Change the wire request once; repeated think=false output
                        // is evidence that this recovery setting did not help.
                        var chatResponse = JsonSerializer.Deserialize<OllamaChatResponse>(responseContent, SwarmJsonOptions.ExternalApi);
                        var responseText = chatResponse?.Message?.Content ?? "";
                        if (string.IsNullOrWhiteSpace(responseText) && physicalBudget?.Exhausted == true)
                            return CreateErrorResponse(effectiveModel, "Model returned no visible response; the physical request limit prevents an automatic retry.", (int)stopwatch.ElapsedMilliseconds);
                        if (string.IsNullOrWhiteSpace(responseText)
                            && !string.IsNullOrWhiteSpace(chatResponse?.Message?.Thinking)
                            && request.Think != false)
                        {
                            executionSettings = ForceOllamaThinkingOff(executionSettings);
                            node.MarkFailure("empty content with Ollama thinking output");
                            _logger.LogWarning(
                                "Ollama returned thinking output but empty content for vision model {Model} on {Node}; retrying with think=false",
                                effectiveModel,
                                node.Name);
                            continue;
                        }

                        if (usingCpuGrammarRecovery
                            && !CompleteCpuGrammarRecovery(node, request, executionSettings, responseText, out var grammarError))
                        {
                            return CreateErrorResponse(effectiveModel, grammarError!, (int)stopwatch.ElapsedMilliseconds);
                        }

                        node.MarkSuccess();

                        return new LlmResponse
                        {
                            Content = responseText,
                            Provider = ProviderName,
                            Model = effectiveModel,
                            UsageMeasurementComplete = chatResponse?.Done == true && chatResponse.PromptEvalCount is >= 0 && chatResponse.EvalCount is >= 0,
                            InputTokens = chatResponse?.PromptEvalCount ?? 0,
                            OutputTokens = chatResponse?.EvalCount ?? 0,
                            TotalTokens = (chatResponse?.PromptEvalCount ?? 0) + (chatResponse?.EvalCount ?? 0),
                            ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
                            WasSuccessful = true,
                            ReasoningContent = chatResponse?.Message?.Thinking,
                            HiddenReasoningSuppressionRequested = request.Think == false,
                            Diagnostics = BuildResponseDiagnostics(responseText, chatResponse?.Message?.Thinking, chatResponse?.EvalCount, usingCpuGrammarRecovery, contextWindowResized)
                        };
                    }

                    if (physicalBudget?.Exhausted == true)
                            return CreateErrorResponse(effectiveModel, BuildApiError(response.StatusCode, responseContent), (int)stopwatch.ElapsedMilliseconds);

                        if (IsModelNotFoundResponse(response.StatusCode, responseContent)
                        && _options.AutoPullMissingModels
                        && LocalModelArtifactCompatibility.IsCompatibleWithCurrentProcess(effectiveModel)
                        && !autoPullAttempted)
                    {
                        autoPullAttempted = true;
                        _logger.LogWarning(
                            "Ollama vision model {Model} not found on {Node}; attempting automatic pull",
                            effectiveModel, node.Name);

                        var pulled = await EnsureModelAsync(
                            effectiveModel,
                            node.Name,
                            requestCancellation).ConfigureAwait(false);
                        if (pulled)
                        {
                            _logger.LogInformation(
                                "Ollama automatic pull succeeded for vision model {Model}; retrying request",
                                effectiveModel);
                            continue;
                        }
                    }

                    var error = BuildApiError(response.StatusCode, responseContent);
                    var overflow = await ResolveContextOverflowAsync(
                            node, request, prompt, imageCount: 1, response.StatusCode, responseContent,
                            contextWindowResized, requestCancellation)
                        .ConfigureAwait(false);
                    if (overflow.Retry)
                    {
                        contextWindowResized = true;
                        stopwatch.Start();
                        continue;
                    }
                    if (overflow.Error is { } contextError)
                    {
                        return CreateErrorResponse(effectiveModel, contextError, (int)stopwatch.ElapsedMilliseconds);
                    }

                    if (IsGrammarRuntimeError(response.StatusCode, responseContent))
                    {
                        var recovered = cpuGrammarRecoveryAttempted ? null : PrepareCpuGrammarRecovery(request, executionSettings);
                        if (recovered is not null)
                        {
                            cpuGrammarRecoveryAttempted = true;
                            executionSettings = recovered;
                            _logger.LogWarning(
                                "Ollama grammar runtime error for {Model} on {Node}; retrying once on CPU with the same JSON contract",
                                effectiveModel, node.Name);
                            stopwatch.Start();
                            continue;
                        }
                        _cpuGrammarRecoveryUntil.TryRemove(CpuGrammarRecoveryKey(node, request, executionSettings), out _);
                        _logger.LogWarning(
                            "Ollama grammar runtime error for {Model} on {Node}; CPU recovery failed or placement is explicit, stopping unchanged retries",
                            effectiveModel, node.Name);
                        return CreateErrorResponse(effectiveModel, error, (int)stopwatch.ElapsedMilliseconds);
                    }

                    if (IsPermanentModelErrorResponse(response.StatusCode, responseContent))
                    {
                        node.MarkFailure(error);
                        _logger.LogError("Ollama vision API model error on {Node}: {StatusCode} - {Content}", node.Name, response.StatusCode, responseContent);
                        return CreateErrorResponse(effectiveModel, error, (int)stopwatch.ElapsedMilliseconds);
                    }

                    if (IsTransientStatusCode(response.StatusCode) && attempt < MaxRetryAttempts)
                    {
                        node.MarkFailure(error);
                        _logger.LogWarning(
                            "Ollama vision transient API error on {Node}: {Status} for {Model}; retrying",
                            node.Name, response.StatusCode, effectiveModel);
                        attempt++;
                        retryDelayNeeded = true;
                        continue;
                    }

                    return CreateErrorResponse(effectiveModel, error, (int)stopwatch.ElapsedMilliseconds);
                }
            }
            catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsTransientError(ex) && attempt < MaxRetryAttempts)
            {
                if (physicalBudget?.Exhausted == true)
                    return CreateErrorResponse(effectiveModel, ex.Message, (int)stopwatch.ElapsedMilliseconds);
                node.MarkFailure(ex.Message);
                _logger.LogWarning(
                    "Ollama vision transient error on {Node} (attempt {Attempt}/{MaxAttempts}): {Message}",
                    node.Name, attempt + 1, MaxRetryAttempts + 1, ex.Message);
                attempt++;
                retryDelayNeeded = true;
            }
            catch
            {
                node.MarkFailure("unhandled exception");
                throw;
            }
        }

        stopwatch.Stop();
        throw new TimeoutException($"Ollama vision failed after {MaxRetryAttempts + 1} attempts for model {effectiveModel}");
    }

    public async Task<(bool IsHealthy, string Message)> TestConnectivityAsync()
    {
        var healthy = new List<string>();
        var unhealthy = new List<string>();

        foreach (var node in _nodes)
        {
            var (ok, _, error) = await GetModelsFromNodeAsync(node, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (ok)
            {
                node.MarkSuccess();
                healthy.Add(node.Name);
            }
            else
            {
                node.MarkFailure(error ?? "connectivity probe failed");
                unhealthy.Add($"{node.Name} ({error ?? "unknown error"})");
            }
        }

        if (healthy.Count > 0)
        {
            var unhealthySummary = unhealthy.Count > 0
                ? $"; down: {string.Join(", ", unhealthy)}"
                : string.Empty;
            return (true, $"Ollama healthy nodes {healthy.Count}/{_nodes.Count}: {string.Join(", ", healthy)}{unhealthySummary}");
        }

        return (false, $"No healthy Ollama nodes. Checked {_nodes.Count}: {string.Join(", ", unhealthy)}");
    }

    public async Task<LlmEmbeddingResponse> GenerateEmbeddingAsync(
        string text,
        string model,
        Dictionary<string, object>? executionSettings = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var requestCancellation =
            LlmReservedExecutionSettings.GetRequestCancellationToken(executionSettings);
        requestCancellation.ThrowIfCancellationRequested();
        var expectedArtifactDigest =
            LlmReservedExecutionSettings.GetExpectedModelArtifactDigest(executionSettings);
        // Ordinary routing remains pinned to the current identity. The sole
        // exception is the explicit legacy identity used by the controlled
        // rebuild orchestrator, which keeps rollback/rebuild deterministic.
        var effectiveModel = EmbeddingModelDefaults.IsSupportedMigrationModel(model)
            ? model.Trim()
            : EmbeddingModelDefaults.Model;
        var localOnly = LlmReservedExecutionSettings.GetRequestPrivacyClass(executionSettings)
            == LlmPrivacyClass.LocalOnly;
        if (localOnly && OllamaModelLocalityPolicy.IsCloudBacked(effectiveModel))
        {
            stopwatch.Stop();
            return new LlmEmbeddingResponse
            {
                Success = false,
                Provider = ProviderName,
                Model = effectiveModel,
                ErrorMessage = $"Ollama cloud-backed model '{effectiveModel}' cannot satisfy LocalOnly privacy.",
                ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
            };
        }
        var requestText = TrimEmbeddingPrompt(text, MaxEmbeddingPromptChars);

        // Check if embedding model is available
        var models = await GetAvailableModelsAsync(requestCancellation).ConfigureAwait(false);
        var hasEmbeddingModel = models.Any(m =>
            m.StartsWith(effectiveModel, StringComparison.OrdinalIgnoreCase));

        if (!hasEmbeddingModel)
        {
            if (_options.AutoPullEmbeddingModel)
            {
                _logger.LogWarning(
                    "Pinned Ollama embedding model {Model} is not installed; attempting automatic pull before failing embedding request",
                    effectiveModel);

                hasEmbeddingModel = await EnsurePinnedEmbeddingModelAvailableAsync(
                    effectiveModel,
                    requestCancellation).ConfigureAwait(false);
            }
        }

        if (!hasEmbeddingModel)
        {
            return new LlmEmbeddingResponse
            {
                Success = false,
                Provider = ProviderName,
                Model = effectiveModel,
                ErrorMessage = $"Embedding model '{effectiveModel}' not installed. Pull it with: ollama pull {effectiveModel}",
                ResponseTimeMs = 0
            };
        }

        for (int attempt = 0; attempt <= MaxRetryAttempts; attempt++)
        {
            using var lease = await AcquireNodeLeaseAsync(executionSettings, requestCancellation).ConfigureAwait(false);
            var node = lease.Node;

            try
            {
                if (localOnly)
                {
                    var localityError = await GetLocalOnlyRejectionAsync(
                            node,
                            effectiveModel,
                            requestCancellation)
                        .ConfigureAwait(false);
                    if (localityError is not null)
                    {
                        stopwatch.Stop();
                        return new LlmEmbeddingResponse
                        {
                            Success = false,
                            Provider = ProviderName,
                            Model = effectiveModel,
                            ErrorMessage = localityError,
                            ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
                        };
                    }
                }

                if (expectedArtifactDigest is not null)
                {
                    var digestCheck = await GetInstalledArtifactDigestAsync(
                        node,
                        effectiveModel,
                        requestCancellation).ConfigureAwait(false);
                    if (!digestCheck.Success
                        || !string.Equals(
                            expectedArtifactDigest,
                            EmbeddingModelIdentity.NormalizeDigest(digestCheck.Digest),
                            StringComparison.Ordinal))
                    {
                        var observed = digestCheck.Digest is null ? "missing" : digestCheck.Digest;
                        var detail = digestCheck.Error is null ? string.Empty : $" ({digestCheck.Error})";
                        var digestError =
                            $"Ollama embedding artifact mismatch for '{effectiveModel}': expected {expectedArtifactDigest}, observed {observed}{detail}. " +
                            "Inference was blocked to prevent mixing vector spaces. Reinstall the approved model or start a fresh embedding rebuild with the newly approved digest.";
                        _logger.LogError("{Error}", digestError);
                        node.MarkFailure(digestError);
                        if (attempt < MaxRetryAttempts)
                        {
                            continue;
                        }
                        stopwatch.Stop();
                        return new LlmEmbeddingResponse
                        {
                            Success = false,
                            Provider = ProviderName,
                            Model = effectiveModel,
                            ErrorMessage = digestError,
                            ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
                        };
                    }
                }

                using var client = CreateHttpClient(ResolveEmbeddingTimeout(executionSettings));

                // Ollama 0.4+ exposes the supported embeddings API at
                // /api/embed with an `input` field and an `embeddings` array.
                // Keep the legacy request below only as a compatibility path
                // for older local runtimes; using the retired endpoint as the
                // primary path made every embedding call fail on current
                // Ollama builds.
                var request = new OllamaEmbedRequest
                {
                    Model = effectiveModel,
                    Input = requestText,
                    KeepAlive = ResolveKeepAlive(executionSettings, fallback: "20m"),
                    Options = new OllamaEmbeddingOptions
                    {
                        NumContext = EmbeddingModelDefaults.OllamaContextWindowTokens,
                    },
                };

                var json = JsonSerializer.Serialize(request);
                using var embedRequest = new HttpRequestMessage(HttpMethod.Post, $"{node.Endpoint}/api/embed")
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                using var response = await client.SendAsync(embedRequest, requestCancellation).ConfigureAwait(false);
                var responseContent = await response.Content.ReadAsStringAsync(requestCancellation).ConfigureAwait(false);

                // Pre-0.4 Ollama releases do not know /api/embed. Retry the
                // same request once using their documented-at-the-time API,
                // rather than asking the user to downgrade or reinstall a
                // healthy runtime.
                if (ShouldUseLegacyEmbeddingEndpoint(response.StatusCode, responseContent))
                {
                    var legacyJson = JsonSerializer.Serialize(new OllamaLegacyEmbeddingRequest
                    {
                        Model = effectiveModel,
                        Prompt = requestText,
                        KeepAlive = ResolveKeepAlive(executionSettings, fallback: "20m"),
                        Options = new OllamaEmbeddingOptions
                        {
                            NumContext = EmbeddingModelDefaults.OllamaContextWindowTokens,
                        },
                    });
                    using var legacyResponse = await client.PostAsync(
                        $"{node.Endpoint}/api/embeddings",
                        new StringContent(legacyJson, Encoding.UTF8, "application/json"),
                        requestCancellation).ConfigureAwait(false);
                    responseContent = await legacyResponse.Content.ReadAsStringAsync(requestCancellation).ConfigureAwait(false);
                    if (legacyResponse.IsSuccessStatusCode)
                    {
                        var legacy = JsonSerializer.Deserialize<OllamaLegacyEmbeddingResponse>(responseContent, SwarmJsonOptions.ExternalApi);
                        return CreateEmbeddingSuccessResponse(legacy?.Embedding, effectiveModel, requestText, stopwatch);
                    }

                    return CreateEmbeddingFailureResponse(
                        effectiveModel,
                        $"API error: {(int)legacyResponse.StatusCode} {legacyResponse.StatusCode} - {TrimApiErrorContent(responseContent, 500)}",
                        stopwatch,
                        node,
                        "legacy embedding endpoint failed");
                }

                stopwatch.Stop();

                if (response.IsSuccessStatusCode)
                {
                    if (expectedArtifactDigest is not null)
                    {
                        // Close the same-tag pull race between preflight and inference.
                        // Ollama accepts a tag, not a digest, on /api/embeddings, so the
                        // selected node must still expose the exact artifact after it
                        // produced the vector before we allow that vector to be stamped.
                        var postflight = await GetInstalledArtifactDigestAsync(
                            node,
                            effectiveModel,
                            requestCancellation).ConfigureAwait(false);
                        if (!postflight.Success
                            || !string.Equals(
                                expectedArtifactDigest,
                                EmbeddingModelIdentity.NormalizeDigest(postflight.Digest),
                                StringComparison.Ordinal))
                        {
                            var observed = postflight.Digest ?? "missing";
                            var postflightError =
                                $"Ollama embedding artifact changed during inference for '{effectiveModel}': expected {expectedArtifactDigest}, observed {observed}. The vector was discarded.";
                            node.MarkFailure(postflightError);
                            if (attempt < MaxRetryAttempts) continue;
                            return new LlmEmbeddingResponse
                            {
                                Success = false,
                                Provider = ProviderName,
                                Model = effectiveModel,
                                ErrorMessage = postflightError,
                                ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds,
                            };
                        }
                    }
                    var embeddingResponse = JsonSerializer.Deserialize<OllamaEmbeddingResponse>(responseContent, SwarmJsonOptions.ExternalApi);
                    return CreateEmbeddingSuccessResponse(embeddingResponse?.Embeddings?.FirstOrDefault(), effectiveModel, requestText, stopwatch, node);
                }

                var error = BuildApiError(response.StatusCode, responseContent);
                var resourceExhausted = IsResourceExhaustion(responseContent);
                if (IsTransientStatusCode(response.StatusCode)
                    && !resourceExhausted
                    && attempt < MaxRetryAttempts)
                {
                    if (IsEmbeddingContextOverflow(responseContent))
                    {
                        var shortened = TrimEmbeddingPrompt(requestText, FallbackEmbeddingPromptChars);
                        if (shortened.Length < requestText.Length)
                        {
                            _logger.LogWarning(
                                "Ollama embedding input exceeded context on {Node} for {Model}; retrying with shortened input ({OriginalLength}->{NewLength} chars)",
                                node.Name, effectiveModel, requestText.Length, shortened.Length);
                            requestText = shortened;
                            continue;
                        }
                    }

                    node.MarkFailure(error);
                    _logger.LogWarning(
                        "Ollama embedding transient API error on {Node}: {Status} for {Model}; retrying",
                        node.Name, response.StatusCode, effectiveModel);
                    continue;
                }

                node.MarkFailure(error);
                _logger.LogError("Ollama embedding error on {Node}: {StatusCode} - {Content}", node.Name, response.StatusCode, responseContent);
                return new LlmEmbeddingResponse
                {
                    Success = false,
                    Provider = ProviderName,
                    Model = effectiveModel,
                    ErrorMessage = error,
                    ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds
                };
            }
            catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
            {
                // A cancelled repair/indexing job says nothing about node health.
                // Do not cool down the node or start a retry with a cancelled token.
                throw;
            }
            catch (Exception ex) when (IsTransientError(ex) && attempt < MaxRetryAttempts)
            {
                node.MarkFailure(ex.Message);
                _logger.LogWarning(
                    "Ollama embedding transient error on {Node} (attempt {Attempt}/{MaxAttempts}): {Message}",
                    node.Name, attempt + 1, MaxRetryAttempts + 1, ex.Message);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                node.MarkFailure(ex.Message);
                _logger.LogError(ex, "Error generating embedding with Ollama on {Node}", node.Name);
                return new LlmEmbeddingResponse
                {
                    Success = false,
                    Provider = ProviderName,
                    Model = effectiveModel,
                    ErrorMessage = ex.Message,
                    ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds
                };
            }
        }

        stopwatch.Stop();
        return new LlmEmbeddingResponse
        {
            Success = false,
            Provider = ProviderName,
            Model = effectiveModel,
            ErrorMessage = $"Ollama embedding failed after {MaxRetryAttempts + 1} attempts",
            ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds
        };
    }

    private async Task<(bool Success, string? Digest, string? Error)> GetInstalledArtifactDigestAsync(
        OllamaNodeState node,
        string model,
        CancellationToken ct,
        bool forceRefresh = false)
    {
        var normalizedModel = NormalizeOllamaModelName(model);
        var key = $"{node.Endpoint}|{normalizedModel}";
        if (!forceRefresh
            && _artifactDigestCache.TryGetValue(key, out var cached)
            && cached.ExpiresAtUtc > DateTime.UtcNow)
        {
            return (true, cached.Digest, null);
        }

        try
        {
            using var client = CreateHttpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await client.GetAsync(
                $"{node.Endpoint}/api/tags",
                timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (false, null, $"/api/tags returned {(int)response.StatusCode}");

            var content = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var tags = JsonSerializer.Deserialize<OllamaTagsResponse>(content, SwarmJsonOptions.ExternalApi);
            var installed = tags?.Models?.FirstOrDefault(candidate =>
                string.Equals(
                    NormalizeOllamaModelName(candidate.Name),
                    normalizedModel,
                    StringComparison.OrdinalIgnoreCase));
            var digest = EmbeddingModelIdentity.NormalizeDigest(installed?.Digest);
            if (digest is null)
                return (false, null, installed is null ? "model is absent from /api/tags" : "model digest is absent from /api/tags");

            if (_artifactDigestCache.Count >= ArtifactDigestCacheCapacity)
            {
                var victim = _artifactDigestCache.Keys.FirstOrDefault();
                if (victim is not null) _artifactDigestCache.TryRemove(victim, out _);
            }
            _artifactDigestCache[key] = new ArtifactDigestCacheEntry(
                digest,
                DateTime.UtcNow.Add(ArtifactDigestCacheTtl));
            return (true, digest, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    private static string NormalizeOllamaModelName(string model)
    {
        var normalized = model.Trim();
        return normalized.Contains(':', StringComparison.Ordinal)
            ? normalized
            : normalized + ":latest";
    }

    private async Task<bool> EnsurePinnedEmbeddingModelAvailableAsync(
        string effectiveModel,
        CancellationToken ct)
    {
        await _embeddingModelInstallLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ClearModelCache();
            var models = await GetAvailableModelsAsync(ct).ConfigureAwait(false);
            if (models.Any(m => m.StartsWith(effectiveModel, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            // A missing local runtime is not a model-install operation. Probe first
            // so clean installs that deliberately omitted Ollama do not advertise a
            // phantom pull, emit avoidable errors, or leave a stuck activity chip.
            if (!await HasReachableNodeAsync(ct).ConfigureAwait(false))
            {
                _logger.LogDebug(
                    "Skipping automatic embedding pull for {Model} because no Ollama node is reachable",
                    effectiveModel);
                return false;
            }

            using var activity = _localRuntimeManager?.TrackActivity(new LocalLlmRuntimeActivity(
                ProviderName,
                effectiveModel,
                LocalLlmWorkloadKind.Embedding,
                LocalLlmRuntimeActivityKind.ModelInstall,
                $"Pulling missing pinned embedding model {effectiveModel}",
                DateTimeOffset.UtcNow));

            var pulled = await EnsureModelAsync(effectiveModel, ct: ct).ConfigureAwait(false);
            if (!pulled)
            {
                _logger.LogWarning("Automatic pull failed for pinned Ollama embedding model {Model}", effectiveModel);
                return false;
            }

            ClearModelCache();
            models = await GetAvailableModelsAsync(ct).ConfigureAwait(false);
            var available = models.Any(m => m.StartsWith(effectiveModel, StringComparison.OrdinalIgnoreCase));
            if (available)
            {
                _logger.LogInformation("Pinned Ollama embedding model {Model} is now installed", effectiveModel);
            }
            else
            {
                _logger.LogWarning("Ollama reported successful pull for {Model}, but the model was not visible in /api/tags after refresh", effectiveModel);
            }

            return available;
        }
        finally
        {
            _embeddingModelInstallLock.Release();
        }
    }

    private void ClearModelCache()
    {
        _cachedModels = null;
        _detectedDefaultModel = null;
        _detectedEmbeddingModel = null;
        _lastModelCheck = DateTime.MinValue;
        _artifactDigestCache.Clear();
    }

    public async Task<List<LlmEmbeddingResponse>> GenerateEmbeddingsAsync(
        IList<string> texts,
        string model,
        Dictionary<string, object>? executionSettings = null)
    {
        // Ollama doesn't support batch embeddings natively, so we process sequentially
        var results = new List<LlmEmbeddingResponse>();

        foreach (var text in texts)
        {
            var result = await GenerateEmbeddingAsync(text, model, executionSettings).ConfigureAwait(false);
            results.Add(result);
        }

        return results;
    }

    /// <inheritdoc/>
    public async Task PreloadModelAsync(string model, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(model))
            return;
        if (!LocalModelArtifactCompatibility.IsCompatibleWithCurrentProcess(model))
        {
            _logger.LogWarning(
                "Refused to preload Ollama model {Model} because its artifact format is incompatible with this computer",
                model);
            return;
        }

        try
        {
            using var lease = await AcquireNodeLeaseAsync(executionSettings: null, ct).ConfigureAwait(false);
            using var client = CreateHttpClient();

            // Sending an empty generate request loads the model into VRAM
            // without producing output. keep_alive keeps it resident.
            var request = new Dictionary<string, object>
            {
                ["model"] = model,
                ["keep_alive"] = "10m"
            };
            // Load at the window its requests will use; otherwise the next
            // request reloads it.
            if (TryGetContextWindowGrant(model, out var window))
            {
                request["options"] = new Dictionary<string, object> { ["num_ctx"] = window };
            }

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync(
                $"{lease.Node.Endpoint}/api/generate", content, ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                // Drain the response stream (Ollama sends back a done message)
                await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                lease.Node.MarkSuccess();
                _logger.LogInformation("Preloaded model {Model} into Ollama VRAM", model);
            }
            else
            {
                _logger.LogWarning("Failed to preload model {Model}: {Status}",
                    model, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to preload model {Model}", model);
        }
    }

    public Task<LlmResponse> GenerateStreamingResponseAsync(
        string prompt,
        string model,
        Func<string, Task> onChunk,
        Dictionary<string, object>? executionSettings = null)
    {
        throw new NotSupportedException("Streaming not yet implemented for Ollama provider");
    }

    /// <summary>
    /// Check if Ollama has an embedding model available
    /// </summary>
    public async Task<(bool Available, string? ModelName, string? Message)> CheckEmbeddingModelAsync()
    {
        var models = await GetAvailableModelsAsync().ConfigureAwait(false);

        var found = models.FirstOrDefault(EmbeddingModelDefaults.IsPinnedModel);
        if (found != null)
        {
            return (true, found, null);
        }

        return (false, null, $"No embedding model found. Install it with: ollama pull {EmbeddingModelDefaults.Model}");
    }

    /// <summary>
    /// Pull a model if not already available.
    /// Uses streaming to read progress from Ollama's /api/pull endpoint.
    /// </summary>
    public async Task<bool> EnsureModelAsync(
        string model,
        string? preferredNodeName = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        var models = await GetAvailableModelsAsync(ct).ConfigureAwait(false);

        if (models.Any(m => m.StartsWith(model, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogInformation("Ollama model {Model} already available", model);
            return true;
        }

        if (!await HasReachableNodeAsync(ct).ConfigureAwait(false))
        {
            _logger.LogDebug(
                "Skipping automatic pull for {Model} because no Ollama node is reachable",
                model);
            return false;
        }

        _logger.LogInformation("Pulling Ollama model {Model}... (this may take a few minutes)", model);

        // For pull operations, probe for a healthy node first.
        var orderedNodes = GetOrderedNodesForRouting(includeCoolingDownNodes: true);
        var targetNode = !string.IsNullOrWhiteSpace(preferredNodeName)
            ? orderedNodes.FirstOrDefault(n => string.Equals(n.Name, preferredNodeName, StringComparison.OrdinalIgnoreCase))
                ?? orderedNodes.FirstOrDefault()
            : orderedNodes.FirstOrDefault();
        if (targetNode is null)
        {
            _logger.LogError("No Ollama nodes configured for model pull");
            return false;
        }

        try
        {
            using var client = CreateHttpClient();
            client.Timeout = TimeSpan.FromMinutes(30); // Model pull can take time

            var request = new { name = model };
            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            // Use ResponseHeadersRead to start streaming immediately
            using var response = await client.SendAsync(
                new HttpRequestMessage(HttpMethod.Post, $"{targetNode.Endpoint}/api/pull")
                {
                    Content = content
                },
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to pull model {Model} from {Node}: {StatusCode}", model, targetNode.Name, response.StatusCode);
                return false;
            }

            // Read the streaming response (Ollama sends JSON objects line by line)
            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            string? lastStatus = null;
            int lastPercent = -1;
            string? line;

            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
            {
                if (string.IsNullOrEmpty(line)) continue;

                try
                {
                    var progress = JsonSerializer.Deserialize<OllamaPullProgress>(line, SwarmJsonOptions.ExternalApi);
                    if (progress != null)
                    {
                        // Log progress updates (but not too frequently)
                        var currentPercent = progress.Completed > 0 && progress.Total > 0
                            ? (int)(100.0 * progress.Completed / progress.Total)
                            : -1;

                        if (progress.Status != lastStatus || (currentPercent >= 0 && currentPercent != lastPercent && currentPercent % 25 == 0))
                        {
                            if (currentPercent >= 0)
                            {
                                _logger.LogInformation("Pulling {Model} on {Node}: {Status} ({Percent}%)",
                                    model, targetNode.Name, progress.Status, currentPercent);
                            }
                            else
                            {
                                _logger.LogInformation("Pulling {Model} on {Node}: {Status}", model, targetNode.Name, progress.Status);
                            }
                            lastStatus = progress.Status;
                            lastPercent = currentPercent;
                        }
                    }
                }
                catch (JsonException)
                {
                    // Ignore malformed progress updates
                }
            }

            _logger.LogInformation("Successfully pulled model {Model} on {Node}", model, targetNode.Name);
            _cachedModels = null; // Invalidate cache
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pulling model {Model} on {Node}", model, targetNode.Name);
            return false;
        }
    }

    /// <summary>
    /// Pull an embedding model if not already available.
    /// </summary>
    public Task<bool> EnsureEmbeddingModelAsync(string model = EmbeddingModelDefaults.Model)
        => EnsureModelAsync(EmbeddingModelDefaults.Model);

    private async Task<bool> HasReachableNodeAsync(CancellationToken ct)
    {
        foreach (var node in GetOrderedNodesForRouting(includeCoolingDownNodes: true))
        {
            var (success, _, error) = await GetModelsFromNodeAsync(
                node,
                TimeSpan.FromSeconds(5),
                ct).ConfigureAwait(false);
            if (success)
            {
                node.MarkSuccess();
                return true;
            }

            node.MarkFailure(error ?? "connectivity probe failed");
        }

        return false;
    }

    public async Task<(bool IsReady, string Message)> CheckRuntimeReadinessAsync(
        string model,
        CancellationToken cancellationToken = default)
    {
        var reachable = await HasReachableNodeAsync(cancellationToken).ConfigureAwait(false);
        return reachable
            ? (true, "Ollama is reachable")
            : (false, "Ollama is not running or no configured Ollama node is reachable");
    }

    /// <summary>
    /// Maximum time to wait for Ollama to return HTTP response headers.
    /// If headers don't arrive within this window the node is unresponsive.
    /// </summary>
    private const int DefaultHeadersTimeoutSeconds = 30;

    private HttpClient CreateHttpClient(TimeSpan? timeout = null)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = timeout ?? TimeSpan.FromSeconds(_options.TimeoutSeconds);
        return client;
    }

    private bool UseStreaming => _options.StreamInactivityTimeoutSeconds > 0;

    /// <summary>
    /// Reads an Ollama streaming response (newline-delimited JSON), updating node
    /// activity on every chunk so the queue and callers know the model is alive.
    /// Throws <see cref="TimeoutException"/> if no data arrives within the
    /// configured inactivity window — the model is treated as hung.
    /// </summary>
    private async Task<(string Content, string? Thinking, OllamaChatResponse? FinalChunk)> ReadStreamingResponseAsync(
        HttpResponseMessage response,
        OllamaNodeState node,
        CancellationToken requestCancellation = default)
    {
        var inactivityTimeout = TimeSpan.FromSeconds(
            Math.Clamp(_options.StreamInactivityTimeoutSeconds, 5, 3600));

        using var stream = await response.Content
            .ReadAsStreamAsync(requestCancellation)
            .ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        var content = new StringBuilder();
        var thinking = new StringBuilder();
        OllamaChatResponse? finalChunk = null;

        while (true)
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(requestCancellation);
            readCts.CancelAfter(inactivityTimeout);

            string? line;
            try
            {
                line = await reader.ReadLineAsync(readCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    $"Ollama node '{node.Name}' stopped sending data for " +
                    $"{inactivityTimeout.TotalSeconds}s — model may be hung. " +
                    "Consider restarting Ollama or increasing StreamInactivityTimeoutSeconds.");
            }

            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            node.MarkActivity();

            var chunk = JsonSerializer.Deserialize<OllamaChatResponse>(line, SwarmJsonOptions.ExternalApi);
            if (chunk?.Message?.Content != null)
                content.Append(chunk.Message.Content);
            if (!string.IsNullOrEmpty(chunk?.Message?.Thinking))
                thinking.Append(chunk.Message.Thinking);

            if (chunk?.Done == true)
            {
                finalChunk = chunk;
                break;
            }
        }

        var thinkingText = thinking.Length == 0 ? null : thinking.ToString();
        return (content.ToString(), thinkingText, finalChunk);
    }

    /// <summary>Convert an Ollama nanosecond duration to whole milliseconds, or null when absent.</summary>
    private static int? NsToMs(long? nanoseconds) =>
        nanoseconds is null ? null : (int)Math.Min(int.MaxValue, nanoseconds.Value / 1_000_000);

    private async Task<string> GetEffectiveModelAsync(string model)
    {
        if (!string.IsNullOrEmpty(model))
        {
            if (!LocalModelArtifactCompatibility.IsCompatibleWithCurrentProcess(model))
            {
                throw new InvalidOperationException(
                    $"Ollama model artifact '{model}' is not compatible with this computer.");
            }

            return model;
        }

        if (!string.IsNullOrEmpty(_options.DefaultModel)
            && LocalModelArtifactCompatibility.IsCompatibleWithCurrentProcess(_options.DefaultModel))
            return _options.DefaultModel;

        if (_detectedDefaultModel != null
            && LocalModelArtifactCompatibility.IsCompatibleWithCurrentProcess(_detectedDefaultModel))
            return _detectedDefaultModel;

        // Try to detect a model
        var models = await GetAvailableModelsAsync().ConfigureAwait(false);
        return models.FirstOrDefault(LocalModelArtifactCompatibility.IsCompatibleWithCurrentProcess)
               ?? "llama3.2";
    }

    private async Task<OllamaNodeLease> AcquireNodeLeaseAsync(
        Dictionary<string, object>? executionSettings,
        CancellationToken ct = default)
    {
        var waitTimeoutSeconds = TryGetInt(executionSettings, "ollama_queue_wait_seconds")
            ?? _options.QueueWaitTimeoutSeconds;
        waitTimeoutSeconds = Math.Clamp(waitTimeoutSeconds, 1, 3600);

        // The configured timeout only governs how long we wait when nodes are IDLE/SILENT.
        // When nodes are actively streaming tokens we wait indefinitely — the work will finish.
        var idleDeadline = DateTime.UtcNow.AddSeconds(waitTimeoutSeconds);

        // How recently a node must have streamed a chunk to count as "alive"
        var activityRecency = TimeSpan.FromSeconds(30);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var ordered = GetOrderedNodesForRouting(includeCoolingDownNodes: false);
            if (ordered.Count == 0)
            {
                // All nodes are cooling down; allow retries against the least-loaded node as a safety valve.
                ordered = GetOrderedNodesForRouting(includeCoolingDownNodes: true);
            }

            foreach (var node in ordered)
            {
                if (node.TryAcquire())
                {
                    return new OllamaNodeLease(node);
                }
            }

            var now = DateTime.UtcNow;
            var anyActive = UseStreaming
                && _nodes.Any(n => n.HasRecentActivity(activityRecency));

            if (anyActive)
            {
                // Nodes are alive and producing tokens — reset the idle clock and keep waiting.
                idleDeadline = now.AddSeconds(waitTimeoutSeconds);
            }
            else if (now >= idleDeadline)
            {
                // No node has shown streaming activity for the entire timeout window.
                throw new TimeoutException(
                    $"All Ollama nodes are busy with no streaming activity for {waitTimeoutSeconds}s — " +
                    "nodes may be hung or overloaded. Consider restarting Ollama.");
            }

            await Task.Delay(120, ct).ConfigureAwait(false);
        }
    }

    private List<OllamaNodeState> GetOrderedNodesForRouting(bool includeCoolingDownNodes)
    {
        var cooldown = TimeSpan.FromSeconds(Math.Clamp(_options.NodeFailureCooldownSeconds, 1, 3600));
        var candidates = _nodes
            .Where(n => includeCoolingDownNodes || !n.IsCoolingDown(cooldown))
            .OrderBy(n => n.Utilization)
            .ThenBy(n => n.ConsecutiveFailures)
            .ThenByDescending(n => n.LastSuccessUtc)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return candidates;
    }

    private async Task<(bool Success, List<string> Models, string? Error)> GetModelsFromNodeAsync(
        OllamaNodeState node,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        try
        {
            using var client = CreateHttpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var response = await client.GetAsync($"{node.Endpoint}/api/tags", cts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return (false, new List<string>(), $"HTTP {(int)response.StatusCode} {response.StatusCode}");
            }

            var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var tagsResponse = JsonSerializer.Deserialize<OllamaTagsResponse>(content, SwarmJsonOptions.ExternalApi);
            var models = tagsResponse?.Models?.Select(m => m.Name).Where(m => !string.IsNullOrWhiteSpace(m)).ToList()
                ?? new List<string>();

            return (true, models, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return (false, new List<string>(), "timeout");
        }
        catch (HttpRequestException ex)
        {
            return (false, new List<string>(), ex.Message);
        }
        catch (Exception ex)
        {
            return (false, new List<string>(), ex.Message);
        }
    }

    private async Task RefreshVisionCapabilitiesAsync(
        OllamaNodeState node,
        IReadOnlyCollection<string> models,
        CancellationToken ct)
    {
        if (models.Count == 0)
        {
            return;
        }

        // /api/show is local metadata and normally returns in milliseconds. Keep
        // it bounded so a broken/old runtime never turns model discovery into a
        // UI stall. Missing capability metadata simply leaves the family fallback
        // in effect.
        using var refreshCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        refreshCts.CancelAfter(TimeSpan.FromSeconds(3));

        try
        {
            await Parallel.ForEachAsync(
                models.Distinct(StringComparer.OrdinalIgnoreCase),
                new ParallelOptions
                {
                    CancellationToken = refreshCts.Token,
                    MaxDegreeOfParallelism = 8,
                },
                async (model, cancellationToken) =>
                {
                    var metadata = await TryGetModelMetadataAsync(
                            node,
                            model,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (metadata?.Capabilities is not null)
                    {
                        var normalizedModel = model.Trim();
                        _visionCapabilityCache[normalizedModel] = metadata.Capabilities.Any(capability =>
                            capability.Equals("vision", StringComparison.OrdinalIgnoreCase));
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug(
                "Ollama capability refresh was bounded or cancelled on {Node}; family fallback remains available",
                node.Name);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Ollama capability refresh failed on {Node}; family fallback remains available",
                node.Name);
        }
    }

    private async Task<OllamaShowResponse?> TryGetModelMetadataAsync(
        OllamaNodeState node,
        string model,
        CancellationToken ct,
        TimeSpan? timeout = null)
    {
        try
        {
            using var client = CreateHttpClient(timeout ?? TimeSpan.FromSeconds(2));
            var requestJson = JsonSerializer.Serialize(new OllamaShowRequest { Model = model });
            using var response = await client.PostAsync(
                    $"{node.Endpoint}/api/show",
                    new StringContent(requestJson, Encoding.UTF8, "application/json"),
                    ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<OllamaShowResponse>(content, SwarmJsonOptions.ExternalApi);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string?> GetLocalOnlyRejectionAsync(
        OllamaNodeState node,
        string model,
        CancellationToken ct)
    {
        if (OllamaModelLocalityPolicy.IsCloudBacked(model))
        {
            return $"Ollama cloud-backed model '{model}' cannot satisfy LocalOnly privacy.";
        }

        var metadata = await TryGetModelMetadataAsync(node, model, ct, RequestMetadataTimeout)
            .ConfigureAwait(false);
        if (metadata is null)
        {
            return $"Ollama could not prove that model '{model}' executes on this device; LocalOnly inference was blocked.";
        }

        if (OllamaModelLocalityPolicy.IsCloudBacked(model, metadata.RemoteHost))
        {
            return $"Ollama model '{model}' is cloud-backed and cannot satisfy LocalOnly privacy.";
        }

        if (!metadata.HasRecognizedMetadata)
        {
            return $"Ollama returned incomplete locality metadata for model '{model}'; LocalOnly inference was blocked.";
        }

        return null;
    }

    private static List<OllamaNodeState> BuildNodeStates(OllamaProviderOptions options)
    {
        var result = new List<OllamaNodeState>();

        if (options.Nodes is { Count: > 0 })
        {
            var i = 1;
            foreach (var configured in options.Nodes)
            {
                if (string.IsNullOrWhiteSpace(configured.Endpoint))
                {
                    continue;
                }

                var normalizedEndpoint = NormalizeEndpoint(configured.Endpoint);
                var name = string.IsNullOrWhiteSpace(configured.Name) ? $"node-{i}" : configured.Name.Trim();
                var concurrency = Math.Clamp(configured.MaxConcurrentRequests, 1, 64);
                result.Add(new OllamaNodeState(name, normalizedEndpoint, concurrency));
                i++;
            }
        }

        if (result.Count == 0)
        {
            var normalizedEndpoint = NormalizeEndpoint(options.Endpoint);
            result.Add(new OllamaNodeState("node-1", normalizedEndpoint, 1));
        }

        return result;
    }

    private static string NormalizeEndpoint(string endpoint)
    {
        var trimmed = (endpoint ?? string.Empty).Trim();
        if (trimmed.EndsWith('/'))
        {
            trimmed = trimmed.TrimEnd('/');
        }

        return string.IsNullOrWhiteSpace(trimmed) ? "http://localhost:11434" : trimmed;
    }

    private static int? TryGetInt(Dictionary<string, object>? settings, string key)
    {
        if (settings == null || !settings.TryGetValue(key, out var raw) || raw == null)
        {
            return null;
        }

        return raw switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            float f => (int)f,
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => null
        };
    }

    private static int ResolveHeadersTimeoutSeconds(Dictionary<string, object>? executionSettings) =>
        Math.Clamp(
            TryGetInt(executionSettings, "ollama_headers_timeout_seconds") ?? DefaultHeadersTimeoutSeconds,
            1,
            3600);

    private TimeSpan ResolveEmbeddingTimeout(Dictionary<string, object>? executionSettings)
    {
        var seconds = TryGetInt(executionSettings, "ollama_embedding_timeout_seconds")
            ?? Math.Min(_options.TimeoutSeconds, 30);
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 3600));
    }

    private static void ApplyExecutionSettings(OllamaChatRequest request, Dictionary<string, object>? executionSettings)
    {
        if (executionSettings == null) return;

        if (executionSettings.TryGetValue("temperature", out var temp))
            request.Options["temperature"] = Convert.ToDouble(temp);

        if (executionSettings.TryGetValue("top_p", out var topP))
            request.Options["top_p"] = Convert.ToDouble(topP);

        if (executionSettings.TryGetValue("seed", out var seed))
            request.Options["seed"] = Convert.ToInt32(seed);

        if (executionSettings.TryGetValue("num_predict", out var numPredict))
            request.Options["num_predict"] = Convert.ToInt32(numPredict);

        if (executionSettings.TryGetValue("max_tokens", out var maxTokens))
            request.Options["num_predict"] = Convert.ToInt32(maxTokens);

        if (executionSettings.TryGetValue("num_gpu", out var gpuLayers))
            request.Options["num_gpu"] = Convert.ToInt32(gpuLayers);

        if (executionSettings.TryGetValue("keep_alive", out var keepAlive))
            request.KeepAlive = keepAlive?.ToString();

        var stream = TryGetBool(executionSettings, "ollama_stream")
            ?? TryGetBool(executionSettings, "stream");
        if (stream.HasValue)
            request.Stream = stream.Value;

        if (executionSettings.TryGetValue("format", out var format)
            && !string.IsNullOrWhiteSpace(format?.ToString()))
        {
            request.Format = format.ToString();
        }
        else if (executionSettings.TryGetValue("response_format", out var responseFormat)
                 && IsJsonObjectResponseFormat(responseFormat))
        {
            request.Format = "json";
        }

        var think = TryGetBool(executionSettings, "think")
            ?? TryGetBool(executionSettings, "ollama_think");
        if (think.HasValue)
            request.Think = think.Value;
    }

    private static bool? TryGetBool(Dictionary<string, object>? settings, string key)
    {
        if (settings == null || !settings.TryGetValue(key, out var raw) || raw == null)
        {
            return null;
        }

        return raw switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            int i => i != 0,
            long l => l != 0,
            _ => null
        };
    }

    private static Dictionary<string, object> ForceOllamaThinkingOff(Dictionary<string, object>? executionSettings)
    {
        var forced = executionSettings is null
            ? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object>(executionSettings, StringComparer.OrdinalIgnoreCase);
        forced["think"] = false;
        forced["ollama_think"] = false;
        return forced;
    }

    private static List<string> BuildResponseDiagnostics(
        string? content, string? reasoningContent, int? outputTokens, bool cpuGrammarRecovery = false,
        bool contextWindowResized = false)
    {
        var diagnostics = new List<string>();
        if (cpuGrammarRecovery)
            diagnostics.Add("ollama_cpu_grammar_recovery");
        if (contextWindowResized)
            diagnostics.Add("ollama_context_window_resized");
        if (!string.IsNullOrWhiteSpace(reasoningContent))
        {
            diagnostics.Add(string.IsNullOrWhiteSpace(content)
                ? "thinking_channel_without_content"
                : "thinking_channel_present");
        }

        if (string.IsNullOrWhiteSpace(content) && outputTokens > 0)
        {
            diagnostics.Add("empty_response_with_output_tokens");
        }

        return diagnostics;
    }

    // A grammar-stack exception is deterministic for the failed execution path,
    // not a transient HTTP outage. Some GPU backends produce reserved tokens
    // where the same artifact on CPU obeys the original JSON grammar. Keep the
    // prompt, model, grammar, sampling and privacy controls intact; try CPU once.
    private static bool IsGrammarRuntimeError(HttpStatusCode status, string content)
        => status == HttpStatusCode.InternalServerError
           && content.Contains("Unexpected empty grammar stack after accepting piece", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, object>? PrepareCpuGrammarRecovery(
        OllamaChatRequest request, Dictionary<string, object>? settings)
    {
        // An explicit placement (including a previous CPU attempt) is binding.
        if (request.Format != "json" || request.Options.ContainsKey("num_gpu"))
            return null;
        var recovered = settings is null
            ? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object>(settings, StringComparer.OrdinalIgnoreCase);
        recovered["num_gpu"] = 0;
        return recovered;
    }

    private static string CpuGrammarRecoveryKey(
        OllamaNodeState node, OllamaChatRequest request, Dictionary<string, object>? settings)
        => $"{node.Endpoint}|{request.Model}|{LlmReservedExecutionSettings.GetExpectedModelArtifactDigest(settings)}";

    private bool ApplyRememberedCpuGrammarRecovery(
        OllamaNodeState node, OllamaChatRequest request, Dictionary<string, object>? settings)
    {
        if (request.Format != "json" || request.Options.ContainsKey("num_gpu"))
            return false;
        var key = CpuGrammarRecoveryKey(node, request, settings);
        if (!_cpuGrammarRecoveryUntil.TryGetValue(key, out var until))
            return false;
        if (until <= DateTime.UtcNow)
        {
            _cpuGrammarRecoveryUntil.TryRemove(key, out _);
            return false;
        }
        request.Options["num_gpu"] = 0;
        return true;
    }

    private bool CompleteCpuGrammarRecovery(
        OllamaNodeState node, OllamaChatRequest request, Dictionary<string, object>? settings,
        string content, out string? error)
    {
        var key = CpuGrammarRecoveryKey(node, request, settings);
        try
        {
            // A 200 containing reserved-token debris must not establish a
            // successful workaround. The router still validates the caller's
            // typed schema and the application still validates its semantics.
            using var json = JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            _cpuGrammarRecoveryUntil.TryRemove(key, out _);
            error = "Ollama CPU grammar recovery returned invalid JSON; the response was discarded.";
            return false;
        }
        if (_cpuGrammarRecoveryUntil.Count >= 128)
            _cpuGrammarRecoveryUntil.Clear();
        // Fixed, not sliding expiry: a busy model still periodically rechecks
        // its normal backend. No persistent model/provider configuration changes.
        _cpuGrammarRecoveryUntil.TryAdd(key, DateTime.UtcNow + CpuGrammarRecoveryLifetime);
        error = null;
        return true;
    }

    /// <summary>
    /// Every native chat request forbids truncation, so a prompt longer than
    /// the runner's window comes back as a sized error rather than being cut
    /// down to whatever fits. A model whose window this adapter grew keeps it.
    /// </summary>
    private void ApplyContextWindow(OllamaChatRequest request)
    {
        request.Truncate = false;
        if (TryGetContextWindowGrant(request.Model, out var window))
        {
            request.Options["num_ctx"] = window;
        }
    }

    private bool TryGetContextWindowGrant(string model, out int windowTokens)
    {
        windowTokens = 0;
        var key = NormalizeOllamaModelName(model);
        if (!_contextWindowGrants.TryGetValue(key, out var grant))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        if (grant.ExpiresAtUtc <= now)
        {
            _contextWindowGrants.TryRemove(new KeyValuePair<string, ContextWindowGrant>(key, grant));
            return false;
        }

        // Idle expiry: a model in steady use keeps its window, while one left
        // unused eventually returns to whatever the runtime defaults to.
        _contextWindowGrants.TryUpdate(
            key,
            grant with { ExpiresAtUtc = now + ContextWindowGrantIdleLifetime },
            grant);
        windowTokens = grant.WindowTokens;
        return true;
    }

    private void GrantContextWindow(string model, int windowTokens)
    {
        var now = DateTime.UtcNow;
        if (_contextWindowGrants.Count >= ContextWindowGrantCapacity)
        {
            var oldest = _contextWindowGrants.MinBy(entry => entry.Value.ExpiresAtUtc);
            _contextWindowGrants.TryRemove(oldest.Key, out _);
        }

        var granted = new ContextWindowGrant(windowTokens, now + ContextWindowGrantIdleLifetime);
        // Concurrent resizes of one model keep the larger window.
        _contextWindowGrants.AddOrUpdate(
            NormalizeOllamaModelName(model),
            granted,
            (_, existing) => existing.WindowTokens >= windowTokens
                ? existing with { ExpiresAtUtc = granted.ExpiresAtUtc }
                : granted);
    }

    /// <summary>
    /// When the runtime reports that a request did not fit its window, grants
    /// the model a window that holds the prompt and the output budget, within
    /// its trained length and memory envelope, and asks for one retry at it.
    /// A request no allowed window can hold fails here instead of truncating.
    /// Any other response is left to the caller's ordinary error handling.
    /// </summary>
    private async Task<ContextOverflowResolution> ResolveContextOverflowAsync(
        OllamaNodeState node,
        OllamaChatRequest request,
        string prompt,
        int imageCount,
        HttpStatusCode status,
        string responseContent,
        bool alreadyResized,
        CancellationToken ct)
    {
        if (!OllamaContextWindowSizing.TryReadOverflow(
                status,
                responseContent,
                out var promptTokens,
                out var reportedWindow))
        {
            if (TryGetInt(request.Options, "num_ctx") is not int failedWindow
                || !IsWindowLoadMemoryFailure(responseContent))
            {
                return default;
            }

            // The device could not hold the larger window right now. Later
            // requests go back to the runtime's default window instead of
            // retrying a load that does not fit.
            BlockContextWindowGrowth(request.Model);
            _logger.LogWarning(
                "Ollama could not load {Model} on {Node} with a {Window:N0}-token window; its requests return to the runtime's default window for {Minutes:N0} minutes",
                request.Model,
                node.Name,
                failedWindow,
                ContextWindowGrowthBlockLifetime.TotalMinutes);
            // A window kept from an earlier request may not be one this request
            // needs, so retry it once at the default. A window this request was
            // just grown to is one it needs.
            return alreadyResized
                ? new ContextOverflowResolution(
                    Retry: false,
                    Error: $"Ollama could not load '{request.Model}' with the {failedWindow:N0}-token context window this request needs: {TrimApiErrorContent(responseContent, 300)}")
                : new ContextOverflowResolution(Retry: true, Error: null);
        }

        // The runtime rejected an over-window request instead of cutting it.
        RecordRuntimeServesWhole(node);
        var outputTokens = TryGetInt(request.Options, "num_predict");
        var required = OllamaContextWindowSizing.RequiredTokens(promptTokens, prompt, imageCount, outputTokens);
        var sentWindow = TryGetInt(request.Options, "num_ctx");
        var currentWindow = reportedWindow ?? sentWindow;
        if (sentWindow is null && reportedWindow is int defaultWindow)
        {
            // A request that set no window was served at the model's default,
            // the window every caller that cannot set one also gets.
            _contextWindows?.RecordDefaultWindow(ProviderName, request.Model, defaultWindow);
        }

        OllamaContextWindowPlan plan;
        if (IsContextWindowGrowthBlocked(request.Model))
        {
            plan = new(null, "a larger window for this model failed to load on this device recently");
        }
        else if (alreadyResized)
        {
            plan = new(null, $"it still needs about {required:N0} tokens after one resize");
        }
        else
        {
            var metadata = await TryGetModelMetadataAsync(node, request.Model, ct, RequestMetadataTimeout)
                .ConfigureAwait(false);
            var artifactBytes = await GetInstalledArtifactBytesAsync(node, request.Model, ct)
                .ConfigureAwait(false);
            var profile = OllamaContextWindowSizing.ReadProfile(metadata?.ModelInfo);
            plan = OllamaContextWindowSizing.Plan(required, currentWindow, profile, artifactBytes,
                requiredIsUpperBound: promptTokens is null);
            // This runtime serves the model whole up to its largest window,
            // whatever version it reports; Model Scout ranks the route by it.
            if (profile is not null
                && OllamaContextWindowSizing.MaximumWindow(profile, artifactBytes) is int largest)
            {
                _contextWindows?.RecordMaximumWindow(ProviderName, request.Model, largest);
            }
        }

        if (plan.WindowTokens is int window)
        {
            GrantContextWindow(request.Model, window);
            _logger.LogInformation(
                "Ollama window for {Model} on {Node} is {Current} tokens and this request needs about {Required:N0}; reloading the model at {Window:N0} tokens",
                request.Model,
                node.Name,
                currentWindow?.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) ?? "an unreported number of",
                required,
                window);
            return new ContextOverflowResolution(Retry: true, Error: null);
        }

        _logger.LogWarning(
            "Ollama cannot serve a request to {Model} on {Node} whole: {Reason}",
            request.Model,
            node.Name,
            plan.Reason);
        return new ContextOverflowResolution(
            Retry: false,
            Error: $"Ollama context window for '{request.Model}' cannot hold this request without truncating it: {plan.Reason}.");
    }

    private async Task<long> GetInstalledArtifactBytesAsync(
        OllamaNodeState node,
        string model,
        CancellationToken ct)
    {
        try
        {
            using var client = CreateHttpClient(RequestMetadataTimeout);
            using var response = await client.GetAsync($"{node.Endpoint}/api/tags", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return 0;
            }

            var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var tags = JsonSerializer.Deserialize<OllamaTagsResponse>(content, SwarmJsonOptions.ExternalApi);
            var normalized = NormalizeOllamaModelName(model);
            return tags?.Models?.FirstOrDefault(candidate => string.Equals(
                           NormalizeOllamaModelName(candidate.Name),
                           normalized,
                           StringComparison.OrdinalIgnoreCase))?.Size
                   ?? 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read the installed size of {Model} on {Node}", model, node.Name);
            return 0;
        }
    }

    private void BlockContextWindowGrowth(string model)
    {
        var key = NormalizeOllamaModelName(model);
        _contextWindowGrants.TryRemove(key, out _);
        if (_contextWindowGrowthBlockedUntil.Count >= ContextWindowGrantCapacity)
        {
            _contextWindowGrowthBlockedUntil.Clear();
        }

        _contextWindowGrowthBlockedUntil[key] = DateTime.UtcNow + ContextWindowGrowthBlockLifetime;
        // Model Scout must not send this model requests only a grown window holds.
        _contextWindows?.SuspendMaximumWindow(ProviderName, model, ContextWindowGrowthBlockLifetime);
    }

    private bool IsContextWindowGrowthBlocked(string model)
    {
        var key = NormalizeOllamaModelName(model);
        if (!_contextWindowGrowthBlockedUntil.TryGetValue(key, out var until))
        {
            return false;
        }

        if (until > DateTime.UtcNow)
        {
            return true;
        }

        _contextWindowGrowthBlockedUntil.TryRemove(new KeyValuePair<string, DateTime>(key, until));
        return false;
    }

    private static bool IsWindowLoadMemoryFailure(string? responseContent)
        => IsResourceExhaustion(responseContent)
           || responseContent?.Contains("requires more system memory", StringComparison.OrdinalIgnoreCase) == true
           || responseContent?.Contains("out of memory", StringComparison.OrdinalIgnoreCase) == true;

    private readonly record struct ContextOverflowResolution(bool Retry, string? Error);

    private sealed record ContextWindowGrant(int WindowTokens, DateTime ExpiresAtUtc);

    private static bool IsJsonObjectResponseFormat(object? responseFormat)
    {
        if (responseFormat is null) return false;
        if (responseFormat is string text)
        {
            return text.Contains("json", StringComparison.OrdinalIgnoreCase);
        }

        if (responseFormat is IReadOnlyDictionary<string, object> readOnly
            && readOnly.TryGetValue("type", out var readOnlyType))
        {
            return readOnlyType?.ToString()?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
        }

        if (responseFormat is IDictionary<string, object> dictionary
            && dictionary.TryGetValue("type", out var type))
        {
            return type?.ToString()?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
        }

        return responseFormat.ToString()?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string ResolveKeepAlive(Dictionary<string, object>? executionSettings, string fallback)
    {
        if (executionSettings is not null &&
            executionSettings.TryGetValue("keep_alive", out var keepAlive) &&
            !string.IsNullOrWhiteSpace(keepAlive?.ToString()))
        {
            return keepAlive!.ToString()!;
        }

        return fallback;
    }

    private LlmResponse CreateErrorResponse(string model, string errorMessage, int responseTimeMs = 0)
    {
        return new LlmResponse
        {
            Content = string.Empty,
            Provider = ProviderName,
            Model = model,
            WasSuccessful = false,
            ErrorMessage = errorMessage,
            ResponseTimeMs = responseTimeMs
        };
    }

    private static bool IsTransientError(Exception ex) =>
        ex is TaskCanceledException ||
        ex is HttpRequestException ||
        ex is TimeoutException ||
        ex.InnerException is TimeoutException ||
        ex.InnerException is System.IO.IOException;

    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code == 408 || code == 409 || code == 425 || code == 429 || code >= 500;
    }

    private static bool ShouldUseLegacyEmbeddingEndpoint(HttpStatusCode statusCode, string responseContent)
    {
        if (statusCode != HttpStatusCode.NotFound)
        {
            return false;
        }

        // A current Ollama runtime returns a model-shaped error when the
        // requested artifact is missing. A plain endpoint-shaped 404 means
        // this is an older runtime which still requires /api/embeddings.
        return !responseContent.Contains("model", StringComparison.OrdinalIgnoreCase);
    }

    private LlmEmbeddingResponse CreateEmbeddingSuccessResponse(
        float[]? vector,
        string model,
        string input,
        Stopwatch stopwatch,
        OllamaNodeState? node = null)
    {
        stopwatch.Stop();
        if (vector is not { Length: > 0 })
        {
            const string error = "Ollama returned an empty embedding vector.";
            node?.MarkFailure(error);
            _logger.LogWarning("{Error} Model={Model}", error, model);
            return new LlmEmbeddingResponse
            {
                Success = false,
                Provider = ProviderName,
                Model = model,
                ErrorMessage = error,
                ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds
            };
        }

        node?.MarkSuccess();
        return new LlmEmbeddingResponse
        {
            Success = true,
            Vector = vector,
            Provider = ProviderName,
            Model = model,
            TokenCount = EstimateTokens(input),
            ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds
        };
    }

    private LlmEmbeddingResponse CreateEmbeddingFailureResponse(
        string model,
        string error,
        Stopwatch stopwatch,
        OllamaNodeState node,
        string failureReason)
    {
        stopwatch.Stop();
        node.MarkFailure(failureReason);
        _logger.LogError("Ollama embedding error on {Node}: {Error}", node.Name, error);
        return new LlmEmbeddingResponse
        {
            Success = false,
            Provider = ProviderName,
            Model = model,
            ErrorMessage = error,
            ResponseTimeMs = (int)stopwatch.ElapsedMilliseconds
        };
    }

    private static bool IsPermanentModelErrorResponse(HttpStatusCode statusCode, string responseContent)
    {
        return IsModelNotFoundResponse(statusCode, responseContent)
               || responseContent.Contains("no longer compatible", StringComparison.OrdinalIgnoreCase)
               || responseContent.Contains("incompatible with your version of Ollama", StringComparison.OrdinalIgnoreCase)
               || responseContent.Contains("unknown model architecture", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsResourceExhaustion(string? responseContent)
    {
        if (string.IsNullOrWhiteSpace(responseContent)) return false;
        return responseContent.Contains("out-of-memory", StringComparison.OrdinalIgnoreCase)
               || responseContent.Contains("OutOfDeviceMemory", StringComparison.OrdinalIgnoreCase)
               || responseContent.Contains("failed to allocate", StringComparison.OrdinalIgnoreCase)
               || responseContent.Contains("insufficient memory", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildApiError(HttpStatusCode statusCode, string responseContent)
    {
        var error = $"API error: {(int)statusCode} {statusCode}";
        if (!string.IsNullOrWhiteSpace(responseContent))
        {
            error += $" - {TrimApiErrorContent(responseContent, 500)}";
        }

        return error;
    }

    private static string TrimApiErrorContent(string content, int maxChars)
    {
        var trimmed = content.Trim();
        return trimmed.Length <= maxChars ? trimmed : trimmed[..maxChars] + "...";
    }

    private static bool IsModelNotFoundResponse(HttpStatusCode statusCode, string responseContent)
    {
        if (statusCode != HttpStatusCode.NotFound)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(responseContent))
        {
            return true;
        }

        return responseContent.Contains("model", StringComparison.OrdinalIgnoreCase)
               && responseContent.Contains("not found", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEmbeddingContextOverflow(string responseContent)
    {
        return responseContent.Contains("input length", StringComparison.OrdinalIgnoreCase)
               && responseContent.Contains("context length", StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimEmbeddingPrompt(string text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "empty content";
        }

        var normalized = text.Length > maxChars ? text[..maxChars] : text;
        if (normalized.Length == text.Length)
        {
            return normalized;
        }

        var lastBreak = normalized.LastIndexOfAny([' ', '\n', '\r', '\t']);
        if (lastBreak >= maxChars / 2)
        {
            normalized = normalized[..lastBreak];
        }

        return string.IsNullOrWhiteSpace(normalized) ? "empty content" : normalized.Trim();
    }

    private static int EstimateTokens(string text) => text.Length / 4;

    private sealed class OllamaNodeLease : IDisposable
    {
        private int _disposed;

        public OllamaNodeState Node { get; }

        public OllamaNodeLease(OllamaNodeState node)
        {
            Node = node;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Node.Release();
            }
        }
    }

    private sealed class OllamaNodeState
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly object _lock = new();
        private int _inflight;
        private int _consecutiveFailures;
        private DateTime _lastFailureUtc = DateTime.MinValue;
        private DateTime _lastSuccessUtc = DateTime.MinValue;
        private DateTime _lastActivityUtc = DateTime.MinValue;

        public string Name { get; }
        public string Endpoint { get; }
        public int MaxConcurrentRequests { get; }

        public OllamaNodeState(string name, string endpoint, int maxConcurrentRequests)
        {
            Name = name;
            Endpoint = endpoint;
            MaxConcurrentRequests = maxConcurrentRequests;
            _semaphore = new SemaphoreSlim(maxConcurrentRequests, maxConcurrentRequests);
        }

        public int ConsecutiveFailures
        {
            get
            {
                lock (_lock)
                {
                    return _consecutiveFailures;
                }
            }
        }

        public DateTime LastSuccessUtc
        {
            get
            {
                lock (_lock)
                {
                    return _lastSuccessUtc;
                }
            }
        }

        public double Utilization => MaxConcurrentRequests <= 0
            ? 1
            : Math.Min(1, Volatile.Read(ref _inflight) / (double)MaxConcurrentRequests);

        public int Inflight => Volatile.Read(ref _inflight);

        public bool TryAcquire()
        {
            if (!_semaphore.Wait(0))
            {
                return false;
            }

            Interlocked.Increment(ref _inflight);
            return true;
        }

        public void Release()
        {
            var remaining = Interlocked.Decrement(ref _inflight);
            if (remaining < 0)
            {
                Interlocked.Exchange(ref _inflight, 0);
                return;
            }

            try
            {
                _semaphore.Release();
            }
            catch (SemaphoreFullException)
            {
                Interlocked.Exchange(ref _inflight, 0);
            }
        }

        public void MarkSuccess()
        {
            lock (_lock)
            {
                _consecutiveFailures = 0;
                _lastSuccessUtc = DateTime.UtcNow;
            }
        }

        public void MarkFailure(string _)
        {
            lock (_lock)
            {
                _consecutiveFailures++;
                _lastFailureUtc = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Called on each streamed chunk to signal the node is alive and producing tokens.
        /// </summary>
        public void MarkActivity()
        {
            lock (_lock)
            {
                _lastActivityUtc = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// True when the node has in-flight work AND has received streaming data recently,
        /// indicating it is actively generating (not hung).
        /// </summary>
        public bool HasRecentActivity(TimeSpan threshold)
        {
            lock (_lock)
            {
                if (_inflight <= 0 || _lastActivityUtc == DateTime.MinValue)
                    return false;
                return DateTime.UtcNow - _lastActivityUtc < threshold;
            }
        }

        public bool IsCoolingDown(TimeSpan cooldown)
        {
            lock (_lock)
            {
                if (_consecutiveFailures <= 0)
                {
                    return false;
                }

                return DateTime.UtcNow - _lastFailureUtc < cooldown;
            }
        }
    }
}

#region Ollama API Models

internal class OllamaTagsResponse
{
    [JsonPropertyName("models")]
    public List<OllamaModelInfo>? Models { get; set; }
}

internal class OllamaModelInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }

    [JsonPropertyName("details")]
    public OllamaModelDetails? Details { get; set; }
}

internal class OllamaModelDetails
{
    [JsonPropertyName("family")]
    public string? Family { get; set; }

    [JsonPropertyName("parameter_size")]
    public string? ParameterSize { get; set; }
}

internal sealed class OllamaShowRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";
}

internal sealed class OllamaShowResponse
{
    [JsonPropertyName("capabilities")]
    public List<string>? Capabilities { get; set; }

    [JsonPropertyName("remote_host")]
    public string? RemoteHost { get; set; }

    [JsonPropertyName("remote_model")]
    public string? RemoteModel { get; set; }

    [JsonPropertyName("details")]
    public JsonElement? Details { get; set; }

    [JsonPropertyName("model_info")]
    public JsonElement? ModelInfo { get; set; }

    [JsonPropertyName("template")]
    public string? Template { get; set; }

    [JsonIgnore]
    public bool HasRecognizedMetadata =>
        Capabilities is not null
        || Details is { ValueKind: JsonValueKind.Object }
        || ModelInfo is { ValueKind: JsonValueKind.Object }
        || !string.IsNullOrWhiteSpace(Template)
        || !string.IsNullOrWhiteSpace(RemoteHost);
}

internal class OllamaChatRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("messages")]
    public List<OllamaMessage> Messages { get; set; } = new();

    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = false;

    [JsonPropertyName("keep_alive")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? KeepAlive { get; set; }

    [JsonPropertyName("format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Format { get; set; }

    [JsonPropertyName("think")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Think { get; set; }

    /// <summary>
    /// False makes a runtime that supports it reject a prompt longer than the
    /// window instead of silently dropping part of it. Older runtimes ignore it.
    /// </summary>
    [JsonPropertyName("truncate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Truncate { get; set; }

    [JsonPropertyName("options")]
    public Dictionary<string, object> Options { get; set; } = new();
}

internal class OllamaMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("thinking")]
    public string? Thinking { get; set; }

    [JsonPropertyName("images")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Images { get; set; }
}

internal class OllamaChatResponse
{
    [JsonPropertyName("message")]
    public OllamaMessage? Message { get; set; }

    [JsonPropertyName("done")]
    public bool Done { get; set; }

    [JsonPropertyName("prompt_eval_count")]
    public int? PromptEvalCount { get; set; }

    [JsonPropertyName("eval_count")]
    public int? EvalCount { get; set; }

    [JsonPropertyName("total_duration")]
    public long? TotalDuration { get; set; }

    [JsonPropertyName("prompt_eval_duration")]
    public long? PromptEvalDuration { get; set; }

    [JsonPropertyName("eval_duration")]
    public long? EvalDuration { get; set; }

    [JsonPropertyName("load_duration")]
    public long? LoadDuration { get; set; }
}

internal class OllamaEmbedRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("input")]
    public string Input { get; set; } = "";

    [JsonPropertyName("keep_alive")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? KeepAlive { get; set; }

    [JsonPropertyName("options")]
    public OllamaEmbeddingOptions? Options { get; set; }
}

internal sealed class OllamaEmbeddingOptions
{
    [JsonPropertyName("num_ctx")]
    public int NumContext { get; set; }
}

internal class OllamaEmbeddingResponse
{
    [JsonPropertyName("embeddings")]
    public float[][]? Embeddings { get; set; }
}

/// <summary>Compatibility DTO for Ollama versions predating <c>/api/embed</c>.</summary>
internal class OllamaLegacyEmbeddingRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("keep_alive")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? KeepAlive { get; set; }

    [JsonPropertyName("options")]
    public OllamaEmbeddingOptions? Options { get; set; }
}

/// <summary>Compatibility response DTO for Ollama's retired embeddings endpoint.</summary>
internal class OllamaLegacyEmbeddingResponse
{
    [JsonPropertyName("embedding")]
    public float[]? Embedding { get; set; }
}

internal class OllamaPullProgress
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("digest")]
    public string? Digest { get; set; }

    [JsonPropertyName("total")]
    public long Total { get; set; }

    [JsonPropertyName("completed")]
    public long Completed { get; set; }
}

internal sealed record ArtifactDigestCacheEntry(string Digest, DateTime ExpiresAtUtc);

#endregion
