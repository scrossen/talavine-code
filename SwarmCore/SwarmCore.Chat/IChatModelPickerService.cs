namespace SwarmCore.Chat;

/// <summary>The model a chat surface should use right now, and whether it can see images.</summary>
public sealed record ChatModelChoice(string Provider, string Model, bool SupportsVision)
{
    public string? ReasoningEffort { get; init; }
}

/// <summary>One selectable entry for a model-picker dropdown.</summary>
public sealed record ChatModelCandidate(string Provider, string Model, bool SupportsVision, bool IsRecommended);

/// <summary>
/// Shared "which model should this chat surface use" logic: remember the user's
/// last choice per surface, or seed a sensible (optionally vision-capable) default
/// the first time. Every chat surface across every host app resolves through this
/// same service so behavior — and bug fixes — are shared, not reimplemented per app.
/// </summary>
public interface IChatModelPickerService
{
    /// <summary>
    /// Resolve the model this surface should use: the user's last choice for
    /// <paramref name="functionType"/> if one exists, otherwise a freshly selected
    /// default (constrained to a vision-capable model when <paramref name="preferVision"/>
    /// is true) which is persisted so it becomes the new "last choice".
    /// </summary>
    /// <param name="functionType">
    /// The router function type this surface's chat calls already use for generation
    /// (e.g. <c>"vibe-builder"</c>) — the SAME preference the real call resolves, so
    /// picking a model here actually changes what that call uses by default. A brand
    /// new surface with no established function type may invent its own stable string
    /// (e.g. <c>"vibes-assistant-chat"</c>); follow the existing dash-separated,
    /// lowercase convention rather than introducing a new namespacing scheme.
    /// </param>
    Task<ChatModelChoice> ResolveAsync(
        string functionType,
        string seedPrompt,
        bool preferVision,
        bool requiresStructuredOutput = false,
        bool allowTextJsonContractRepair = false,
        CancellationToken ct = default);

    /// <summary>Record the user's explicit pick for this surface as the new "last choice".</summary>
    Task SetChoiceAsync(
        string functionType,
        string provider,
        string model,
        bool requiresVision = false,
        bool requiresStructuredOutput = false,
        bool allowTextJsonContractRepair = false,
        CancellationToken ct = default);

    /// <summary>List configured models for a picker dropdown, decorated with vision support.</summary>
    Task<IReadOnlyList<ChatModelCandidate>> ListCandidatesAsync(string functionType, CancellationToken ct = default);
}
