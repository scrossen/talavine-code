using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// One exact provider/model route retained in a durable automatic-routing survival set.
/// Every member carries its own current, route-bound function proof; a catalog row or
/// provider-health observation alone is not enough to enter the set.
/// </summary>
public sealed record LlmRouteSurvivalCandidate(
    string Provider,
    string Model,
    ModelScoutDecisionRouteReceipt DecisionEvidence,
    ModelScoutFunctionEvidenceReceipt FunctionEvidence,
    bool? SupportsVision = null,
    bool? SupportsToolUse = null,
    bool? SupportsStructuredOutput = null,
    int? MaxContextTokens = null,
    bool IsLocal = false,
    bool IsSubscription = false);

/// <summary>
/// Ordered, proof-bound routes that can keep one automatic function alive while Model
/// Scout repairs its preferred route asynchronously. This is additive state: the
/// existing primary and fallback columns remain the compatibility authority for hosts
/// that do not understand survival sets.
/// </summary>
public sealed record LlmRouteSurvivalSet
{
    // v2 binds the qualification-receipt fields v1 left out. A stored v1 set no longer
    // validates and is ignored until Model Scout's maintenance writes the v2 set.
    public const string Schema = "llm-route-survival-set.v2";

    public required string FunctionType { get; init; }

    /// <summary>
    /// SHA-256 of the exact function-preference baseline used to build this set. It
    /// includes route values, the mutation revision, and user-pin state, but does not
    /// duplicate free-form authorship in the survival-set row.
    /// </summary>
    public required string BoundPreferenceIdentitySha256 { get; init; }

    /// <summary>
    /// Exposed separately for cheap stale-state diagnostics. Null is valid for a legacy
    /// preference row; <see cref="BoundPreferenceIdentitySha256"/> remains authoritative.
    /// </summary>
    public string? BoundPreferenceMutationRevision { get; init; }

    public required IReadOnlyList<LlmRouteSurvivalCandidate> Routes { get; init; }

    /// <summary>
    /// Canonical hash over the preference binding, route order, and every proof field.
    /// Storage recomputes this on write and read so partial or stale payloads fail closed.
    /// </summary>
    public required string ProofBundleSha256 { get; init; }

    /// <summary>Machine correlation for the Scout decision that assembled the set.</summary>
    public required string CorrelationId { get; init; }

    public DateTime CreatedAtUtc { get; init; }

    /// <summary>Opaque revision advanced on every successful survival-set write.</summary>
    public required string MutationRevision { get; init; }

    public static LlmRouteSurvivalSet Create(
        string functionType,
        FunctionPreferenceVersion boundPreference,
        IReadOnlyList<LlmRouteSurvivalCandidate> routes,
        string correlationId,
        DateTime? createdAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(boundPreference);
        ArgumentNullException.ThrowIfNull(routes);

        var set = new LlmRouteSurvivalSet
        {
            FunctionType = ModelScoutFunctionEvidence.NormalizeFunctionType(functionType),
            BoundPreferenceIdentitySha256 = LlmRouteSurvivalSetValidation.ComputePreferenceIdentitySha256(
                boundPreference),
            BoundPreferenceMutationRevision = boundPreference.MutationRevision,
            Routes = routes
                .Select(route => route is null
                    ? throw new ArgumentException("A survival route cannot be null.", nameof(routes))
                    : new LlmRouteSurvivalCandidate(
                    route.Provider.Trim(),
                    route.Model.Trim(),
                    route.DecisionEvidence,
                    route.FunctionEvidence,
                    route.SupportsVision,
                    route.SupportsToolUse,
                    route.SupportsStructuredOutput,
                    route.MaxContextTokens,
                    route.IsLocal,
                    route.IsSubscription))
                .ToArray(),
            ProofBundleSha256 = string.Empty,
            CorrelationId = correlationId.Trim(),
            CreatedAtUtc = createdAtUtc ?? DateTime.UtcNow,
            MutationRevision = Guid.NewGuid().ToString("N"),
        };

        return set with
        {
            ProofBundleSha256 = LlmRouteSurvivalSetValidation.ComputeProofBundleSha256(set),
        };
    }
}

public enum LlmRouteSurvivalSetWriteStatus
{
    Applied,
    Conflict,
    /// <summary>
    /// Durable storage failed and the exact set-plus-audit commit could not be proven.
    /// Callers must not reinterpret this as an optimistic-concurrency conflict or
    /// automatically retry it, because the commit outcome may be uncertain.
    /// </summary>
    StorageFailure,
    Rejected,
    Unsupported,
}

public sealed record LlmRouteSurvivalSetWriteResult(
    LlmRouteSurvivalSetWriteStatus Status,
    LlmRouteSurvivalSet? Current = null,
    string? Error = null)
{
    public bool Applied => Status == LlmRouteSurvivalSetWriteStatus.Applied;
}

/// <summary>
/// Shared structural and cryptographic binding checks for survival-set ingress and
/// restart reads. Runtime execution still performs its ordinary live hard-safety,
/// privacy, provider-availability, cooldown, and request-capability checks.
/// </summary>
public static class LlmRouteSurvivalSetValidation
{
    public const int MinimumRouteCount = 3;
    public const int MaximumRouteCount = 8;
    private const int MaximumProviderLength = 80;
    private const int MaximumModelLength = 240;
    private const int MaximumCorrelationLength = 64;

    public static bool TryValidate(
        LlmRouteSurvivalSet? set,
        FunctionPreferenceVersion currentPreference,
        out string? error,
        DateTime? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(currentPreference);
        var now = nowUtc ?? DateTime.UtcNow;
        if (set is null)
        {
            error = "The survival set is missing.";
            return false;
        }

        var functionType = ModelScoutFunctionEvidence.NormalizeFunctionType(set.FunctionType);
        var preferenceFunction = ModelScoutFunctionEvidence.NormalizeFunctionType(
            currentPreference.FunctionType);
        if (!currentPreference.Exists
            || string.IsNullOrWhiteSpace(functionType)
            || !string.Equals(functionType, preferenceFunction, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(currentPreference.PreferredProvider)
            || string.IsNullOrWhiteSpace(currentPreference.PreferredModel)
            || !LlmFunctionPreferenceValidation.TryValidateFallbackPair(
                currentPreference.FallbackProvider,
                currentPreference.FallbackModel,
                out _))
        {
            error = "The survival set is not bound to an existing exact function preference.";
            return false;
        }

        var expectedPreferenceIdentity = ComputePreferenceIdentitySha256(currentPreference);
        if (!FixedTimeSha256Equals(set.BoundPreferenceIdentitySha256, expectedPreferenceIdentity)
            || !string.Equals(
                set.BoundPreferenceMutationRevision,
                currentPreference.MutationRevision,
                StringComparison.Ordinal))
        {
            error = "The survival set preference binding is stale or does not match the exact route.";
            return false;
        }

        if (set.Routes is null
            || set.Routes.Count < MinimumRouteCount
            || set.Routes.Count > MaximumRouteCount)
        {
            error = $"A survival set must contain {MinimumRouteCount} to {MaximumRouteCount} routes.";
            return false;
        }

        if (!IsMachineCorrelation(set.CorrelationId)
            || set.CreatedAtUtc == default
            || set.CreatedAtUtc > now.AddMinutes(5)
            || !Guid.TryParseExact(set.MutationRevision, "N", out _))
        {
            error = "The survival set metadata is invalid.";
            return false;
        }

        var routeIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var executionChannels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var vendorFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < set.Routes.Count; index++)
        {
            var route = set.Routes[index];
            if (route is null
                || route.DecisionEvidence is null
                || route.FunctionEvidence is null
                || string.IsNullOrWhiteSpace(route.Provider)
                || route.Provider.Length > MaximumProviderLength
                || string.IsNullOrWhiteSpace(route.Model)
                || route.Model.Length > MaximumModelLength
                || string.IsNullOrWhiteSpace(route.DecisionEvidence.Provider)
                || route.DecisionEvidence.Provider.Length > MaximumProviderLength
                || string.IsNullOrWhiteSpace(route.DecisionEvidence.Model)
                || route.DecisionEvidence.Model.Length > MaximumModelLength
                || string.IsNullOrWhiteSpace(route.FunctionEvidence.FunctionType)
                || string.IsNullOrWhiteSpace(route.FunctionEvidence.Provider)
                || route.FunctionEvidence.Provider.Length > MaximumProviderLength
                || string.IsNullOrWhiteSpace(route.FunctionEvidence.Model)
                || route.FunctionEvidence.Model.Length > MaximumModelLength
                || string.IsNullOrWhiteSpace(route.FunctionEvidence.Source)
                || string.IsNullOrWhiteSpace(route.FunctionEvidence.ProvenanceKind))
            {
                error = $"Survival route {index} has an invalid provider/model identity.";
                return false;
            }

            var identity = $"{route.Provider.Trim()}\u001f{route.Model.Trim()}";
            if (!routeIdentities.Add(identity))
            {
                error = "A survival set cannot contain a duplicate provider/model route.";
                return false;
            }

            executionChannels.Add(CanonicalExecutionChannel(route.Provider));
            vendorFamilies.Add(CanonicalVendorFamily(route.Provider));
            if (!string.Equals(
                    route.Provider.Trim(),
                    route.DecisionEvidence.Provider.Trim(),
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    route.Model.Trim(),
                    route.DecisionEvidence.Model.Trim(),
                    StringComparison.OrdinalIgnoreCase)
                || !route.DecisionEvidence.Eligible
                || !ModelScoutCapabilityEvidence.IsFunctionAuthoritative(
                    route.DecisionEvidence.CapabilityProvenance)
                || !DecisionProvenanceMatchesProof(
                    route.DecisionEvidence.CapabilityProvenance,
                    route.FunctionEvidence.ProvenanceKind)
                || !string.Equals(
                    route.DecisionEvidence.EvidenceSource?.Trim(),
                    route.FunctionEvidence.Source.Trim(),
                    StringComparison.OrdinalIgnoreCase)
                || route.DecisionEvidence.CapabilityScore is < 0 or > 100
                || route.DecisionEvidence.EstimatedLatencyMs is < 0
                || route.DecisionEvidence.EstimatedCostUsd is < 0
                || route.DecisionEvidence.ReliabilityScore is < 0 or > 1
                || route.MaxContextTokens is <= 0
                || !IsBoundedDurableEvidence(route.DecisionEvidence.Reason, 512, required: true)
                || !IsBoundedDurableEvidence(route.DecisionEvidence.EvidenceSource, 256, required: true)
                || !IsBoundedDurableEvidence(route.DecisionEvidence.EvidenceVersion, 256)
                || !IsBoundedDurableEvidence(route.DecisionEvidence.RoutingPrior?.Source, 256)
                || !IsBoundedDurableEvidence(route.FunctionEvidence.Source, 256, required: true)
                || !IsBoundedDurableEvidence(route.FunctionEvidence.BundleVersion, 256)
                || !IsBoundedDurableEvidence(route.FunctionEvidence.ExecutionContractId, 256)
                || !IsBoundedDurableEvidence(route.FunctionEvidence.RuntimeVersion, 256)
                || !IsBoundedDurableEvidence(route.FunctionEvidence.ExecutionRuntimeIdentity, 256)
                || !IsBoundedDurableEvidence(route.FunctionEvidence.HardwareBucket, 256))
            {
                error = $"Survival route {index} has inconsistent decision or capability evidence.";
                return false;
            }

            if (!ModelScoutFunctionEvidence.IsCoherent(
                    route.FunctionEvidence,
                    functionType,
                    route.Provider,
                    route.Model,
                    nowUtc: now))
            {
                error = $"Survival route {index} does not carry coherent current function proof.";
                return false;
            }
        }

        if (executionChannels.Count < MinimumRouteCount)
        {
            error = $"A survival set must span at least {MinimumRouteCount} independent execution channels.";
            return false;
        }

        if (vendorFamilies.Count < 2)
        {
            error = "A survival set must span at least two vendor families.";
            return false;
        }

        if (!SameRoute(
                set.Routes[0],
                currentPreference.PreferredProvider!,
                currentPreference.PreferredModel!))
        {
            error = "The first survival route must remain the legacy preferred route.";
            return false;
        }

        var hasFallback = !string.IsNullOrWhiteSpace(currentPreference.FallbackProvider)
                          && !string.IsNullOrWhiteSpace(currentPreference.FallbackModel);
        if (hasFallback
            && !SameRoute(
                set.Routes[1],
                currentPreference.FallbackProvider!,
                currentPreference.FallbackModel!))
        {
            error = "The second survival route must remain the legacy fallback route.";
            return false;
        }

        var expectedBundle = ComputeProofBundleSha256(set);
        if (!FixedTimeSha256Equals(set.ProofBundleSha256, expectedBundle))
        {
            error = "The survival-set proof bundle hash does not match its ordered routes.";
            return false;
        }

        error = null;
        return true;
    }

    public static string ComputePreferenceIdentitySha256(FunctionPreferenceVersion preference)
    {
        ArgumentNullException.ThrowIfNull(preference);
        var canonical = new StringBuilder(512);
        Append(canonical, "schema", "llm-function-preference-binding.v1");
        Append(canonical, "exists", preference.Exists);
        Append(
            canonical,
            "function",
            ModelScoutFunctionEvidence.NormalizeFunctionType(preference.FunctionType));
        Append(canonical, "primaryProvider", preference.PreferredProvider);
        Append(canonical, "primaryModel", preference.PreferredModel);
        Append(canonical, "fallbackProvider", preference.FallbackProvider);
        Append(canonical, "fallbackModel", preference.FallbackModel);
        Append(canonical, "enabled", preference.IsEnabled);
        Append(canonical, "vision", preference.RequiresVision);
        Append(canonical, "updatedAt", preference.UpdatedAt?.Ticks);
        Append(canonical, "updatedBy", preference.UpdatedBy);
        Append(canonical, "mutationRevision", preference.MutationRevision);
        Append(canonical, "pinnedAt", preference.PinnedByUserAt?.Ticks);
        return Sha256(canonical.ToString());
    }

    /// <summary>
    /// Collapses spelling aliases only within one physical adapter/channel. Direct API
    /// and subscription CLI routes from the same vendor intentionally remain distinct:
    /// they have separate authentication, capacity, process, and cooldown domains.
    /// </summary>
    public static string CanonicalExecutionChannel(string provider)
        => provider.Trim().ToUpperInvariant() switch
        {
            "CODEX" or "CODEXCLI" => "codex-cli",
            "OPENAI" => "openai-api",
            "AZUREOPENAI" => "azure-openai-api",
            "CLAUDECODE" => "claude-code-cli",
            "CLAUDE" or "ANTHROPIC" => "anthropic-api",
            "GEMINICLI" => "gemini-cli",
            "GEMINI" or "GOOGLE" or "GOOGLEAI" => "gemini-api",
            "VERTEXAI" => "vertex-ai",
            "GROK" or "XAI" => "xai-api",
            "HUGGINGFACE" or "HUGGINGFACEAPI" or "HF" => "huggingface-api",
            "BEDROCK" or "AWSBEDROCK" or "AMAZONBEDROCK" => "aws-bedrock-api",
            "OLLAMA" => "ollama",
            "LMSTUDIO" or "LM STUDIO" => "lmstudio",
            "OPENROUTER" => "openrouter",
            "DEEPSEEK" => "deepseek-api",
            "GENIEX" => "geniex",
            "REMOTEPEER" => "remote-peer",
            var normalized => normalized.ToLowerInvariant(),
        };

    /// <summary>
    /// Separate vendor-diversity axis. This does not merge execution channels for
    /// cooldown or attempt identity; it only prevents a nominally three-channel set
    /// from depending entirely on one vendor family.
    /// </summary>
    public static string CanonicalVendorFamily(string provider)
    {
        // Each LAN node remains its own execution and cooldown channel, but all
        // Ollama@ nodes are the same Ollama vendor family. Otherwise three daemons
        // could satisfy the vendor-diversity requirement by name alone.
        if (OllamaProviderFamily.IsOllamaFamily(provider))
            return "ollama";

        return provider.Trim().ToUpperInvariant() switch
        {
            "CODEX" or "CODEXCLI" or "OPENAI" or "AZUREOPENAI" => "openai",
            "CLAUDECODE" or "CLAUDE" or "ANTHROPIC" => "anthropic",
            "GEMINICLI" or "GEMINI" or "GOOGLE" or "GOOGLEAI" or "VERTEXAI" => "google",
            "GROK" or "XAI" => "xai",
            "HUGGINGFACE" or "HUGGINGFACEAPI" or "HF" => "huggingface",
            "BEDROCK" or "AWSBEDROCK" or "AMAZONBEDROCK" => "aws",
            "OLLAMA" => "ollama",
            "LMSTUDIO" or "LM STUDIO" => "lmstudio",
            "OPENROUTER" => "openrouter",
            "DEEPSEEK" => "deepseek",
            "GENIEX" => "geniex",
            "REMOTEPEER" => "remote-peer",
            var normalized => normalized.ToLowerInvariant(),
        };
    }

    public static string ComputeProofBundleSha256(LlmRouteSurvivalSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        var canonical = new StringBuilder(4_096);
        Append(canonical, "schema", LlmRouteSurvivalSet.Schema);
        Append(
            canonical,
            "function",
            ModelScoutFunctionEvidence.NormalizeFunctionType(set.FunctionType));
        Append(canonical, "preferenceIdentity", set.BoundPreferenceIdentitySha256);
        Append(canonical, "preferenceRevision", set.BoundPreferenceMutationRevision);
        Append(canonical, "correlation", set.CorrelationId);
        Append(canonical, "createdAt", set.CreatedAtUtc.Ticks);
        Append(canonical, "mutationRevision", set.MutationRevision);
        Append(canonical, "routeCount", set.Routes?.Count ?? 0);

        if (set.Routes is not null)
        {
            foreach (var route in set.Routes)
            {
                Append(canonical, "provider", route.Provider.Trim());
                Append(canonical, "model", route.Model.Trim());
                AppendDecision(canonical, route.DecisionEvidence);
                AppendProof(canonical, route.FunctionEvidence);
                Append(canonical, "supportsVision", route.SupportsVision?.ToString());
                Append(canonical, "supportsToolUse", route.SupportsToolUse?.ToString());
                Append(
                    canonical,
                    "supportsStructuredOutput",
                    route.SupportsStructuredOutput?.ToString());
                Append(canonical, "maxContextTokens", route.MaxContextTokens);
                Append(canonical, "isLocal", route.IsLocal);
                Append(canonical, "isSubscription", route.IsSubscription);
            }
        }

        return Sha256(canonical.ToString());
    }

    private static void AppendDecision(
        StringBuilder canonical,
        ModelScoutDecisionRouteReceipt decision)
    {
        Append(canonical, "decisionProvider", decision.Provider);
        Append(canonical, "decisionModel", decision.Model);
        Append(canonical, "decisionTotal", decision.Total.ToString(CultureInfo.InvariantCulture));
        Append(canonical, "decisionCapability", decision.Capability.ToString(CultureInfo.InvariantCulture));
        Append(canonical, "decisionSpeed", decision.Speed.ToString(CultureInfo.InvariantCulture));
        Append(canonical, "decisionCost", decision.Cost.ToString(CultureInfo.InvariantCulture));
        Append(canonical, "decisionReliability", decision.Reliability.ToString(CultureInfo.InvariantCulture));
        Append(canonical, "decisionEvidenceConfidence", decision.EvidenceConfidence.ToString(CultureInfo.InvariantCulture));
        Append(canonical, "decisionCapabilityScore", decision.CapabilityScore.ToString(CultureInfo.InvariantCulture));
        Append(canonical, "decisionEstimatedLatencyMs", decision.EstimatedLatencyMs?.ToString(CultureInfo.InvariantCulture));
        Append(canonical, "decisionEstimatedCostUsd", decision.EstimatedCostUsd?.ToString(CultureInfo.InvariantCulture));
        Append(canonical, "decisionReliabilityScore", decision.ReliabilityScore?.ToString(CultureInfo.InvariantCulture));
        Append(canonical, "decisionCapabilityProvenance", decision.CapabilityProvenance.ToString());
        Append(canonical, "decisionEvidenceSource", decision.EvidenceSource);
        Append(canonical, "decisionEligible", decision.Eligible);
        Append(canonical, "decisionReason", decision.Reason);
        Append(canonical, "decisionEvidenceVersion", decision.EvidenceVersion);
        Append(canonical, "decisionRoutingPriorQuality", decision.RoutingPrior?.Quality.ToString());
        Append(canonical, "decisionRoutingPriorSpeed", decision.RoutingPrior?.Speed.ToString());
        Append(
            canonical,
            "decisionRoutingPriorCapacity",
            decision.RoutingPrior?.CapacityEfficiency.ToString());
        Append(canonical, "decisionRoutingPriorSource", decision.RoutingPrior?.Source);
    }

    private static void AppendProof(
        StringBuilder canonical,
        ModelScoutFunctionEvidenceReceipt proof)
    {
        Append(canonical, "proofFunction", proof.FunctionType);
        Append(canonical, "proofProvider", proof.Provider);
        Append(canonical, "proofModel", proof.Model);
        Append(canonical, "proofSource", proof.Source);
        Append(canonical, "proofIdentity", proof.ProofIdentitySha256);
        Append(canonical, "proofSuite", proof.SuiteSha256);
        Append(canonical, "proofEvaluated", proof.EvaluatedAtUtc.Ticks);
        Append(canonical, "proofExpires", proof.ExpiresAtUtc.Ticks);
        Append(canonical, "proofWeightedScore", proof.WeightedScore);
        Append(canonical, "proofPassRate", proof.PassRate);
        Append(canonical, "proofCriticalFailures", proof.CriticalFailures);
        Append(canonical, "proofParseFailures", proof.ParseFailures);
        Append(canonical, "proofCaseCount", proof.CaseCount);
        Append(canonical, "proofProvenance", proof.ProvenanceKind);
        Append(canonical, "proofOperatorCleared", proof.OperatorCleared);
        Append(canonical, "proofHardware", proof.HardwareBucket);
        Append(canonical, "proofClearanceTier", proof.ClearanceTier);
        Append(canonical, "proofBundleVersion", proof.BundleVersion);
        Append(canonical, "proofExecutionContract", proof.ExecutionContractId);
        Append(canonical, "proofRuntimeVersion", proof.RuntimeVersion);
        Append(canonical, "proofRuntimeIdentity", proof.ExecutionRuntimeIdentity);
        Append(canonical, "proofScope", proof.EvidenceScope);
        Append(canonical, "proofSourceFunction", proof.SourceFunctionType);
        Append(canonical, "proofQualificationProtocol", proof.QualificationProtocolVersion);
        Append(canonical, "proofProviderPack", proof.ProviderPackIdentity);
        Append(canonical, "proofRouteKind", proof.RouteKind);
        Append(canonical, "proofSourceSuiteVersion", proof.SourceSuiteVersion);
        Append(canonical, "proofPrompt", proof.PromptSha256);
        Append(canonical, "proofExecutableBundle", proof.ExecutableBundleSha256);
        Append(canonical, "proofEvaluatorVersion", proof.EvaluatorVersion);
        Append(canonical, "proofRequiredSettings", proof.RequiredSettingsSha256);
        Append(canonical, "proofExpectedCaseCount", proof.ExpectedCaseCount);
        Append(canonical, "proofProviderFailures", proof.ProviderFailures);
        Append(canonical, "proofFallbackCaseCount", proof.FallbackCaseCount);
    }

    private static bool SameRoute(
        LlmRouteSurvivalCandidate route,
        string provider,
        string model)
        => string.Equals(route.Provider.Trim(), provider.Trim(), StringComparison.OrdinalIgnoreCase)
           && string.Equals(route.Model.Trim(), model.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool DecisionProvenanceMatchesProof(
        ModelScoutCapabilityEvidenceKind kind,
        string? proofProvenance)
        => kind switch
        {
            ModelScoutCapabilityEvidenceKind.MeasuredFunctionBenchmark => string.Equals(
                proofProvenance,
                "measured-function-benchmark",
                StringComparison.OrdinalIgnoreCase),
            ModelScoutCapabilityEvidenceKind.OperatorClearance => string.Equals(
                proofProvenance,
                "operator-clearance",
                StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    private static bool IsBoundedDurableEvidence(
        string? value,
        int maximumLength,
        bool required = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return !required;
        }

        return value.Length <= maximumLength
               && string.Equals(
                   RoutingDiagnosticSanitizer.Sanitize(value, maximumLength),
                   value,
                   StringComparison.Ordinal);
    }

    private static bool IsMachineCorrelation(string? value)
        => value is { Length: > 0 and <= MaximumCorrelationLength }
           && value.All(character => char.IsLetterOrDigit(character)
                                     || character is '-' or '_' or '.' or ':');

    private static bool FixedTimeSha256Equals(string? left, string? right)
    {
        if (!TryParseSha256(left, out var leftBytes)
            || !TryParseSha256(right, out var rightBytes))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static bool TryParseSha256(string? value, out byte[] bytes)
    {
        bytes = [];
        if (value is not { Length: 64 })
        {
            return false;
        }

        try
        {
            bytes = Convert.FromHexString(value);
            return bytes.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Sha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static void Append(StringBuilder builder, string name, string? value)
    {
        value ??= string.Empty;
        builder.Append(name.Length)
            .Append(':')
            .Append(name)
            .Append('=')
            .Append(value.Length)
            .Append(':')
            .Append(value)
            .Append(';');
    }

    private static void Append(StringBuilder builder, string name, bool value)
        => Append(builder, name, value ? "1" : "0");

    private static void Append(StringBuilder builder, string name, int value)
        => Append(builder, name, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder builder, string name, int? value)
        => Append(builder, name, value?.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder builder, string name, long? value)
        => Append(builder, name, value?.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder builder, string name, long value)
        => Append(builder, name, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder builder, string name, double value)
        => Append(builder, name, value.ToString("R", CultureInfo.InvariantCulture));
}
