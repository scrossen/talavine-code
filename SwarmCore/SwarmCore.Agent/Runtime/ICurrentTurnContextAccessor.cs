namespace SwarmCore.Agent.Runtime;

/// <summary>
/// Ambient access to the turn currently being run, so tools invoked deep inside
/// the tool-use engine can learn which conversation/turn they belong to without
/// threading that context through the <see cref="ToolUseEngine.ToolDescriptor"/>
/// invoke signature (which has no per-call context parameter).
///
/// <para><see cref="AgentTurnRunner"/> sets <see cref="Current"/> at the start of
/// a turn and clears it when the turn ends. Implementations MUST use
/// <see cref="System.Threading.AsyncLocal{T}"/> so the value flows down the
/// async call chain (runner → engine → tool invoke) without leaking across
/// concurrent turns.</para>
/// </summary>
public interface ICurrentTurnContextAccessor
{
    CurrentTurnContext? Current { get; set; }
}

/// <summary>The conversation/turn a tool call is executing within.</summary>
/// <param name="ConversationId">The active conversation id.</param>
/// <param name="TurnId">The UI turn row id (<c>AgentChatTurn.Id</c>) authoring this turn, if known.</param>
/// <param name="ProfileId">The resolved profile driving the turn, if known.</param>
/// <param name="Provider">The effective LLM provider driving the turn (profile + per-request override), if known.</param>
/// <param name="Model">The effective model id driving the turn (profile + per-request override), if known.</param>
/// <param name="AllowedSourceAccountIds">Optional communication-source boundary inherited from the operation that started the turn.</param>
/// <param name="UserMessage">The current user request, retained only in the ambient turn flow so tool access can enforce turn policy.</param>
/// <param name="ToolAccessState">Mutable, turn-local counters used to enforce bounded tool access.</param>
/// <param name="CommunicationScopeKind">The explicit communication authority class selected by the caller.</param>
/// <param name="CommunicationScopeAuthority">Sanitized caller authority explaining why this scope was granted.</param>
/// <param name="CommunicationScopeUserInitiated">Whether the user requested or approved durable continuation of this scope.</param>
/// <param name="ActingAgentId">
/// Trusted identity of the autonomous agent this turn runs on behalf of (e.g. a
/// worker's email address), supplied by the runner from
/// <see cref="AgentTurnRequest.ActingAgentId"/>. Agent-partitioned tools (agent
/// memory) key their storage partition off this value; it must NEVER be
/// derived from model-supplied tool input, so one agent cannot name another
/// agent's partition. Null on ordinary user chat turns.
/// </param>
/// <param name="UnattendedAgentTurn">
/// True when no human is watching this turn run (agent work cycle, scheduled
/// task, background continuation) — see
/// <see cref="AgentTurnRequest.UnattendedAgentTurn"/>. Consumers should gate
/// on <see cref="IsUnattended"/>, which also folds in
/// <paramref name="ActingAgentId"/>.
/// </param>
public sealed record CurrentTurnContext(
    string ConversationId,
    Guid? TurnId,
    Guid? ProfileId,
    string? Provider = null,
    string? Model = null,
    IReadOnlySet<Guid>? AllowedSourceAccountIds = null,
    string? UserMessage = null,
    CurrentTurnToolAccessState? ToolAccessState = null,
    AgentCommunicationScopeKind CommunicationScopeKind = AgentCommunicationScopeKind.None,
    string CommunicationScopeAuthority = "legacy-unspecified",
    bool CommunicationScopeUserInitiated = false,
    string? ActingAgentId = null,
    bool UnattendedAgentTurn = false)
{
    /// <summary>Host-compiled original-request restriction; independent of interactive read authority.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool? ToolsWithheldByUser { get; init; }
    /// <summary>Exact authored original, distinct from a legacy host wrapper; runtime only.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? AuthenticatedOriginalUserMessage { get; init; }
    /// <summary>Host-created maintenance authority, never accepted from tool arguments.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public AgentRepairAuthority? RepairAuthority { get; init; }
    /// <summary>Host-configured repository grant for an isolated repair coding mission.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public CodingRepairGrant? CodingRepairGrant { get; init; }
    /// <summary>Active host-owned goal inbox scope, independent of protected Mission authority.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Guid? GoalMessageGoalId { get; init; }
    /// <summary>Current worker lease for an exact host mailbox acknowledgement; never provider input.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? GoalMessageLeaseToken { get; init; }
    public Guid? MissionObjectiveId { get; init; }
    /// <summary>Exact host-computed learning partition/bindings for this procedure turn; never from tool input.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SwarmCore.Agent.Learning.AgentRoutineLearningContext? RoutineLearningContext { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public SwarmCore.Agent.Learning.AgentRoutineLearningTurnScope? RoutineLearningTurnScope { get; init; }
    public int MissionAttempt { get; init; }
    public string? MissionBudgetReference { get; init; }
    public SwarmCore.Agent.Continual.MissionAuthority? MissionResources { get; init; }
    public bool MissionInvocationIsBounded { get; init; }
    /// <summary>Trusted app/agent identity supplied by its runtime, never accepted as tool arguments.</summary>
    public string? ApplicationId { get; init; }
    public string? ApplicationAgentId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public MissionVerifiedInput? VerifiedMissionInput { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<string, bool, CancellationToken, Task>? RecordMissionPermissionAttempt { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<CancellationToken, Task>? ValidateMissionExecution { get; init; }
    /// <summary>Host-only expected writable artifact version; null selects ordinary root behavior.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<string, CancellationToken, Task<string?>>? GetMissionWriteBaseAsync { get; init; }
    /// <summary>Trusted host checks persisted pending work after tool receipts are durable; never supplied by a model.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<CancellationToken, Task<string?>>? GetMissionTurnYieldReasonAsync { get; init; }
    /// <summary>Host-resolved profile tools after explicit exclusions; never read from tool input.</summary>
    public IReadOnlySet<string>? AdmittedToolIds { get; init; }
    /// <summary>
    /// Host-selected workspace root for this turn (<see cref="AgentTurnRequest.WorkspaceRoot"/>).
    /// Sandbox path resolution uses it as the default root while the turn runs; never read from tool input.
    /// </summary>
    public string? WorkspaceRoot { get; init; }
    public Func<string, string, CancellationToken, Task<string?>>? ValidateMissionToolDispatchAsync { get; init; }

    /// <summary>
    /// True when this turn runs with no human present, regardless of the
    /// authority it carries. A turn with an <see cref="ActingAgentId"/> is by
    /// definition an autonomous agent cycle, so it counts as unattended even
    /// if a caller forgot to set <see cref="UnattendedAgentTurn"/> — the two
    /// signals back each other up. Escape hatches whose justification is "the
    /// user is present and typed this request" (the script <c>app</c> DI
    /// hatch, approval-tier auto-execution) must check this, not just
    /// <see cref="CommunicationScopeUserInitiated"/>: an unattended turn can
    /// legitimately carry standing user authority while its prompt contains
    /// untrusted inbox/web content that no one is watching act.
    /// </summary>
    public bool IsUnattended => UnattendedAgentTurn || ActingAgentId is not null;
}

/// <summary>
/// Turn-local, concurrency-safe access counters. The object is deliberately
/// carried by reference inside the otherwise immutable turn context so every
/// tool invocation observes the same one-shot permits.
/// </summary>
public sealed class CurrentTurnToolAccessState
{
    private string? _broadCommunicationLookupOwner;
    private int _broadCommunicationWorkflowSteps;
    private int _specificCommunicationReads;
    private int _boundedBroadCommunicationLookups;
    private int _boundedBroadCommunicationWorkflowSteps;
    private int _lastCitationAlias;

    /// <summary>
    /// Reserves one contiguous range in the turn-wide <c>S#</c> citation
    /// namespace and returns the first number in that range. Every evidence
    /// producer in a turn must use this allocator so an alias can never name
    /// two different sources merely because the model called another tool.
    /// </summary>
    public int ReserveCitationAliases(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        while (true)
        {
            var observed = Volatile.Read(ref _lastCitationAlias);
            if (observed > int.MaxValue - count)
            {
                throw new InvalidOperationException("The turn-wide citation alias range was exhausted.");
            }

            if (Interlocked.CompareExchange(ref _lastCitationAlias, observed + count, observed) == observed)
            {
                return observed + 1;
            }
        }
    }

    public bool TryBeginBroadCommunicationLookup()
        => Interlocked.CompareExchange(
            ref _broadCommunicationLookupOwner,
            "single",
            comparand: null) is null;

    public bool TryUseBroadCommunicationWorkflow(string workflowId, int maxSteps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSteps, 1);

        var owner = "workflow:" + workflowId;
        var observed = Interlocked.CompareExchange(
            ref _broadCommunicationLookupOwner,
            owner,
            comparand: null);
        if (observed is not null && !observed.Equals(owner, StringComparison.Ordinal))
        {
            return false;
        }

        return Interlocked.Increment(ref _broadCommunicationWorkflowSteps) <= maxSteps;
    }

    public bool TryUseSpecificCommunicationRead(int maxReads)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxReads, 1);
        return Interlocked.Increment(ref _specificCommunicationReads) <= maxReads;
    }

    /// <summary>
    /// Counter-based broad-lookup allowance for user-initiated turns, where
    /// the single-permit model would police intent instead of stopping
    /// runaway loops.
    /// </summary>
    public bool TryUseBoundedBroadCommunicationLookups(int maxLookups)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLookups, 1);
        return Interlocked.Increment(ref _boundedBroadCommunicationLookups) <= maxLookups;
    }

    /// <summary>
    /// Counter-based multi-step workflow allowance for user-initiated turns,
    /// the workflow analogue of
    /// <see cref="TryUseBoundedBroadCommunicationLookups"/>.
    /// <para>
    /// <see cref="TryUseBroadCommunicationWorkflow"/> models a workflow as a
    /// single owner with a handful of steps, which is right for an unattended
    /// turn but polices intent on a user-initiated one: a user who asks the
    /// agent to investigate with SQL legitimately runs one
    /// list-databases → describe-schema → query → query → query chain, and
    /// every one of those tools spends a step from the same tiny budget. This
    /// counter keeps a runaway loop bounded without capping human-scale work,
    /// and does not claim exclusive ownership, so two legitimate workflows in
    /// one user turn do not deny each other.
    /// </para>
    /// </summary>
    public bool TryUseBoundedBroadCommunicationWorkflow(int maxSteps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSteps, 1);
        return Interlocked.Increment(ref _boundedBroadCommunicationWorkflowSteps) <= maxSteps;
    }
}

/// <summary>
/// <see cref="AsyncLocal{T}"/>-backed accessor. A single shared instance is
/// registered as a singleton; the <c>AsyncLocal</c> storage keeps per-turn
/// values isolated across concurrent async flows.
/// </summary>
public sealed class AsyncLocalCurrentTurnContextAccessor : ICurrentTurnContextAccessor
{
    private static readonly AsyncLocal<CurrentTurnContext?> Storage = new();

    /// <summary>
    /// Read-only access for append-only infrastructure helpers that cannot take a
    /// scoped service dependency. The runner still owns setting and clearing the
    /// value through <see cref="Current"/>.
    /// </summary>
    public static CurrentTurnContext? Ambient => Storage.Value;

    public CurrentTurnContext? Current
    {
        get => Storage.Value;
        set
        {
            Storage.Value = value;
            SwarmCore.Abstractions.Security.ExecutionResourceBoundary.Classified = value?.MissionObjectiveId is null ? null
                : async (action, ct) =>
                {
                    if (value.ValidateMissionExecution is not { } validate)
                        throw new InvalidOperationException("Mission execution requires a durable fence.");
                    await validate(ct);
                    if (!Enum.TryParse<SwarmCore.Agent.Continual.MissionResourceKind>(action.Kind, out var kind)
                        || !Enum.TryParse<SwarmCore.Agent.Continual.MissionEffect>(action.Effects, out var effects)
                        || value.MissionResources?.Evaluate(new(new(kind, action.Identity), effects, value.ProfileId?.ToString("D") ?? "")).Allowed != true)
                        throw new SwarmCore.Agent.Continual.MissionActionSkippedException("Outside mission resources/effects: " + action.Surface);
                    ct.ThrowIfCancellationRequested();
                };
            SwarmCore.Abstractions.Security.ExecutionResourceBoundary.Current = value?.MissionObjectiveId is null ? null
                : async (surface, resource, ct) =>
                {
                    if (value.ValidateMissionExecution is not { } validate)
                        throw new InvalidOperationException("Mission execution requires a durable fence.");
                    await validate(ct);
                    throw new SwarmCore.Agent.Continual.MissionActionSkippedException(
                        "SkipOutsideMission: " + surface + " has no reviewed concrete resource/effect binding. Use the mission's bounded adapters; do not request permission.");
                };
        }
    }
}
