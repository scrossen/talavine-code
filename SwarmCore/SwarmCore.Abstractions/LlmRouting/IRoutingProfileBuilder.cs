namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// One proposed per-function preference. The user reviews these as a diff against the
/// current preferences and can apply individually or all at once.
/// </summary>
public sealed record RoutingProfileFunctionRow(
    string FunctionType,
    string ProposedProvider,
    string ProposedModel,
    string? ProposedFallbackProvider,
    string? ProposedFallbackModel,
    string? CurrentProvider,
    string? CurrentModel,
    string Reason);

/// <summary>
/// One proposed change to the global routing policy (budget cap, quiet hours, thresholds).
/// Carried alongside the function rows so the configurator's recommendation is
/// "here's the whole picture", not just per-function edits.
/// </summary>
public sealed record RoutingProfilePolicyChange(
    RoutingPolicy ProposedPolicy,
    RoutingPolicy CurrentPolicy,
    string Reason);

/// <summary>
/// A complete profile proposal from the configurator agent. The router does not apply
/// this automatically — the user reviews and confirms via the settings UI. The proposal
/// is idempotent: applying it twice yields the same configuration.
/// </summary>
public sealed record RoutingProfileProposal(
    IReadOnlyList<RoutingProfileFunctionRow> FunctionRows,
    RoutingProfilePolicyChange? PolicyChange,
    IReadOnlyList<string> Notes,
    DateTimeOffset GeneratedAt,
    string? AuditCorrelationId = null);

/// <summary>
/// The configurator agent. Inspects the user's installed providers, subscriptions,
/// budget, hardware, calibration results, and recent activity, and produces a proposed
/// baseline configuration — both the per-function preference matrix and the global
/// <see cref="RoutingPolicy"/>.
///
/// This is intentionally a separate concept from <see cref="IModelScout"/>: the scout
/// keeps a live catalog of "what models exist and how do they score"; the builder
/// answers "given that catalog and what the user has set up, what should each function
/// actually point at."
///
/// Runs on demand (user clicks "Refresh recommendations") or weekly (a hosted service
/// can schedule it; not built into SwarmCore so the timing stays app-owned).
/// </summary>
public interface IRoutingProfileBuilder
{
    Task<RoutingProfileProposal> BuildAsync(CancellationToken ct = default);

    /// <summary>
    /// Apply a previously-built proposal to <see cref="ILlmSettingsProvider"/> +
    /// <see cref="IRoutingPolicyStore"/>. Explicit user-pinned function routes are left
    /// unchanged. Idempotent. Returns the number of preference rows that actually changed.
    /// </summary>
    Task<int> ApplyAsync(RoutingProfileProposal proposal, CancellationToken ct = default);
}
