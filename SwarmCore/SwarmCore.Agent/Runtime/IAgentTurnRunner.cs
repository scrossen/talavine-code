using System.Collections.Frozen;

namespace SwarmCore.Agent.Runtime;

/// <summary>
/// Explicit communication-data authority for an agent turn. Core callers must
/// deliberately choose no communication access, named source accounts, or a
/// trusted profile-wide surface. User-request entry points use the explicit
/// <see cref="UserRequested"/> factory; malformed direct runner requests still
/// fail closed.
/// </summary>
public sealed class AgentCommunicationScope
{
    private static readonly IReadOnlySet<Guid> EmptyAccounts = Array.Empty<Guid>().ToFrozenSet();

    private AgentCommunicationScope(
        AgentCommunicationScopeKind kind,
        IReadOnlySet<Guid> accountIds,
        string authority,
        bool explicitlyWidened,
        bool userInitiated)
    {
        Kind = kind;
        AccountIds = accountIds;
        Authority = NormalizeAuthority(authority);
        ExplicitlyWidened = explicitlyWidened;
        UserInitiated = userInitiated;
    }

    public AgentCommunicationScopeKind Kind { get; }
    public IReadOnlySet<Guid> AccountIds { get; }
    public string Authority { get; }
    public bool ExplicitlyWidened { get; }

    /// <summary>
    /// True when this scope came from a direct user request or a persisted
    /// user-approved continuation (for example a scheduled follow-up). It
    /// allows bounded work to continue after the initiating UI turn ends.
    /// </summary>
    public bool UserInitiated { get; }

    public static AgentCommunicationScope None { get; } = new(
        AgentCommunicationScopeKind.None,
        EmptyAccounts,
        "none",
        explicitlyWidened: false,
        userInitiated: false);

    public static AgentCommunicationScope ForAccounts(IEnumerable<Guid> accountIds, string authority)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        if (string.IsNullOrWhiteSpace(authority))
            throw new ArgumentException("Communication-scope authority is required.", nameof(authority));

        var normalized = accountIds
            .Where(static id => id != Guid.Empty)
            .ToFrozenSet();
        return normalized.Count == 0
            ? None
            : new AgentCommunicationScope(
                AgentCommunicationScopeKind.SelectedAccounts,
                normalized,
                authority.Trim(),
                explicitlyWidened: false,
                userInitiated: false);
    }

    public static AgentCommunicationScope ForAccount(Guid accountId, string authority)
        => ForAccounts([accountId], authority);

    public static AgentCommunicationScope TrustedProfileWide(
        string authority,
        bool explicitlyWidened = false,
        bool userInitiated = false)
    {
        if (string.IsNullOrWhiteSpace(authority))
            throw new ArgumentException("Profile-wide communication authority is required.", nameof(authority));

        return new AgentCommunicationScope(
            AgentCommunicationScopeKind.TrustedProfileWide,
            EmptyAccounts,
            authority.Trim(),
            explicitlyWidened,
            userInitiated);
    }

    /// <summary>
    /// Profile-wide access implied by a direct, authenticated user request.
    /// This is recorded as an explicit widening so downstream audit and
    /// explanation surfaces can show why the agent was allowed to cross
    /// account boundaries.
    /// </summary>
    public static AgentCommunicationScope UserRequested(string authority)
        => TrustedProfileWide(authority, explicitlyWidened: true, userInitiated: true);

    /// <summary>
    /// A selected-account scope that was explicitly approved by the user for
    /// work that may continue after the initiating turn (scheduled task,
    /// follow-up, or another durable continuation).
    /// </summary>
    public static AgentCommunicationScope UserApprovedAccounts(
        IEnumerable<Guid> accountIds,
        string authority)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        if (string.IsNullOrWhiteSpace(authority))
            throw new ArgumentException("Communication-scope authority is required.", nameof(authority));

        var normalized = accountIds
            .Where(static id => id != Guid.Empty)
            .ToFrozenSet();
        return normalized.Count == 0
            ? None
            : new AgentCommunicationScope(
                AgentCommunicationScopeKind.SelectedAccounts,
                normalized,
                authority.Trim(),
                explicitlyWidened: false,
                userInitiated: true);
    }

    private static string NormalizeAuthority(string authority)
    {
        var trimmed = authority.Trim();
        if (trimmed.Length > 80)
            trimmed = trimmed[..80];

        return new string(trimmed
            .Select(static ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' or ':' ? ch : '-')
            .ToArray());
    }

    /// <summary>
    /// Converts to the repository boundary representation. Null is emitted
    /// only for an explicitly constructed trusted profile-wide scope.
    /// </summary>
    public IReadOnlySet<Guid>? ToAllowedSourceAccountIds()
        => Kind == AgentCommunicationScopeKind.TrustedProfileWide ? null : AccountIds;
}

public enum AgentCommunicationScopeKind
{
    None = 0,
    SelectedAccounts = 1,
    TrustedProfileWide = 2,
}

/// <summary>
/// Runs one chat turn end-to-end: resolves the conversation + profile,
/// hydrates the transcript, invokes the tool-use engine, streams
/// <see cref="TurnEvent"/>s, and persists the result.
/// <para>
/// The only component that owns the "a user sent a message" lifecycle.
/// Scheduled tasks, external entry points, and the chat facade all call
/// this instead of re-implementing the plumbing.
/// </para>
/// </summary>
public interface IAgentTurnRunner
{
    IAsyncEnumerable<TurnEvent> RunAsync(AgentTurnRequest request, CancellationToken ct = default);
}

/// <summary>
/// Inputs to <see cref="IAgentTurnRunner.RunAsync"/>. Kept as a record so
/// additional fields (attachment, reply-to, scheduled metadata) can be added
/// without breaking call sites.
/// </summary>
public sealed record AgentTurnRequest
{
    /// <summary>Explicit host-selected limits also apply to attended work; null preserves the profile/default policy.</summary>
    public AgentTurnExecutionLimits? ExecutionLimits { get; init; }
    /// <summary>Trusted host contract for verified reusable execution; never supplied by model arguments.</summary>
    public SwarmCore.Agent.Procedures.AgentProcedureContext? ProcedureContext { get; init; }
    public SwarmCore.Agent.Procedures.AgentProcedureJob? ProcedureJob { get; init; }
    /// <summary>Optional host-only fresh authority source for an explicitly supplied procedure context.
    /// Without this or the registered job provider, a pending candidate cannot be committed.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<CancellationToken, Task<SwarmCore.Agent.Procedures.AgentProcedureCurrentAuthority>>?
        RefreshProcedureAuthorityAsync { get; init; }
    /// <summary>Host-created employee scope. AgentTurnRunner binds its exact provider context before model execution.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SwarmCore.Agent.Learning.AgentRoutineLearningTurnScope? RoutineLearningTurnScope { get; init; }
    public SwarmCore.Abstractions.LlmRouting.LlmModelRequest? ModelRequest { get; init; }
    public required string ConversationId { get; init; }
    public required string UserMessage { get; init; }
    /// <summary>
    /// Original authenticated user instruction supplied by a host that wraps
    /// UserMessage with continuation, receipts or model-authored remaining work.
    /// Never take this field from model/tool metadata. Null uses the direct request.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? AuthenticatedOriginalUserMessage { get; init; }

    /// <summary>
    /// Required communication-data authority for this turn. A null supplied
    /// through reflection or an unsafe caller is treated as <see cref="AgentCommunicationScope.None"/>.
    /// </summary>
    public required AgentCommunicationScope CommunicationScope { get; init; }

    /// <summary>When null, the conversation's configured profile is used; when that's also null, the default profile.</summary>
    public Guid? ProfileIdOverride { get; init; }

    /// <summary>Optional per-request model override (for the "try this with GPT" UX).</summary>
    public string? ProviderOverride { get; init; }
    public string? ModelOverride { get; init; }
    /// <summary>Keep the exact route supplied by the host; do not use lower-level provider/model fallbacks.</summary>
    public bool DisableRouteFallback { get; init; }

    /// <summary>
    /// The person asked for the conversation's working context to be compacted before this turn runs (a host's
    /// <c>/compact</c>). The engine summarizes the provider-facing history once, before the first model call, when
    /// there is enough history to compact; the durable transcript is never rewritten. Host state, never model output.
    /// </summary>
    public bool CompactWorkingContextFirst { get; init; }

    /// <summary>
    /// Legacy single-image attachment. Prefer <see cref="Images"/>; when both
    /// are set, <see cref="Images"/> wins. Kept so existing single-image
    /// callers don't break.
    /// </summary>
    public string? ImageBase64 { get; init; }

    /// <summary>IANA media type for <see cref="ImageBase64"/> (e.g. <c>image/png</c>).</summary>
    public string? ImageMediaType { get; init; }

    /// <summary>
    /// Image attachments carried with the user turn. When non-empty, the
    /// runner constructs a multi-part user <c>ChatMessage</c> (text + one
    /// ImagePart per image) and the provider translators forward them to the
    /// vision wire format. Caller is responsible for selecting a
    /// vision-capable model — the runner does NOT validate model capability
    /// here, since the translator silently drops images when the model
    /// doesn't support them rather than failing the turn.
    /// </summary>
    public IReadOnlyList<TurnImageAttachment>? Images { get; init; }

    /// <summary>
    /// Vision-derived text for <see cref="Images"/>, in matching order. When a
    /// description is present the runner sends that untrusted evidence as text
    /// instead of requiring the tool-use model itself to support vision. The
    /// original image is still persisted on the user turn for display and audit.
    /// </summary>
    public IReadOnlyList<TurnImageDescription>? ImageDescriptions { get; init; }

    /// <summary>
    /// <see cref="Images"/> when set, else the legacy single-image pair
    /// lifted into a one-element list, else empty. The runner and store only
    /// ever read this.
    /// </summary>
    public IReadOnlyList<TurnImageAttachment> EffectiveImages =>
        Images is { Count: > 0 }
            ? Images
            : !string.IsNullOrEmpty(ImageBase64) && !string.IsNullOrEmpty(ImageMediaType)
                ? new[] { new TurnImageAttachment(ImageBase64, ImageMediaType) }
                : Array.Empty<TurnImageAttachment>();

    /// <summary>
    /// Text-file attachments (CSV, JSON, plain text, …) carried with the user
    /// turn. The runner injects each file's content into the user message as
    /// a fenced block, so any model can read the data — no vision capability
    /// required.
    /// </summary>
    public IReadOnlyList<TurnFileAttachment>? Files { get; init; }

    /// <summary><see cref="Files"/> or empty; the runner and store only ever read this.</summary>
    public IReadOnlyList<TurnFileAttachment> EffectiveFiles =>
        Files ?? Array.Empty<TurnFileAttachment>();

    /// <summary>
    /// Repository-facing source-account boundary derived from the required,
    /// typed <see cref="CommunicationScope"/>. Null can only mean a caller
    /// explicitly selected trusted profile-wide authority.
    /// </summary>
    public IReadOnlySet<Guid>? AllowedSourceAccountIds
        => (CommunicationScope ?? AgentCommunicationScope.None).ToAllowedSourceAccountIds();

    /// <summary>
    /// Immutable view snapshot captured by the surface that initiated this
    /// turn. This must never be inferred from an app-wide last-writer value,
    /// because multiple windows can be active at the same time.
    /// </summary>
    public CurrentViewSnapshot? CurrentView { get; init; }

    /// <summary>
    /// Host-declared routing demand for this turn, derived by the surface that
    /// started it from its own metadata (for example, an evidence workspace
    /// declares high-stakes work). Never derived from model output or from
    /// message content. Null leaves demand to size and interactivity alone.
    /// </summary>
    public SwarmCore.Abstractions.LlmRouting.LlmTaskDemand? RoutingDemand { get; init; }

    /// <summary>
    /// True only when an interactive caller recognized an explicit request to
    /// widen a previously account-bound communication search.
    /// </summary>
    public bool CommunicationScopeExplicitlyWidened
        => (CommunicationScope ?? AgentCommunicationScope.None).ExplicitlyWidened;

    /// <summary>
    /// True when this turn belongs to a direct user request or a durable
    /// continuation the user approved. Background execution alone never sets
    /// this bit.
    /// </summary>
    public bool CommunicationScopeUserInitiated
        => (CommunicationScope ?? AgentCommunicationScope.None).UserInitiated;

    /// <summary>Freeform metadata appended to the turn record for analytics/debug.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>
    /// Trusted identity of the autonomous agent this turn runs on behalf of
    /// (e.g. a worker's email address). Flows into
    /// <see cref="CurrentTurnContext.ActingAgentId"/> so agent-partitioned
    /// tools can key their storage partition off the execution context rather
    /// than model-supplied input. Only set this from caller-owned provisioning
    /// state — never from anything the model wrote. Null for ordinary chat.
    /// </summary>
    public string? ActingAgentId { get; init; }

    /// <summary>
    /// True when no human is watching this turn run: an autonomous agent
    /// cycle, a scheduled task firing on a timer, or any other background
    /// continuation. Orthogonal to <see cref="CommunicationScopeUserInitiated"/> —
    /// a boss-provisioned worker cycle carries the user's standing authority
    /// (userInitiated=true) yet nobody is present to notice a hijacked turn.
    /// Escape hatches that trade sandboxing for "the user is right there and
    /// asked for this" (the code-execution <c>app</c> DI hatch, the
    /// approval-tier auto-grant) must stay closed when this is set. Set it
    /// from caller-owned provisioning state, never from model output.
    /// </summary>
    public bool UnattendedAgentTurn { get; init; }

    /// <summary>
    /// Host-granted authority that lets an unattended repair turn execute
    /// side-effecting tools (shell, file writes, local routing control)
    /// without a consent card, under <see cref="RepairAuthorityPolicy"/>. This
    /// is the deliberate, bounded exception to the "escape hatches stay
    /// closed when unattended" rule above. Null for every ordinary turn. Set
    /// only from caller-owned host state, never from model output.
    /// </summary>
    public AgentRepairAuthority? RepairAuthority { get; init; }
    /// <summary>Trusted host identity for a durable mission; never supplied through tool input.</summary>
    public Guid? MissionObjectiveId { get; init; }
    /// <summary>Trusted host reconstruction after a mission source-scope change. The current
    /// request carries authoritative work and eligible receipt references; persisted historical
    /// transcript remains intact but must not be replayed into the provider projection.</summary>
    public bool ReconstructMissionContext { get; init; }
    public SwarmCore.Agent.ToolUseEngine.AdaptiveAgentContext? AdaptiveReasoning { get; init; }
    public SwarmCore.Agent.ToolUseEngine.WorkingContextAuthority? WorkingContextAuthority { get; init; }
    public IReadOnlyList<SwarmCore.Agent.Continual.MissionObservedState> WorkingContextObservedStates { get; init; } = [];
    public Func<CancellationToken, Task<SwarmCore.Agent.ToolUseEngine.WorkingContextAuthority>>? RefreshWorkingContextAuthorityAsync { get; init; }

    public Func<CancellationToken, Task<IReadOnlyList<SwarmCore.Agent.Continual.MissionObservedState>>>? RefreshWorkingContextObservedStatesAsync { get; init; }

    public Func<IReadOnlyList<string>, CancellationToken, Task<bool>>? ValidateWorkingContextSourceCallsAsync { get; init; }

    /// <summary>
    /// Host-selected file workspace for this turn (for example a coding conversation's repository).
    /// Flows into <see cref="CurrentTurnContext.WorkspaceRoot"/> and
    /// <see cref="ContextInjectionRequest.WorkspaceRoot"/>, so sandbox-resolving tools treat it as
    /// their default root and fence relative paths to it for this turn only. Null keeps the host's
    /// configured sandbox root. Set it only from host state, never from model output.
    /// </summary>
    public string? WorkspaceRoot { get; init; }

    public int MissionAttempt { get; init; }
    public string? MissionBudgetReference { get; init; }
    public SwarmCore.Agent.Continual.MissionAuthority? MissionResources { get; init; }
    public Func<string, bool, CancellationToken, Task>? RecordMissionPermissionAttempt { get; init; }
    /// <summary>Immutable authenticated intent from the mission store, independent of continuation text.</summary>
    public string? MissionOriginalUserIntent { get; init; }
    /// <summary>Host-owned durable revocation/lease check, rebuilt on each mission execution.</summary>
    public Func<CancellationToken, Task>? ValidateMissionExecution { get; init; }
    /// <summary>Host-only expected writable artifact version; null selects ordinary root behavior.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<string, CancellationToken, Task<string?>>? GetMissionWriteBaseAsync { get; init; }
    /// <summary>Trusted host checks persisted pending work after tool receipts are durable; never supplied by a model.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<CancellationToken, Task<string?>>? GetMissionTurnYieldReasonAsync { get; init; }
    /// <summary>Host receipt preflight: a nonnull explanation rejects dispatch recoverably; it does not replace the durable start fence.</summary>
    public Func<string, string, CancellationToken, Task<string?>>? ValidateMissionToolDispatchAsync { get; init; }

    /// <summary>
    /// True when the host is resuming an already-persisted objective rather
    /// than presenting a new user-authored request. The prompt still enters the
    /// transcript so the model can resume coherently, but chat surfaces may
    /// hide the host-authored nudge and render the answer as an automatic
    /// continuation of the original turn.
    /// </summary>
    public bool IsContinuation { get; init; }

    /// <summary>
    /// Tool ids to withhold from this turn even when the resolved profile
    /// enables them. Lets callers that share one profile across differently
    /// privileged agents (e.g. the employee tool-turn profile) gate individual
    /// tools per turn. Null or empty means no per-turn exclusions.
    /// </summary>
    public IReadOnlyCollection<string>? ExcludedToolIds { get; init; }
}

public sealed record AgentTurnExecutionLimits(int MaxIterations, TimeSpan HardTimeout, int MaxCumulativeTokens);

/// <summary>
/// Text extracted from one user image by a dedicated vision route before the
/// tool-capable assistant turn begins.
/// </summary>
public sealed record TurnImageDescription(
    string? FileName,
    string Description,
    TurnImageAnalysisStatus Status = TurnImageAnalysisStatus.Analyzed);

/// <summary>
/// Whether a pre-description actually inspected image pixels. This survives
/// into deterministic output guards so a model cannot turn a placeholder into
/// a visual claim or a dead-end failure message.
/// </summary>
public enum TurnImageAnalysisStatus
{
    Analyzed = 0,
    NoUsableRoute = 1,
    RouteFailed = 2,
    EmptyResponse = 3,
}

/// <summary>
/// Optional host seam: the workspace a turn runs in when its <see cref="AgentTurnRequest"/>
/// carries none. <see cref="AgentTurnRunner"/> asks it for every such turn and never inherits a
/// parent turn's workspace. A host returns a root only for turns that belong to its own workspace
/// executor (for example a continuation of a coding objective), and null for everything else —
/// sub-agents, missions, attachment turns and unbound conversations. Never derive the value from model output.
/// </summary>
public interface IAgentTurnWorkspaceResolver
{
    ValueTask<string?> ResolveWorkspaceRootAsync(AgentTurnRequest request, CancellationToken ct = default);
}

/// <summary>
/// Optional host seam: narrows the tools a turn may use after its profile resolved them, from host-owned
/// request state (metadata, authority, attendance, workspace) — for example a coding executor's turns get
/// only the coding harness and read-only context tools. <see cref="AgentTurnRunner"/> applies every
/// registered filter in order, before routing and tool discovery, on the initial list and on each refresh
/// of the discoverable list. A filter can only remove tools: anything it returns that the profile did not
/// resolve is dropped. A filter that throws fails the turn's preparation (never "all tools").
/// </summary>
public interface IAgentTurnToolFilter
{
    ValueTask<IReadOnlyList<SwarmCore.Agent.Catalog.ToolDescriptor>> FilterAsync(AgentTurnRequest request,
        SwarmCore.Agent.ToolUseEngine.AgentProfileSnapshot profile,
        IReadOnlyList<SwarmCore.Agent.Catalog.ToolDescriptor> tools, CancellationToken ct = default);
}
