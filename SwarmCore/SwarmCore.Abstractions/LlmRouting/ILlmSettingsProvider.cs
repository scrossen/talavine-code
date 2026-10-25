namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Function-specific model preference configuration.
/// This is the database-agnostic DTO for model preferences.
/// </summary>
/// <summary>
/// Pin authority and actor identity for function preferences. These are two different
/// questions and are deliberately answered by two different members:
/// <list type="bullet">
/// <item><see cref="IsUserPinned(DateTime?)"/> answers "is this route protected from
/// autonomous overwrite?" It reads <see cref="LlmFunctionPreference.PinnedByUserAt"/>
/// and nothing else. Model Scout stewardship and A/B autopilot must leave a pinned
/// route alone (mirrors the ProviderSaveSource.UserEdit back-off for provider settings).</item>
/// <item><see cref="IsUserActor(string?)"/> answers "did a human, rather than an
/// autonomous subsystem, initiate this call?" It compares a caller-supplied actor id
/// against the <see cref="User"/> sentinel. It is only valid for actor/caller ids that
/// the calling code itself produced — never for a stored authorship field.</item>
/// </list>
/// Authority was previously derived by string-matching <c>UpdatedBy</c>. That is no
/// longer done anywhere: <see cref="LlmFunctionPreference.UpdatedBy"/> is free-form
/// audit text that may hold a real person's name.
/// </summary>
public static class LlmPreferenceAuthors
{
    /// <summary>
    /// Sentinel actor id meaning "a human initiated this". Used for audit
    /// <c>Actor</c> columns and for caller ids passed into routing APIs. This is a
    /// machine sentinel, never a person's name, and never a source of pin authority.
    /// </summary>
    public const string User = "user";

    /// <summary>
    /// Audit provenance written only by the settings provider's lazy
    /// <see cref="ILlmSettingsProvider.EnsureFunctionPreferenceAsync"/> path after
    /// the router has completed a usable call. This is not mutation authority and
    /// must never be used as a substitute for <see cref="IsUserPinned"/>.
    /// </summary>
    public const string RouterAutoMaterialization = "llm-router:auto-materialization";

    /// <summary>
    /// The single authority test for a function route: a pin exists if and only if
    /// <see cref="LlmFunctionPreference.PinnedByUserAt"/> is set. Pass that value
    /// directly; do not pass an authorship or actor string.
    /// </summary>
    public static bool IsUserPinned(DateTime? pinnedByUserAt) => pinnedByUserAt.HasValue;

    /// <summary>
    /// True when a caller-supplied actor/user id denotes the human user rather than an
    /// autonomous subsystem. This is an identity check for values the caller minted, and
    /// is <b>not</b> an authority check — never call it on a persisted <c>UpdatedBy</c>.
    /// </summary>
    public static bool IsUserActor(string? actor) =>
        string.Equals(actor, User, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Authority carried by a function-preference write. Existing explicit user pins are
/// immutable to ordinary/autonomous writes; only a user-confirmed action may opt in to
/// replacing one. This is deliberately separate from <see cref="LlmFunctionPreference.UpdatedBy"/>:
/// a round-tripped DTO can still contain <c>UpdatedBy="user"</c> even when an autonomous
/// subsystem is now attempting the write.
/// </summary>
public enum FunctionPreferenceWriteAuthority
{
    /// <summary>Leave an existing <see cref="LlmPreferenceAuthors.User"/> pin unchanged.</summary>
    RespectUserPin,

    /// <summary>
    /// The user explicitly confirmed replacing the existing pin. UI/tooling callers must
    /// choose this value deliberately; autonomous services must never use it.
    /// </summary>
    UserAuthorizedOverride,
}

/// <summary>
/// Exact optimistic-concurrency baseline for a function preference. The full route and
/// authorship state and mutation revision are included so every later write, including
/// a same-value write, invalidates a pending user-approved change.
/// </summary>
public sealed record FunctionPreferenceVersion(
    bool Exists,
    string FunctionType,
    string? PreferredProvider,
    string? PreferredModel,
    string? FallbackProvider,
    string? FallbackModel,
    bool IsEnabled,
    bool RequiresVision,
    DateTime? UpdatedAt,
    string? UpdatedBy,
    string? MutationRevision = null,
    DateTime? PinnedByUserAt = null)
{
    public static FunctionPreferenceVersion Capture(
        string functionType,
        LlmFunctionPreference? preference)
        => preference is null
            ? new FunctionPreferenceVersion(
                false,
                functionType,
                null,
                null,
                null,
                null,
                false,
                false,
                null,
                null,
                null,
                null)
            : new FunctionPreferenceVersion(
                true,
                preference.FunctionType,
                preference.PreferredProvider,
                preference.PreferredModel,
                preference.FallbackProvider,
                preference.FallbackModel,
                preference.IsEnabled,
                preference.RequiresVision,
                preference.UpdatedAt,
                preference.UpdatedBy,
                preference.MutationRevision,
                preference.PinnedByUserAt);
}

public enum FunctionPreferenceConditionalWriteStatus
{
    Applied,
    Conflict,
    Rejected
}

public sealed record FunctionPreferenceConditionalWriteResult(
    FunctionPreferenceConditionalWriteStatus Status,
    LlmFunctionPreference? Current = null)
{
    public bool Applied => Status == FunctionPreferenceConditionalWriteStatus.Applied;
}

/// <summary>One exact-row member of an all-or-nothing preference batch.</summary>
public sealed record FunctionPreferenceBatchWrite(
    LlmFunctionPreference Preference,
    FunctionPreferenceVersion Expected);

public enum FunctionPreferenceBatchWriteStatus
{
    Applied,
    Conflict,
    Rejected,
    Unsupported,
}

/// <summary>
/// Result of an all-or-nothing function-preference batch. A non-applied result
/// guarantees that this batch changed no route.
/// </summary>
public sealed record FunctionPreferenceBatchWriteResult(
    FunctionPreferenceBatchWriteStatus Status,
    int AppliedCount = 0,
    string? FunctionType = null,
    string? Error = null)
{
    public bool Applied => Status == FunctionPreferenceBatchWriteStatus.Applied;
}

public class LlmFunctionPreference
{
    public int Id { get; set; }
    public required string FunctionType { get; set; }
    public required string PreferredProvider { get; set; }
    public required string PreferredModel { get; set; }
    public string? FallbackProvider { get; set; }
    public string? FallbackModel { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool RequiresVision { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public string? CreatedBy { get; set; }

    /// <summary>
    /// Free-form audit text: who last wrote this row. May hold a real person's display
    /// name, a subsystem tag, or null/empty. It is never pin or mutation authority — use
    /// <see cref="PinnedByUserAt"/> for that. A small set of exact machine provenance
    /// markers may carry route evidence (for example, successful router materialization).
    /// By convention a null or empty value means Model Scout owns the row.
    /// </summary>
    public string? UpdatedBy { get; set; }

    /// <summary>
    /// UTC time the user pinned this route, or null if the route is not pinned. This is
    /// the sole source of pin authority (see <see cref="LlmPreferenceAuthors.IsUserPinned(DateTime?)"/>).
    /// A timestamp rather than a flag so pin age is available to pin-health reporting.
    /// </summary>
    public DateTime? PinnedByUserAt { get; set; }

    /// <summary>
    /// Transient correlation for the atomic preference-change receipt. Storage
    /// implementations persist this on the audit row, not on the preference row,
    /// so a Scout recommendation can be joined to the mutation it authorized.
    /// </summary>
    public string? AuditCorrelationId { get; set; }

    /// <summary>
    /// Opaque storage revision for the exact row. Protected callers generate this
    /// before a conditional write; storage persists it and advances it for every
    /// mutation so same-value ABA writes remain distinguishable during compensation.
    /// </summary>
    public string? MutationRevision { get; set; }

    // Known-good config (managed by IKnownGoodConfigTracker)
    public string? KnownGoodProvider { get; set; }
    public string? KnownGoodModel { get; set; }
    public DateTime? KnownGoodConfirmedAt { get; set; }
}

/// <summary>
/// Shared validation for persisted function-route state. A fallback is one route,
/// so provider and model must either both be present or both be absent. Storage and
/// ingress implementations use this boundary instead of silently inventing the
/// missing half of a route.
/// </summary>
public static class LlmFunctionPreferenceValidation
{
    /// <summary>
    /// Creates a compensating write for a prior exact row. Compensation restores
    /// the prior values but is itself a new mutation, so it must never resurrect
    /// the prior revision and make A/R0 -&gt; B/R1 -&gt; A/R0 look unchanged to a
    /// stale compare-and-set caller.
    /// </summary>
    public static LlmFunctionPreference CreateCompensatingWrite(
        LlmFunctionPreference prior,
        string? auditCorrelationId = null)
        => new()
        {
            Id = prior.Id,
            FunctionType = prior.FunctionType,
            PreferredProvider = prior.PreferredProvider,
            PreferredModel = prior.PreferredModel,
            FallbackProvider = prior.FallbackProvider,
            FallbackModel = prior.FallbackModel,
            IsEnabled = prior.IsEnabled,
            RequiresVision = prior.RequiresVision,
            CreatedAt = prior.CreatedAt,
            UpdatedAt = DateTime.UtcNow,
            CreatedBy = prior.CreatedBy,
            UpdatedBy = prior.UpdatedBy,
            PinnedByUserAt = prior.PinnedByUserAt,
            AuditCorrelationId = auditCorrelationId,
            MutationRevision = Guid.NewGuid().ToString("N"),
            KnownGoodProvider = prior.KnownGoodProvider,
            KnownGoodModel = prior.KnownGoodModel,
            KnownGoodConfirmedAt = prior.KnownGoodConfirmedAt,
        };

    public static bool TryValidateFallbackPair(
        LlmFunctionPreference preference,
        out string? error)
        => TryValidateFallbackPair(
            preference.FallbackProvider,
            preference.FallbackModel,
            out error);

    public static bool TryValidateFallbackPair(
        string? fallbackProvider,
        string? fallbackModel,
        out string? error)
    {
        var hasProvider = !string.IsNullOrWhiteSpace(fallbackProvider);
        var hasModel = !string.IsNullOrWhiteSpace(fallbackModel);
        if (hasProvider == hasModel)
        {
            error = null;
            return true;
        }

        error = "A fallback route must include both provider and model, or neither.";
        return false;
    }
}

/// <summary>
/// Provider configuration for LLM services
/// </summary>
public class LlmProviderSettings
{
    public required string ProviderName { get; set; }
    public string? ApiKey { get; set; }
    public string? Endpoint { get; set; }
    public string? DefaultModel { get; set; }
    public string? DefaultEmbeddingModel { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int Priority { get; set; } = 100;
    public bool DisabledByHealthCheck { get; set; }
    public DateTime? HealthCheckDisabledAt { get; set; }
    public string? HealthCheckDisabledReason { get; set; }
    public Dictionary<string, string> AdditionalSettings { get; set; } = new();

    // Rate limiting settings
    /// <summary>
    /// Maximum emails to process per hour for this provider.
    /// Null or 0 means unlimited (typically for local providers like Ollama).
    /// Default is 20 for cloud providers.
    /// </summary>
    public int? MaxEmailsPerHour { get; set; }

    /// <summary>
    /// Batch size for processing emails at once.
    /// Higher values are more efficient but use more memory.
    /// Default: 50 for local, 10 for cloud.
    /// </summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// Whether this provider requests local treatment (no API costs).
    /// This mutable flag is not sufficient for LocalOnly privacy routing;
    /// <see cref="LlmProviderLocalityPolicy"/> must also validate the provider
    /// identity and endpoint.
    /// </summary>
    public bool IsLocalProvider { get; set; }

    /// <summary>
    /// UTC timestamp of the most recent successful validation ping. Null if never validated.
    /// Set by the startup provider-health validator after a successful probe.
    /// </summary>
    public DateTime? LastValidatedAt { get; set; }

    /// <summary>
    /// Human-readable error from the last failed validation probe. Null when healthy
    /// or never validated. Examples: "model 'gpt-5-nano' not found", "401 Unauthorized".
    /// </summary>
    public string? LastValidationError { get; set; }

    /// <summary>
    /// UTC timestamp of the most recent user-initiated edit (e.g. Settings UI save).
    /// Autonomous systems (reflection agent, self-healer) consult this to avoid
    /// overwriting a choice the user just made.
    /// </summary>
    public DateTime? UserEditedAt { get; set; }

    /// <summary>
    /// Storage revision of the provider row. Preserve the original value when
    /// editing or cloning a loaded provider, including user edits after slow
    /// discovery/proof work. A null value represents a new provider. Checked
    /// persistence rejects an existing-row save without its original revision.
    /// </summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// Applies a user's new API key to this loaded row and changes nothing else:
    /// endpoint, default model, priority, limits and provider-specific settings
    /// stay as saved. A provider that was off only because it had no working key
    /// (never connected, disconnected, or paused by the health check) is turned
    /// on; a provider the user paused stays paused.
    /// </summary>
    public void ReplaceApiKey(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        var offOnlyForMissingKey = !IsEnabled
            && (DisabledByHealthCheck || string.IsNullOrWhiteSpace(ApiKey));
        ApiKey = apiKey;
        if (offOnlyForMissingKey)
        {
            IsEnabled = true;
            ClearHealthCheckDisable();
        }
    }

    /// <summary>
    /// Disconnects this loaded row: removes the key and turns the provider off,
    /// keeping its endpoint, default model, priority, limits and provider-specific
    /// settings for a later reconnect. A disconnect is the user's choice, so any
    /// health-check pause is cleared and automatic recovery leaves the row alone.
    /// </summary>
    public void Disconnect()
    {
        ApiKey = null;
        IsEnabled = false;
        ClearHealthCheckDisable();
    }

    private void ClearHealthCheckDisable()
    {
        DisabledByHealthCheck = false;
        HealthCheckDisabledAt = null;
        HealthCheckDisabledReason = null;
    }
}

/// <summary>
/// What triggered a <see cref="ILlmSettingsProvider.SaveProviderAsync"/> call. Controls
/// whether <see cref="LlmProviderSettings.UserEditedAt"/> gets stamped and whether an
/// audit/notification trail should be emitted.
/// </summary>
public enum ProviderSaveSource
{
    /// <summary>Change came from the user via the Settings UI. Stamps UserEditedAt.</summary>
    UserEdit,
    /// <summary>Change came from an autonomous subsystem (self-healer, reflection agent, guided setup).</summary>
    Autonomous,
    /// <summary>
    /// Autonomous provider bootstrap/maintenance that must not treat a provider-level
    /// default as proof for every function. The provider row is saved normally, while
    /// function preferences remain unchanged until their own function-scoped evidence
    /// authorizes a write.
    /// </summary>
    AutonomousProviderOnly,
}

/// <summary>
/// Outcome of <see cref="ILlmSettingsProvider.SaveProviderAsync"/>. Exposes the cascade
/// side-effect (how many function preferences were rewritten to match a new local-provider
/// default) so the caller can tell the user what happened.
/// </summary>
public sealed class SaveProviderResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }

    /// <summary>
    /// Number of FunctionPreferences rows whose preferred/fallback model was rewritten
    /// to match a changed local default. This remains zero for
    /// <see cref="ProviderSaveSource.AutonomousProviderOnly"/>, because those callers
    /// must apply independently proven function routes themselves.
    /// </summary>
    public int CascadedPreferenceCount { get; init; }

    /// <summary>The previous DefaultModel value, for audit logging / notifications.</summary>
    public string? PreviousDefaultModel { get; init; }
}

/// <summary>
/// Outcome of an atomic, default-column-only provider update. This narrow write is
/// used after a function-scoped repair has already committed: it must never replay a
/// stale provider DTO over concurrent endpoint, enabled, health, or priority edits.
/// </summary>
public enum ProviderDefaultConditionalWriteStatus
{
    Applied,
    Conflict,
    Rejected,
    Unsupported,
}

public sealed record ProviderDefaultConditionalWriteResult(
    ProviderDefaultConditionalWriteStatus Status,
    string? CurrentDefaultModel = null,
    string? Error = null)
{
    public bool Applied => Status == ProviderDefaultConditionalWriteStatus.Applied;
}

/// <summary>
/// Exact provider-readiness baseline used by autonomous local-runtime repair. It
/// deliberately includes the user-visible readiness fields as well as the storage
/// revision, so even legacy/direct writers that forget to advance UpdatedAt still
/// invalidate a stale repair.
/// </summary>
public sealed record ProviderReadinessVersion(
    bool Exists,
    string ProviderName,
    string? Endpoint,
    bool IsEnabled,
    int Priority,
    bool DisabledByHealthCheck,
    DateTime? HealthCheckDisabledAt,
    string? HealthCheckDisabledReason,
    bool IsLocalProvider,
    DateTime? LastValidatedAt,
    string? LastValidationError,
    DateTime? UserEditedAt,
    DateTime? UpdatedAt)
{
    public static ProviderReadinessVersion Capture(
        string providerName,
        LlmProviderSettings? settings)
        => settings is null
            ? new ProviderReadinessVersion(
                false,
                providerName,
                null,
                false,
                0,
                false,
                null,
                null,
                false,
                null,
                null,
                null,
                null)
            : new ProviderReadinessVersion(
                true,
                settings.ProviderName,
                settings.Endpoint,
                settings.IsEnabled,
                settings.Priority,
                settings.DisabledByHealthCheck,
                settings.HealthCheckDisabledAt,
                settings.HealthCheckDisabledReason,
                settings.IsLocalProvider,
                settings.LastValidatedAt,
                settings.LastValidationError,
                settings.UserEditedAt,
                settings.UpdatedAt);
}

public enum ProviderReadinessConditionalWriteStatus
{
    Applied,
    NoChange,
    Conflict,
    Rejected,
    Unsupported,
}

public sealed record ProviderReadinessConditionalWriteResult(
    ProviderReadinessConditionalWriteStatus Status,
    string? Error = null)
{
    public bool Ready => Status is ProviderReadinessConditionalWriteStatus.Applied
        or ProviderReadinessConditionalWriteStatus.NoChange;
}

/// <summary>
/// Provider class authorized by an autonomous readiness write. This is explicit so
/// a successful local process probe can never accidentally classify a subscription
/// CLI as private/local compute (or vice versa).
/// </summary>
public enum ProviderReadinessIntent
{
    LocalRuntime,
    SubscriptionCli,
    CredentialedApi,
}

/// <summary>Explicit configuration fields owned by a provider host. Omitted fields retain their current values.</summary>
public sealed record ProviderConfigurationPatch(
    string? Endpoint = null,
    bool? IsEnabled = null,
    int? Priority = null,
    bool? IsLocalProvider = null,
    bool? DisabledByHealthCheck = null,
    IReadOnlyDictionary<string, string>? AdditionalSettings = null);

/// <summary>
/// Service for providing LLM configuration and preferences.
/// Applications implement this to load settings from their preferred storage.
/// </summary>
public interface ILlmSettingsProvider
{
    /// <summary>
    /// Get the preference for a specific function type
    /// </summary>
    Task<LlmFunctionPreference?> GetFunctionPreferenceAsync(string functionType);

    /// <summary>
    /// Cancellation-aware preference read for bounded background maintenance.
    /// Compatibility providers retain their existing implementation while the
    /// wait itself remains cancellable; storage-backed providers should override
    /// this overload and pass the token through to physical I/O.
    /// </summary>
    async Task<LlmFunctionPreference?> GetFunctionPreferenceAsync(
        string functionType,
        CancellationToken cancellationToken)
        => await GetFunctionPreferenceAsync(functionType)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Get all function preferences
    /// </summary>
    Task<List<LlmFunctionPreference>> GetAllFunctionPreferencesAsync();

    /// <summary>
    /// Set or update a function preference while preserving any existing explicit user pin.
    /// User-confirmed actions must call the authority overload explicitly.
    /// </summary>
    Task<bool> SetFunctionPreferenceAsync(LlmFunctionPreference preference)
        => SetFunctionPreferenceAsync(
            preference,
            FunctionPreferenceWriteAuthority.RespectUserPin);

    /// <summary>
    /// Set or update a function preference with explicit write authority. Implementations
    /// must enforce the user-pin check and mutation in one storage transaction or host lock.
    /// </summary>
    Task<bool> SetFunctionPreferenceAsync(
        LlmFunctionPreference preference,
        FunctionPreferenceWriteAuthority authority);

    /// <summary>
    /// Applies a preference only if storage still exactly matches <paramref name="expected"/>.
    /// Implementations must compare and mutate atomically. A read/compare/write sequence
    /// with an unlocked gap is not a valid implementation.
    /// </summary>
    Task<FunctionPreferenceConditionalWriteResult> TrySetFunctionPreferenceAsync(
        LlmFunctionPreference preference,
        FunctionPreferenceWriteAuthority authority,
        FunctionPreferenceVersion expected);

    /// <summary>
    /// Applies a preference only if the exact row for its function still matches
    /// <paramref name="expected"/>. Unlike <see cref="TrySetFunctionPreferenceAsync"/>,
    /// this does not compare the inherited effective route, so a concurrent latent child
    /// edit beneath a parent pin cannot be overwritten by compensation.
    /// </summary>
    Task<FunctionPreferenceConditionalWriteResult> TrySetExactFunctionPreferenceAsync(
        LlmFunctionPreference preference,
        FunctionPreferenceWriteAuthority authority,
        FunctionPreferenceVersion expected);

    /// <summary>
    /// Applies every exact function row in one storage transaction only when
    /// every exact baseline still matches. Implementations that cannot provide
    /// atomic batch semantics must return <see cref="FunctionPreferenceBatchWriteStatus.Unsupported"/>
    /// without changing any row.
    /// </summary>
    Task<FunctionPreferenceBatchWriteResult> TrySetExactFunctionPreferencesAsync(
        IReadOnlyList<FunctionPreferenceBatchWrite> writes,
        FunctionPreferenceWriteAuthority authority,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new FunctionPreferenceBatchWriteResult(
            FunctionPreferenceBatchWriteStatus.Unsupported,
            Error: "This settings provider does not support atomic function-route batches."));

    /// <summary>
    /// Deletes an exact function preference only when that exact row still matches
    /// <paramref name="expected"/>. This is primarily used to compensate a just-created
    /// user route when its terminal audit receipt cannot be committed; a concurrent edit
    /// must win instead of being removed by the compensation.
    /// </summary>
    Task<FunctionPreferenceConditionalWriteResult> TryDeleteFunctionPreferenceAsync(
        string functionType,
        FunctionPreferenceWriteAuthority authority,
        FunctionPreferenceVersion expected);

    /// <summary>
    /// Get provider settings by name
    /// </summary>
    Task<LlmProviderSettings?> GetProviderSettingsAsync(string providerName);

    /// <summary>
    /// Get all enabled provider settings
    /// </summary>
    Task<List<LlmProviderSettings>> GetAllProviderSettingsAsync();

    /// <summary>
    /// Get the default provider name
    /// </summary>
    string GetDefaultProvider();

    /// <summary>
    /// Get the default model for a provider
    /// </summary>
    string GetDefaultModel(string provider);

    /// <summary>
    /// Delete a function preference while preserving any existing explicit user pin.
    /// User-confirmed actions must call the authority overload explicitly.
    /// </summary>
    Task<bool> DeleteFunctionPreferenceAsync(string functionType)
        => DeleteFunctionPreferenceAsync(
            functionType,
            FunctionPreferenceWriteAuthority.RespectUserPin);

    /// <summary>
    /// Deletes a function preference with explicit write authority. Implementations must
    /// enforce the user-pin check and deletion in one storage transaction or host lock.
    /// </summary>
    Task<bool> DeleteFunctionPreferenceAsync(
        string functionType,
        FunctionPreferenceWriteAuthority authority);

    /// <summary>
    /// Save or update provider settings. For local providers, a change to
    /// <see cref="LlmProviderSettings.DefaultModel"/> automatically cascades to every
    /// function preference pointing at the same provider (local providers can only run
    /// one model at a time — divergent function prefs cause load/unload thrashing).
    /// </summary>
    /// <param name="settings">Updated provider settings.</param>
    /// <param name="source">
    /// UserEdit (default) stamps UserEditedAt so autonomous subsystems back off.
    /// Autonomous leaves UserEditedAt alone — used by self-healer / reflection agent.
    /// </param>
    Task<SaveProviderResult> SaveProviderAsync(
        LlmProviderSettings settings,
        ProviderSaveSource source = ProviderSaveSource.UserEdit);

    /// <summary>
    /// Permanently removes a provider row. Intended for derived rows whose owner
    /// went away (an <c>Ollama@node</c> LAN node the user deleted); built-in
    /// providers are disabled, not deleted. Returns false when no row existed.
    /// Stores that do not support deletion keep the default no-op.
    /// </summary>
    Task<bool> DeleteProviderAsync(string providerName, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    /// <summary>Apply explicit configuration fields against the exact read revision, preserving credentials, defaults and limits.</summary>
    Task<ProviderReadinessConditionalWriteResult> TryPatchProviderConfigurationAsync(
        string providerName, ProviderConfigurationPatch patch, ProviderReadinessVersion expected,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ProviderReadinessConditionalWriteResult(
            ProviderReadinessConditionalWriteStatus.Unsupported,
            "This settings provider does not support conditional configuration patches."));

    /// <summary>
    /// Atomically changes only one provider-default column when its current value still
    /// equals <paramref name="expectedDefaultModel"/>. A null replacement may clear the
    /// chat default; embedding defaults must remain explicitly pinned. Implementations
    /// must not rewrite any other provider fields. The fail-closed compatibility
    /// implementation refuses the operation; durable hosts should override it with a
    /// storage-level conditional update.
    /// </summary>
    Task<ProviderDefaultConditionalWriteResult> TrySetProviderDefaultOnlyAsync(
        string providerName,
        string? expectedDefaultModel,
        string? replacementModel,
        bool embeddingModel = false,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ProviderDefaultConditionalWriteResult(
            ProviderDefaultConditionalWriteStatus.Unsupported,
            expectedDefaultModel,
            "This settings provider does not support atomic default-only updates."));

    /// <summary>
    /// Creates or repairs only the local-provider readiness columns when storage
    /// still exactly matches <paramref name="expected"/>. Implementations must not
    /// replay a provider DTO or rewrite defaults, credentials, rate limits, health
    /// observations, or any unrelated provider state. A provider that was disabled
    /// manually (rather than by a health check) must remain disabled.
    /// </summary>
    Task<ProviderReadinessConditionalWriteResult> TryEnsureLocalProviderReadyAsync(
        string providerName,
        string endpointWhenMissing,
        int newProviderPriority,
        ProviderReadinessVersion expected,
        CancellationToken cancellationToken = default)
        => TryEnsureProviderReadyAsync(
            providerName,
            endpointWhenMissing,
            newProviderPriority,
            ProviderReadinessIntent.LocalRuntime,
            expected,
            cancellationToken);

    /// <summary>
    /// Atomically creates or repairs only the readiness state for a provider that
    /// has just passed a live provider-class-specific probe. Existing defaults,
    /// credentials, endpoint, limits, priority, and user metadata must be preserved.
    /// A successful live probe may advance LastValidatedAt and clear its stale
    /// validation error together with the readiness repair.
    /// Subscription rows are registered without guessed endpoints, credentials, or
    /// model defaults. A provider
    /// disabled manually (rather than by a health check) remains disabled.
    /// </summary>
    Task<ProviderReadinessConditionalWriteResult> TryEnsureProviderReadyAsync(
        string providerName,
        string? endpointWhenMissing,
        int newProviderPriority,
        ProviderReadinessIntent intent,
        ProviderReadinessVersion expected,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ProviderReadinessConditionalWriteResult(
            ProviderReadinessConditionalWriteStatus.Unsupported,
            "This settings provider does not support atomic provider-readiness updates."));

    /// <summary>
    /// Atomically pauses an existing subscription CLI provider after an exact,
    /// conclusive readiness probe proves that its executable/session is not
    /// currently routable. The operation must never create a provider row,
    /// overwrite unrelated provider columns, or reinterpret a manual disable as
    /// a health decision. A later exact Ready probe may reactivate the row through
    /// <see cref="TryEnsureProviderReadyAsync"/>.
    /// </summary>
    Task<ProviderReadinessConditionalWriteResult> TrySetSubscriptionCliUnavailableAsync(
        string providerName,
        string safeReason,
        ProviderReadinessVersion expected,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ProviderReadinessConditionalWriteResult(
            ProviderReadinessConditionalWriteStatus.Unsupported,
            "This settings provider does not support atomic subscription-readiness pauses."));

    /// <summary>
    /// Create a function preference row for <paramref name="functionType"/> only if no
    /// exact-match row already exists. Never overwrites — this is "materialize the decision the
    /// router just made" so user and self-healing system have something concrete to edit.
    /// Default impl is a no-op for backward compat; Sqlite provider overrides.
    /// </summary>
    Task<bool> EnsureFunctionPreferenceAsync(
        string functionType,
        string preferredProvider,
        string preferredModel,
        string? fallbackProvider = null,
        string? fallbackModel = null,
        bool requiresVision = false)
        => Task.FromResult(false);

    /// <summary>
    /// Reads the durable proof-bound survival set for one exact automatic function
    /// route. Implementations must return null when the set is stale, malformed,
    /// expired, attached beneath a user pin, or no longer matches the exact primary /
    /// fallback preference. Callers must still perform live request admission before
    /// executing any returned route.
    /// </summary>
    Task<LlmRouteSurvivalSet?> GetRouteSurvivalSetAsync(
        string functionType,
        CancellationToken cancellationToken = default)
        => Task.FromResult<LlmRouteSurvivalSet?>(null);

    /// <summary>
    /// Persists an ordered survival set only if the exact preference still matches
    /// <paramref name="expected"/>. The preference baseline check, survival-set row,
    /// and append-only evidence receipt must commit in one storage transaction.
    /// Compatibility implementations fail closed without changing state.
    /// </summary>
    Task<LlmRouteSurvivalSetWriteResult> TrySetRouteSurvivalSetAsync(
        LlmRouteSurvivalSet survivalSet,
        FunctionPreferenceWriteAuthority authority,
        FunctionPreferenceVersion expected,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new LlmRouteSurvivalSetWriteResult(
            LlmRouteSurvivalSetWriteStatus.Unsupported,
            Error: "This settings provider does not support durable route survival sets."));
}
