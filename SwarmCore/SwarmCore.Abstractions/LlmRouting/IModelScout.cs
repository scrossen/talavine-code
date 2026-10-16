using System.Text.Json.Serialization;

namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Capability/cost tier requested from <see cref="IModelScout"/>.
/// The tier controls the default capability floor and score weights; callers
/// can still provide explicit weights when the user has expressed a policy.
/// </summary>
public enum ModelScoutTier
{
    Economy = 0,
    Balanced = 1,
    Frontier = 2,
}

/// <summary>Typed provenance for a numeric capability score.</summary>
public enum ModelScoutCapabilityEvidenceKind
{
    Unknown = 0,
    StaticHeuristic = 1,
    CatalogPrior = 2,
    MeasuredGeneralBenchmark = 3,
    MeasuredFunctionBenchmark = 4,
    OperatorClearance = 5,
}

public static class ModelScoutCapabilityEvidence
{
    public static bool IsMeasured(ModelScoutCapabilityEvidenceKind kind)
        => kind is ModelScoutCapabilityEvidenceKind.MeasuredGeneralBenchmark
            or ModelScoutCapabilityEvidenceKind.MeasuredFunctionBenchmark;

    /// <summary>
    /// Exact function authority may be a real function benchmark or a current,
    /// explicit operator clearance. A clearance is intentionally not reported as
    /// measured evidence.
    /// </summary>
    public static bool IsFunctionAuthoritative(ModelScoutCapabilityEvidenceKind kind)
        => kind is ModelScoutCapabilityEvidenceKind.MeasuredFunctionBenchmark
            or ModelScoutCapabilityEvidenceKind.OperatorClearance;

    /// <summary>
    /// Canonical hard capability floor for each Scout tier. Both the ranker and
    /// router execution boundary use this mapping so a custom or faulty Scout
    /// implementation cannot label a below-tier candidate eligible and bypass
    /// the caller's requested tier.
    /// </summary>
    public static decimal MinimumScoreForTier(ModelScoutTier tier)
        => tier switch
        {
            ModelScoutTier.Frontier => 90m,
            ModelScoutTier.Balanced => 75m,
            _ => 55m,
        };
}

/// <summary>
/// User- or application-selected weights for the three Model Scout axes.
/// Values do not need to sum to one; the ranker normalizes them.
/// </summary>
public sealed record ModelScoutScoreWeights(
    decimal Capability,
    decimal Speed,
    decimal Cost);

/// <summary>
/// An operator-published relative quality category used only when a route has
/// categorical clearance rather than a measured benchmark score. These names
/// are deliberately qualitative: they are not synthetic benchmark results.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModelScoutRelativeQualityPrior>))]
public enum ModelScoutRelativeQualityPrior
{
    Unknown = 0,
    Economy = 1,
    Standard = 2,
    High = 3,
}

/// <summary>
/// An operator-published relative response-speed category used only when this
/// installation has no applicable latency or throughput measurement.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModelScoutRelativeSpeedPrior>))]
public enum ModelScoutRelativeSpeedPrior
{
    Unknown = 0,
    Deliberate = 1,
    Standard = 2,
    Fast = 3,
}

/// <summary>
/// An operator-published relative subscription-capacity category. It models
/// expected quota consumption among routes with no marginal API bill; it is
/// not a dollar estimate.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModelScoutRelativeCapacityEfficiencyPrior>))]
public enum ModelScoutRelativeCapacityEfficiencyPrior
{
    Unknown = 0,
    Low = 1,
    Standard = 2,
    High = 3,
}

/// <summary>
/// Explicit qualitative cold-start evidence. A complete prior may compare
/// operator-cleared routes until this installation records real measurements.
/// <see cref="Source"/> is a bounded machine-readable policy revision, not
/// free-form user text.
/// </summary>
public sealed record ModelScoutRoutingPrior(
    ModelScoutRelativeQualityPrior Quality,
    ModelScoutRelativeSpeedPrior Speed,
    ModelScoutRelativeCapacityEfficiencyPrior CapacityEfficiency,
    string Source)
{
    public bool IsComplete =>
        Enum.IsDefined(Quality)
        && Enum.IsDefined(Speed)
        && Enum.IsDefined(CapacityEfficiency)
        && Quality != ModelScoutRelativeQualityPrior.Unknown
        && Speed != ModelScoutRelativeSpeedPrior.Unknown
        && CapacityEfficiency != ModelScoutRelativeCapacityEfficiencyPrior.Unknown
        && IsSafeSource(Source);

    public static bool TryCreate(
        string? quality,
        string? speed,
        string? capacityEfficiency,
        string? source,
        out ModelScoutRoutingPrior? prior)
    {
        prior = null;
        if (!Enum.TryParse<ModelScoutRelativeQualityPrior>(quality, true, out var parsedQuality)
            || !Enum.TryParse<ModelScoutRelativeSpeedPrior>(speed, true, out var parsedSpeed)
            || !Enum.TryParse<ModelScoutRelativeCapacityEfficiencyPrior>(
                capacityEfficiency,
                true,
                out var parsedCapacityEfficiency))
        {
            return false;
        }

        var candidate = new ModelScoutRoutingPrior(
            parsedQuality,
            parsedSpeed,
            parsedCapacityEfficiency,
            source?.Trim() ?? string.Empty);
        if (!candidate.IsComplete)
            return false;

        prior = candidate;
        return true;
    }

    private static bool IsSafeSource(string? source)
        => !string.IsNullOrWhiteSpace(source)
           && source.Length <= 120
           && source.All(character =>
               char.IsAsciiLetterOrDigit(character)
               || character is '.' or ':' or '-' or '_');
}

/// <summary>
/// A database-agnostic request for a function-aware model recommendation.
/// A null provider asks Scout to compare every available provider; specifying
/// one restricts the decision to that provider.
/// </summary>
public sealed class ModelScoutRecommendationRequest
{
    public LlmModelRequest? ModelRequest { get; init; }
    public ModelScoutConnectionPreference PreferredModelConnection { get; init; } = ModelScoutConnectionPreference.SubscriptionFirst;
    public required string FunctionType { get; init; }
    public string? Provider { get; init; }
    /// <summary>An exact user/manual/diagnostic constraint, never an automatic default.</summary>
    public string? ExactModel { get; init; }
    public IReadOnlyList<string>? AllowedProviders { get; init; }
    public ModelScoutTier Tier { get; init; } = ModelScoutTier.Balanced;
    public LlmInteractivity Interactivity { get; init; } = LlmInteractivity.Unspecified;
    public LlmWorkloadKind Workload { get; init; } = LlmWorkloadKind.Unspecified;

    /// <summary>
    /// Host-declared demand (surface, complexity, expected context). Resolved
    /// with interactivity and size by <see cref="LlmTaskDemandPolicy"/>; it
    /// orders admissible routes and never grants eligibility.
    /// </summary>
    public LlmTaskDemand? Demand { get; init; }

    /// <summary>
    /// Burn fraction at which a subscription window is under pressure. A
    /// pressured subscription keeps its eligibility but loses the
    /// subscription-first preference, so work rotates to another healthy
    /// subscription before it drains the pressured one. Null disables it.
    /// </summary>
    public decimal? SubscriptionQuotaThreshold { get; init; }

    public LlmPrivacyClass PrivacyClass { get; init; } = LlmPrivacyClass.CloudOk;
    public bool RequiresVision { get; init; }
    /// <summary>
    /// Hard requirement for native/validated tool calling. A provider-level tool
    /// translator alone is not model capability evidence; candidates must carry an
    /// affirmative <see cref="ModelScoutCandidate.SupportsToolUse"/> value.
    /// </summary>
    public bool RequiresToolUse { get; init; }
    /// <summary>
    /// Hard requirement for a measured or catalog-declared structured-output
    /// contract. Unknown support is ineligible only when the caller cannot
    /// repair imperfect output — see <see cref="StructuredOutputRepairAvailable"/>.
    /// </summary>
    public bool RequiresStructuredOutput { get; init; }

    /// <summary>
    /// The caller has a JSON-repair path (the router's typed repair ladder, a
    /// self-validating cascade, etc.) that can coerce imperfect structured
    /// output. When true, provider-native structured-output evidence is not an
    /// admission prerequisite: both unknown and explicitly absent native support
    /// are allowed because the caller-owned repair and validation boundary decides
    /// usability at execution time. When false (the default), the strict native
    /// capability gate stands.
    /// </summary>
    public bool StructuredOutputRepairAvailable { get; init; }
    /// <summary>
    /// Minimum context window required by the caller. Null means no explicit
    /// context-window gate; when set, unknown context size is ineligible.
    /// </summary>
    public int? MinimumContextTokens { get; init; }
    public int EstimatedInputTokens { get; init; } = 1_000;
    public int EstimatedOutputTokens { get; init; } = 500;
    public decimal? MaximumEstimatedCostUsd { get; init; }
    public int? LatencyTargetMs { get; init; }
    public decimal? MinimumCapabilityScore { get; init; }
    public bool RequireMeasuredCapabilityScore { get; init; }
    /// <summary>
    /// Hard floor for measured success reliability (0-1). Unknown reliability is
    /// disclosed and penalized but remains admissible; a known-bad route must not win
    /// merely because it is cheap or fast.
    /// </summary>
    public decimal MinimumReliabilityScore { get; init; } = 0.50m;
    public ModelScoutScoreWeights? Weights { get; init; }

    /// <summary>
    /// Persist the full ranked recommendation evidence. Passive UI previews should
    /// disable this to avoid flooding the durable ledger; decisions used to execute
    /// or change a route should retain the default.
    /// </summary>
    public bool PersistAudit { get; init; } = true;
    public string? AuditSessionId { get; init; }
    public string? AuditTurnId { get; init; }

    /// <summary>
    /// When true, only a current exact-function measurement or applicable global
    /// model-quality clearance is admissible. Load-bearing pipelines should enable
    /// this; general interactive recommendations may use general priors.
    /// </summary>
    public bool RequireCurrentFunctionEvidence { get; init; }

    /// <summary>
    /// When true, the recommendation may use general priors but Scout must also
    /// refresh applicable function/global evidence and keep one deduplicated,
    /// auditable proof recovery episode active when none is current. This is the normal automatic
    /// routing mode for non-load-bearing functions: work continues through hard-
    /// gated provisional choices without silently abandoning benchmark recovery.
    /// Ignored when <see cref="RequireCurrentFunctionEvidence"/> is true.
    /// </summary>
    public bool MaintainCurrentFunctionEvidenceRecovery { get; init; }

    /// <summary>
    /// Whether this recommendation may start live provider, local-runtime, or
    /// hardware inventory work. Recommendations are cache-first by default: normal
    /// routing and startup consume persisted/current catalogs without turning each
    /// request into discovery. Explicit refresh/repair actions, provider-change
    /// recovery, and the scheduled catalog-maintenance pass opt in.
    /// </summary>
    public bool AllowLiveInventoryRefresh { get; init; }

    /// <summary>Keep serving evidence warm without waiting for physical inventory
    /// in the requesting call. Passive maintenance/UI reads leave this false.</summary>
    public bool MaintainServingInventory { get; init; }
}

/// <summary>
/// One provider/model candidate and the evidence Scout can use to rank it.
/// <see cref="CapabilityScore"/> uses a 0-100 scale;
/// <see cref="ReliabilityScore"/> uses a 0-1 fraction. Unknown speed, cost, and
/// reliability remain null so the decision can disclose uncertainty instead of
/// inventing precision.
/// </summary>
public sealed class ModelScoutCandidate
{
    private ModelScoutFunctionPerformance? _functionPerformance;
    public ModelScoutFunctionPerformance? FunctionPerformance { get => _functionPerformance; init => _functionPerformance = value; }
    public ModelScoutCandidate WithFunctionPerformance(ModelScoutFunctionPerformance? performance)
    {
        var copy = (ModelScoutCandidate)MemberwiseClone();
        copy._functionPerformance = performance;
        return copy;
    }
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public decimal CapabilityScore { get; init; }
    public ModelScoutCapabilityEvidenceKind CapabilityEvidenceKind { get; init; }
    /// <summary>
    /// Average successful output-token count represented by
    /// <see cref="EstimatedLatencyMs"/>. A total response latency is not time to
    /// first token; this baseline lets the ranker scale that measured sample to
    /// the current workload without double-counting it as fixed startup time.
    /// Null for static/website latency rows whose sampled output size is unknown.
    /// </summary>
    public decimal? LatencySampleOutputTokens { get; init; }
    public decimal? EstimatedLatencyMs { get; init; }
    public decimal? TokensPerSecond { get; init; }
    public decimal? EstimatedCostUsd { get; init; }
    public decimal? InputCostPer1K { get; init; }
    public decimal? OutputCostPer1K { get; init; }
    public decimal? ReliabilityScore { get; init; }
    public int? ReliabilitySampleCount { get; init; }
    public bool IsAvailable { get; init; } = true;
    public bool IsLocal { get; init; }
    public bool IsSubscription { get; init; }
    public bool? SupportsVision { get; init; }
    public bool? SupportsToolUse { get; init; }
    public bool? SupportsStructuredOutput { get; init; }
    public int? MaxContextTokens { get; init; }
    /// <summary>
    /// The largest window the route's runtime grows to for a request its
    /// current window (<see cref="MaxContextTokens"/>) cannot hold. A native
    /// Ollama route on a runtime that rejects an over-window request instead
    /// of truncating it reloads the model at a larger window, within its
    /// trained length and memory envelope. Null when the route cannot grow.
    /// Growing reloads the model, so a waiting request prefers a route that
    /// holds it as is.
    /// </summary>
    public int? MaxGrowableContextTokens { get; init; }
    public string? CapabilityEvidenceSource { get; init; }
    public string? EvidenceFunctionType { get; init; }
    public string? EvidenceSource { get; init; }
    public string? EvidenceVersion { get; init; }
    public DateTime? EvidenceTimestampUtc { get; init; }
    public DateTime? EvidenceExpiresAtUtc { get; init; }
    public bool IsEvidenceCurrent { get; init; } = true;
    /// <summary>
    /// Qualitative policy evidence for comparing cleared routes before local
    /// measurements exist. Measured capability, latency/throughput, and metered
    /// cost always take precedence over the corresponding category.
    /// </summary>
    public ModelScoutRoutingPrior? RoutingPrior { get; init; }
    public ModelScoutFunctionEvidenceReceipt? FunctionEvidence { get; init; }
}

/// <summary>
/// Auditable normalized score components for a ranked candidate.
/// </summary>
public sealed record ModelScoutScoreBreakdown(
    decimal Capability,
    decimal Speed,
    decimal Cost,
    decimal Reliability,
    decimal EvidenceConfidence,
    decimal Total,
    ModelScoutScoreWeights AppliedWeights,
    ModelScoutRoutingPrior? AppliedRoutingPrior = null);

/// <summary>
/// Compact, self-contained proof copied from an authoritative website evidence
/// row. <see cref="ProvenanceKind"/> distinguishes function-scoped measurements
/// from zero-metric global model clearances; consumers must not reinterpret the
/// latter as a score. It contains no prompt, response, or full candidate set.
/// </summary>
public sealed record ModelScoutFunctionEvidenceReceipt(
    string FunctionType,
    string Provider,
    string Model,
    string Source,
    string ProofIdentitySha256,
    string SuiteSha256,
    DateTime EvaluatedAtUtc,
    DateTime ExpiresAtUtc,
    double WeightedScore,
    double PassRate,
    int CriticalFailures,
    int ParseFailures,
    int CaseCount,
    string ProvenanceKind,
    bool OperatorCleared,
    string? HardwareBucket = null,
    string? ClearanceTier = null,
    string? BundleVersion = null,
    string? ExecutionContractId = null,
    string? RuntimeVersion = null,
    string? ExecutionRuntimeIdentity = null,
    string? EvidenceScope = null,
    string? SourceFunctionType = null,
    string? QualificationProtocolVersion = null,
    string? ProviderPackIdentity = null,
    string? RouteKind = null,
    string? SourceSuiteVersion = null,
    string? PromptSha256 = null,
    string? ExecutableBundleSha256 = null,
    string? EvaluatorVersion = null,
    string? RequiredSettingsSha256 = null,
    int? ExpectedCaseCount = null,
    int? ProviderFailures = null,
    int? FallbackCaseCount = null);

public static class ModelScoutFunctionEvidenceScopes
{
    public const string Function = "function";
    public const string GlobalModel = "global-model";

    public static bool IsGlobalModel(string? value)
        => string.Equals(value?.Trim(), GlobalModel, StringComparison.OrdinalIgnoreCase);

    public static bool IsFunction(string? value)
        => string.IsNullOrWhiteSpace(value)
           || string.Equals(value.Trim(), Function, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Structural validation for an exact current function-proof receipt.</summary>
public static class ModelScoutFunctionEvidence
{
    public const string LocalModelQualificationProvenance = "local-model-qualification";
    public const string LocalModelQualificationSourcePrefix =
        "local-global-model-qualification:";
    public const string LocalModelQualificationProtocolVersion =
        "global-generative-message-understanding-v1";

    public static bool IsCoherent(
        ModelScoutCandidate candidate,
        string expectedFunctionType,
        DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var proof = candidate.FunctionEvidence;
        if (proof is null
            || string.IsNullOrWhiteSpace(expectedFunctionType)
            || !candidate.IsAvailable
            || !candidate.IsEvidenceCurrent
            || (!ModelScoutCapabilityEvidence.IsFunctionAuthoritative(
                    candidate.CapabilityEvidenceKind)
                && !IsLocalModelQualification(proof))
            || !SameFunction(expectedFunctionType, candidate.EvidenceFunctionType)
            || string.IsNullOrWhiteSpace(candidate.EvidenceSource)
            || !CandidateProvenanceMatches(
                candidate.CapabilityEvidenceKind,
                proof.ProvenanceKind)
            || !IsCoherent(
                proof,
                expectedFunctionType,
                candidate.Provider,
                candidate.Model,
                candidate.EvidenceSource,
                now)
            || candidate.EvidenceTimestampUtc is not { } candidateEvaluated
            || candidateEvaluated != proof.EvaluatedAtUtc
            || candidate.EvidenceExpiresAtUtc is not { } candidateExpiry
            || candidateExpiry != proof.ExpiresAtUtc)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates a compact proof at an execution boundary where the full candidate
    /// is intentionally unavailable. The caller supplies the exact route and source
    /// it is about to execute so a stale or cross-route receipt cannot be replayed.
    /// </summary>
    public static bool IsCoherent(
        ModelScoutFunctionEvidenceReceipt? proof,
        string expectedFunctionType,
        string expectedProvider,
        string expectedModel,
        string? expectedSource = null,
        DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var evidenceContractIsValid = IsMeasuredFunctionBenchmark(proof)
                                      || IsOperatorClearance(proof)
                                      || IsLocalModelQualification(proof);
        return proof is not null
               && !string.IsNullOrWhiteSpace(expectedFunctionType)
               && !string.IsNullOrWhiteSpace(expectedProvider)
               && !string.IsNullOrWhiteSpace(expectedModel)
               && (proof.OperatorCleared || IsLocalModelQualification(proof))
               && evidenceContractIsValid
               && SameFunction(expectedFunctionType, proof.FunctionType)
               && IsScopeCompatible(proof, expectedFunctionType)
               && Same(expectedProvider, proof.Provider)
               && Same(expectedModel, proof.Model)
               && (string.IsNullOrWhiteSpace(expectedSource) || Same(expectedSource, proof.Source))
               && IsSha256(proof.ProofIdentitySha256)
               && IsSha256(proof.SuiteSha256)
               && proof.EvaluatedAtUtc <= now.AddMinutes(5)
               && proof.ExpiresAtUtc > now
               && proof.ExpiresAtUtc > proof.EvaluatedAtUtc;
    }

    private static bool IsScopeCompatible(
        ModelScoutFunctionEvidenceReceipt proof,
        string expectedFunctionType)
    {
        if (ModelScoutFunctionEvidenceScopes.IsFunction(proof.EvidenceScope))
        {
            return true;
        }

        var normalizedFunction = NormalizeFunctionType(expectedFunctionType);
        return ModelScoutFunctionEvidenceScopes.IsGlobalModel(proof.EvidenceScope)
               && (proof.Source?.StartsWith(
                       "website-global-model-clearance:",
                       StringComparison.OrdinalIgnoreCase) == true
                   || proof.Source?.StartsWith(
                       LocalModelQualificationSourcePrefix,
                       StringComparison.OrdinalIgnoreCase) == true)
               && !normalizedFunction.StartsWith("custom-", StringComparison.OrdinalIgnoreCase)
               && !normalizedFunction.Contains("embedding", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CandidateProvenanceMatches(
        ModelScoutCapabilityEvidenceKind kind,
        string? provenanceKind)
        => kind switch
        {
            ModelScoutCapabilityEvidenceKind.MeasuredFunctionBenchmark => string.Equals(
                provenanceKind,
                "measured-function-benchmark",
                StringComparison.OrdinalIgnoreCase),
            ModelScoutCapabilityEvidenceKind.OperatorClearance => string.Equals(
                provenanceKind,
                "operator-clearance",
                StringComparison.OrdinalIgnoreCase),
            ModelScoutCapabilityEvidenceKind.MeasuredGeneralBenchmark => string.Equals(
                provenanceKind,
                LocalModelQualificationProvenance,
                StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    private static bool IsMeasuredFunctionBenchmark(ModelScoutFunctionEvidenceReceipt? proof)
        => proof is not null
           && string.Equals(
               proof.ProvenanceKind,
               "measured-function-benchmark",
               StringComparison.OrdinalIgnoreCase)
           && proof.CaseCount > 0
           && double.IsFinite(proof.WeightedScore)
           && double.IsFinite(proof.PassRate)
           && proof.WeightedScore is >= 0 and <= 1
           && proof.PassRate is >= 0 and <= 1
           && proof.CriticalFailures >= 0
           && proof.ParseFailures >= 0
           && string.IsNullOrWhiteSpace(proof.ClearanceTier);

    private static bool IsOperatorClearance(ModelScoutFunctionEvidenceReceipt? proof)
        => proof is not null
           && string.Equals(
               proof.ProvenanceKind,
               "operator-clearance",
               StringComparison.OrdinalIgnoreCase)
           && proof.ClearanceTier?.Trim().ToLowerInvariant() is "economy" or "balanced" or "frontier"
           && proof.CaseCount == 0
           && proof.WeightedScore == 0
           && proof.PassRate == 0
           && proof.CriticalFailures == 0
           && proof.ParseFailures == 0
           && !string.IsNullOrWhiteSpace(proof.BundleVersion)
           && (ModelScoutFunctionEvidenceScopes.IsFunction(proof.EvidenceScope)
               || (ModelScoutFunctionEvidenceScopes.IsGlobalModel(proof.EvidenceScope)
                   && !string.IsNullOrWhiteSpace(proof.SourceFunctionType)));

    private static bool IsLocalModelQualification(ModelScoutFunctionEvidenceReceipt? proof)
        => proof is not null
           && string.Equals(
               proof.ProvenanceKind,
               LocalModelQualificationProvenance,
               StringComparison.OrdinalIgnoreCase)
           && !proof.OperatorCleared
           && ModelScoutFunctionEvidenceScopes.IsGlobalModel(proof.EvidenceScope)
           && string.Equals(
               proof.SourceFunctionType,
               "model-quality",
               StringComparison.OrdinalIgnoreCase)
           // Receipts are deserialized from persisted JSON, so Source can be null.
           && proof.Source?.StartsWith(
               LocalModelQualificationSourcePrefix,
               StringComparison.OrdinalIgnoreCase) == true
           && string.Equals(
               proof.QualificationProtocolVersion,
               LocalModelQualificationProtocolVersion,
               StringComparison.Ordinal)
           && proof.RouteKind?.Trim().ToLowerInvariant() is "api" or "subscription-cli" or "local"
           && !string.IsNullOrWhiteSpace(proof.ExecutionContractId)
           && !string.IsNullOrWhiteSpace(proof.ProviderPackIdentity)
           && !string.IsNullOrWhiteSpace(proof.ExecutionRuntimeIdentity)
           && !string.IsNullOrWhiteSpace(proof.SourceSuiteVersion)
           && !string.IsNullOrWhiteSpace(proof.EvaluatorVersion)
           && IsSha256(proof.PromptSha256)
           && IsSha256(proof.ExecutableBundleSha256)
           && IsSha256(proof.RequiredSettingsSha256)
           && proof.ExpectedCaseCount is > 0
           && proof.CaseCount == proof.ExpectedCaseCount
           && proof.ProviderFailures == 0
           && proof.FallbackCaseCount == 0
           && proof.CaseCount > 0
           && double.IsFinite(proof.WeightedScore)
           && double.IsFinite(proof.PassRate)
           && proof.WeightedScore is >= 0 and <= 1
           && proof.PassRate is >= 0 and <= 1
           && proof.CriticalFailures == 0
           && proof.ParseFailures == 0
           && string.IsNullOrWhiteSpace(proof.ClearanceTier);

    private static bool Same(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left)
           && !string.IsNullOrWhiteSpace(right)
           && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool SameFunction(string? left, string? right)
        => string.Equals(
            NormalizeFunctionType(left),
            NormalizeFunctionType(right),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Canonical benchmark identity shared by hosts and the core router. These
    /// aliases are the same workload/proof lane; keeping normalization here
    /// prevents a valid agent-chat website receipt from being rejected when the
    /// runtime call uses its background agent-loop name.
    /// </summary>
    public static string NormalizeFunctionType(string? functionType)
    {
        var normalized = (functionType ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "chat" or "assistant-chat" or "chatbot" or "agent-loop" => FunctionTypes.AgentChat,
            "embedding" or "embeddings" => FunctionTypes.TextEmbedding,
            _ => normalized,
        };
    }

    private static bool IsSha256(string? value)
        => value is { Length: 64 }
           && value.All(character =>
               character is >= '0' and <= '9'
               or >= 'a' and <= 'f'
               or >= 'A' and <= 'F');
}

/// <summary>One bounded route row in a live Model Scout decision receipt.</summary>
public sealed record ModelScoutDecisionRouteReceipt(
    string Provider,
    string Model,
    decimal Total,
    decimal Capability,
    decimal Speed,
    decimal Cost,
    decimal Reliability,
    decimal EvidenceConfidence,
    decimal CapabilityScore,
    decimal? EstimatedLatencyMs,
    decimal? EstimatedCostUsd,
    decimal? ReliabilityScore,
    ModelScoutCapabilityEvidenceKind CapabilityProvenance,
    string? EvidenceSource,
    bool Eligible,
    string Reason,
    string? EvidenceVersion = null,
    ModelScoutRoutingPrior? RoutingPrior = null,
    decimal? LatencySampleOutputTokens = null,
    decimal? TokensPerSecond = null,
    string? RankingNote = null,
    int? MaxContextTokens = null,
    bool IsLocal = false,
    bool IsSubscription = false,
    int? MaxGrowableContextTokens = null);

/// <summary>
/// Bounded evidence retained with an actual runtime selection when the caller
/// suppresses the full recommendation audit.
/// </summary>
public sealed record ModelScoutRuntimeDecisionReceipt(
    ModelScoutScoreWeights AppliedWeights,
    ModelScoutDecisionRouteReceipt Selected,
    IReadOnlyList<ModelScoutDecisionRouteReceipt> Alternatives,
    ModelScoutFunctionEvidenceReceipt? FunctionEvidence)
{
    public string? FunctionType { get; init; }
    public LlmWorkloadKind Workload { get; init; }
    public string? SelectionPolicyVersion { get; init; }

    /// <summary>Workload and demand policy revisions that together ordered this decision.</summary>
    public static string SelectionPolicy => $"{LlmWorkloadPolicy.Version}+{LlmTaskDemandPolicy.Version}";

    /// <summary>The resolved demand class that ordered the candidates.</summary>
    public string? Demand { get; init; }
    public IReadOnlyList<string> DemandSignals { get; init; } = [];

    /// <summary>The window the whole request needed, and the part compaction cannot remove (token counts only).</summary>
    public int? RequiredContextTokens { get; init; }
    public int? IrreducibleContextTokens { get; init; }
    public DateTime ExpiresAtUtc { get; init; } = DateTime.UtcNow.AddMinutes(5);
    /// <summary>
    /// Creates the same bounded runtime receipt for primary selection, dynamic
    /// fallback, and host-provided self-heal results. Keeping this projection at
    /// the contract boundary prevents execution paths from retaining only an
    /// opaque correlation ID while losing the actual score/evidence decision.
    /// </summary>
    public static ModelScoutRuntimeDecisionReceipt FromDecision(
        ModelScoutRecommendationDecision decision,
        ModelScoutRankedCandidate selected)
    {
        static bool SameRoute(ModelScoutRankedCandidate left, ModelScoutRankedCandidate right)
            => string.Equals(left.Candidate.Provider, right.Candidate.Provider, StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.Candidate.Model, right.Candidate.Model, StringComparison.OrdinalIgnoreCase);

        static ModelScoutDecisionRouteReceipt ToReceipt(
            ModelScoutRankedCandidate ranked,
            bool isSelected)
            => new(
                ranked.Candidate.Provider,
                ranked.Candidate.Model,
                ranked.Score.Total,
                ranked.Score.Capability,
                ranked.Score.Speed,
                ranked.Score.Cost,
                ranked.Score.Reliability,
                ranked.Score.EvidenceConfidence,
                ranked.Candidate.CapabilityScore,
                ranked.Candidate.EstimatedLatencyMs,
                ranked.Candidate.EstimatedCostUsd,
                ranked.Candidate.ReliabilityScore,
                ranked.Candidate.CapabilityEvidenceKind,
                ranked.Candidate.EvidenceSource ?? ranked.Candidate.CapabilityEvidenceSource,
                ranked.IsEligible,
                isSelected
                    ? "Selected as the executable route used under the applied constraints and admission policy."
                    : ranked.RejectionReason
                      ?? "Eligible ranked alternative; the admission policy preferred the executed route for this request.",
                ranked.Candidate.EvidenceVersion,
                ranked.Candidate.RoutingPrior,
                ranked.Candidate.LatencySampleOutputTokens,
                ranked.Candidate.TokensPerSecond,
                ranked.RankingNote,
                ranked.Candidate.MaxContextTokens,
                ranked.Candidate.IsLocal,
                ranked.Candidate.IsSubscription,
                ranked.Candidate.MaxGrowableContextTokens);

        var others = decision.Candidates
            .Where(candidate => !SameRoute(candidate, selected))
            .ToList();
        var bounded = new List<ModelScoutRankedCandidate>(capacity: 3);
        bounded.AddRange(others.Where(candidate => candidate.IsEligible).Take(2));
        if (bounded.Count < 3)
        {
            bounded.AddRange(others
                .Where(candidate => !candidate.IsEligible && !bounded.Contains(candidate))
                .Take(3 - bounded.Count));
        }
        if (bounded.Count < 3)
        {
            bounded.AddRange(others
                .Where(candidate => !bounded.Contains(candidate))
                .Take(3 - bounded.Count));
        }

        return new ModelScoutRuntimeDecisionReceipt(
            selected.Score.AppliedWeights,
            ToReceipt(selected, isSelected: true),
            bounded.Select(candidate => ToReceipt(candidate, isSelected: false)).ToList(),
            selected.Candidate.FunctionEvidence)
        {
            FunctionType = decision.Request.FunctionType,
            Workload = LlmWorkloadPolicy.Resolve(decision.Request.Workload, decision.Request.Interactivity, decision.Request.FunctionType),
            SelectionPolicyVersion = SelectionPolicy,
            Demand = decision.Demand?.Class.ToString(),
            DemandSignals = decision.Demand?.Signals ?? [],
            RequiredContextTokens = decision.Demand?.RequiredContextTokens,
            IrreducibleContextTokens = decision.Demand?.IrreducibleContextTokens,
        };
    }
}

public sealed class ModelScoutRankedCandidate
{
    public required ModelScoutCandidate Candidate { get; init; }
    public required ModelScoutScoreBreakdown Score { get; init; }
    public string? RejectionReason { get; init; }
    public bool IsEligible => string.IsNullOrWhiteSpace(RejectionReason);

    /// <summary>
    /// Bounded, fixed-vocabulary explanation of the ordering preferences that
    /// placed this route (demand group, context fit, node load). Never holds
    /// prompt or response text.
    /// </summary>
    public string? RankingNote { get; init; }
}

/// <summary>
/// Full recommendation result. Keeping the ranked candidates and normalized
/// score components makes the decision explainable and persistable by any host.
/// </summary>
public sealed class ModelScoutRecommendationDecision
{
    public string AuditCorrelationId { get; set; } = Guid.NewGuid().ToString("N");
    public required ModelScoutRecommendationRequest Request { get; init; }

    /// <summary>The demand the ranker resolved for this request, when it ranked one.</summary>
    public LlmResolvedTaskDemand? Demand { get; init; }
    public ModelScoutRankedCandidate? Selected { get; init; }
    public IReadOnlyList<ModelScoutRankedCandidate> Candidates { get; init; } = [];
    public required string Reason { get; init; }
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Reusable Model Scout boundary for every SwarmCore host. Hosts own candidate
/// discovery and persistence; SwarmCore owns the request/decision contract and
/// ranking semantics.
/// </summary>
public interface IModelScout
{
    Task<ModelScoutRecommendationDecision> RecommendAsync(
        ModelScoutRecommendationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional host policy gate for live automatic Scout selection. Hosts that expose
/// an automation on/off control implement this so disabling automation immediately
/// returns routing to configured preferences without unregistering Scout itself.
/// </summary>
public interface IModelScoutRoutingGate
{
    Task<bool> IsAutomaticSelectionEnabledAsync(CancellationToken cancellationToken = default);
}
