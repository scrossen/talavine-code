using SwarmCore.Abstractions.LlmRouting;
using SwarmCore.Agent.Runtime;

namespace SwarmCore.PersonalMemory;

/// <summary>Host configuration. Defaults are safe for every app; hosts override from their settings.</summary>
public sealed class PersonalMemoryOptions
{
    /// <summary>Global/admin switch. False makes capture, recall, tools and consolidation no-ops.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Router function type used for extraction and consolidation (background interactivity).</summary>
    public string FunctionType { get; set; } = FunctionTypes.KnowledgeExtraction;

    /// <summary>Privacy class for memory LLM calls (hosts may force LocalOnly).</summary>
    public LlmPrivacyClass PrivacyClass { get; set; } = LlmPrivacyClass.Unspecified;

    /// <summary>
    /// Optional route pin for every memory LLM call (extraction, consolidation, conversation summaries). Hosts
    /// whose router has no automatic selection (no Model Scout) must set both, or those calls find no route and
    /// only the deterministic paths run. Empty means automatic routing.
    /// </summary>
    public string? RouteProvider { get; set; }

    /// <summary>Model for <see cref="RouteProvider"/>; ignored unless both are set.</summary>
    public string? RouteModel { get; set; }

    /// <summary>
    /// Applies <see cref="RouteProvider"/>/<see cref="RouteModel"/> to a memory call's router options.
    /// The pin goes on a copy: hosts may pass, and reuse, their own options instance.
    /// </summary>
    public LlmRouterOptions ApplyRoute(LlmRouterOptions routing)
    {
        ArgumentNullException.ThrowIfNull(routing);
        if (string.IsNullOrWhiteSpace(RouteProvider) || string.IsNullOrWhiteSpace(RouteModel)
            || !string.IsNullOrWhiteSpace(routing.OverrideProvider)) return routing;
        routing = routing.Clone();
        routing.OverrideProvider = RouteProvider.Trim();
        routing.OverrideModel = RouteModel.Trim();
        // A memory route is configured explicitly by the person or their administrator (like a pinned model), so the
        // router honors it as an exact pin instead of requiring automatic-selection proof it may not have.
        routing.OverrideProvenance = LlmOverrideProvenance.User;
        return routing;
    }

    public MemoryAdmissionOptions Admission { get; set; } = new();
    public MemoryDigestOptions Digest { get; set; } = new();
    public MemoryConsolidationOptions Consolidation { get; set; } = new();
    public MemoryRecallOptions Recall { get; set; } = new();
}

public sealed class MemoryAdmissionOptions
{
    public int MinTextLength { get; set; } = 8;
    public int MaxTextLength { get; set; } = 280;
    public int MaxSummaryLength { get; set; } = 600;
    public double MinConfidence { get; set; } = 0.3;
}

public sealed class MemoryDigestOptions
{
    public int TotalCharBudget { get; set; } = 2400;
    public int IdentityCharBudget { get; set; } = 900;
    public int ResponsePreferencesCharBudget { get; set; } = 500;
    public int ProjectsAndPlansCharBudget { get; set; } = 600;
    public int RecentConversationsCharBudget { get; set; } = 500;
    public double MinConfidence { get; set; } = 0.5;

    /// <summary>Sensitive facts (health, finances, identifiers) stay out of the always-on digest by default.</summary>
    public bool IncludeSensitive { get; set; }
    public bool IncludeSourceReferences { get; set; } = true;

    /// <summary>Plans whose event is older than this are no longer "current".</summary>
    public TimeSpan PlanGrace { get; set; } = TimeSpan.FromDays(2);

    /// <summary>Conversation summaries older than this are excluded from the digest.</summary>
    public TimeSpan RecentConversationWindow { get; set; } = TimeSpan.FromDays(30);

    public int BudgetFor(MemoryDigestSection section) => section switch
    {
        MemoryDigestSection.Identity => IdentityCharBudget,
        MemoryDigestSection.ResponsePreferences => ResponsePreferencesCharBudget,
        MemoryDigestSection.ProjectsAndPlans => ProjectsAndPlansCharBudget,
        MemoryDigestSection.RecentConversations => RecentConversationsCharBudget,
        _ => 0,
    };
}

public sealed class MemoryConsolidationOptions
{
    public bool UseLlm { get; set; } = true;
    public int MaxClaimsPerRun { get; set; } = 40;
    public int MaxAwaitingClaimsConsidered { get; set; } = 200;
    public int MaxFactsInPrompt { get; set; } = 60;
    public int MaxPromptChars { get; set; } = 24_000;
    public int MaxOperations { get; set; } = 80;
    public int MaxLlmOutputTokens { get; set; } = 2_000;

    /// <summary>Unverified third-party claims older than this are rejected (not deleted).</summary>
    public TimeSpan UnverifiedClaimLifetime { get; set; } = TimeSpan.FromDays(120);

    /// <summary>Conversation summaries expire after this.</summary>
    public TimeSpan ConversationSummaryLifetime { get; set; } = TimeSpan.FromDays(45);

    /// <summary>When no LLM rewrite happened, a plan this long past its event is expired (retained as history).</summary>
    public TimeSpan PastPlanExpiry { get; set; } = TimeSpan.FromDays(7);
}

public sealed class MemoryRecallOptions
{
    /// <summary>Name the injector registers under; profiles list it to enable recall.</summary>
    public string InjectorName { get; set; } = PersonalMemoryContextInjector.DefaultName;
    public int RelevantFactLimit { get; set; } = 6;
    public int RelevantCharBudget { get; set; } = 900;
    public double MinimumRelevanceScore { get; set; } = 2.5;
}

/// <summary>
/// Host-owned availability: per-user opt-out, tenant admin switch, plan entitlements.
/// Combined with <see cref="PersonalMemoryOptions.Enabled"/>.
/// </summary>
public interface IPersonalMemoryAvailability
{
    ValueTask<bool> IsEnabledAsync(MemoryScope scope, CancellationToken ct = default);
}

public sealed class AlwaysEnabledPersonalMemoryAvailability : IPersonalMemoryAvailability
{
    public ValueTask<bool> IsEnabledAsync(MemoryScope scope, CancellationToken ct = default) => ValueTask.FromResult(true);
}

/// <summary>
/// Per-turn memory context supplied by the host. <see cref="Incognito"/> (temporary chat)
/// makes capture, recall and every memory tool a no-op for the turn.
/// </summary>
/// <param name="AuthenticatedUserMessage">The person's own message for this turn, as authenticated by the host.
/// Explicit "remember" requests must be supported by these words. Never tool or retrieved content.</param>
public sealed record MemoryTurnContext(
    MemoryScope Scope,
    bool Incognito = false,
    string? AuthenticatedUserMessage = null,
    string? ConversationId = null,
    string? TurnId = null);

/// <summary>
/// Resolves the memory context for a turn. <paramref name="request"/> is non-null when called
/// from the context injector; tools call with null and the host returns the ambient turn.
/// Returning null disables memory for the call (fail closed).
/// </summary>
public interface IPersonalMemoryTurnContextProvider
{
    MemoryTurnContext? Resolve(ContextInjectionRequest? request);
}

/// <summary>
/// AsyncLocal ambient turn context hosts can set around a turn:
/// <c>using var _ = AmbientMemoryTurnContext.Begin(ctx);</c>
/// </summary>
public static class AmbientMemoryTurnContext
{
    private static readonly AsyncLocal<MemoryTurnContext?> Ambient = new();

    public static MemoryTurnContext? Current => Ambient.Value;

    public static IDisposable Begin(MemoryTurnContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var previous = Ambient.Value;
        Ambient.Value = context;
        return new Restore(previous);
    }

    private sealed class Restore(MemoryTurnContext? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Ambient.Value = previous;
        }
    }
}

/// <summary>Provider that returns <see cref="AmbientMemoryTurnContext.Current"/>, optionally mapping injector requests.</summary>
public sealed class AmbientPersonalMemoryTurnContextProvider(
    Func<ContextInjectionRequest, MemoryTurnContext?>? fromRequest = null) : IPersonalMemoryTurnContextProvider
{
    public MemoryTurnContext? Resolve(ContextInjectionRequest? request)
        => request is not null && fromRequest is not null
            ? fromRequest(request)
            : AmbientMemoryTurnContext.Current;
}
