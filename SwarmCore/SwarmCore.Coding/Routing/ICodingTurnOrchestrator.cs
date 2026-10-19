using SwarmCore.Agent.Runtime;

namespace SwarmCore.Coding.Routing;

/// <summary>One user request to the coding harness.</summary>
public sealed record CodingTurnRequest
{
    public required string ConversationId { get; init; }
    public required string UserMessage { get; init; }
    public SwarmCore.Abstractions.Collaboration.TurnAssistanceOptions Assistance { get; init; } = new();
    /// <summary>Uses the plan-mode tool fence for an inspection report, without the planning prompt.</summary>
    public bool ReviewOnly { get; init; }
    /// <summary>Host-authenticated original, before continuation or evidence augmentation; runtime only.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? AuthenticatedOriginalUserMessage { get; init; }
    /// <summary>Value-free host intake evidence; never model-authored metadata.</summary>
    public SwarmCore.Coding.Secrets.CredentialIntakeReceipt? CredentialIntake { get; init; }

    /// <summary>Lane to start on. Null uses the configured default lane.</summary>
    public string? LaneName { get; init; }
    /// <summary>User-selected model for the first lane. Hand-offs keep each destination's configured model.</summary>
    public string? ModelOverride { get; init; }
    public bool IsContinuation { get; init; }
    public IReadOnlyList<SwarmCore.Coding.Files.CodingAttachment> Attachments { get; init; } = [];

    /// <summary>Overrides the lane source's <see cref="CodingLaneSet.HandOffOnRefusal"/> for this request when set.</summary>
    public bool? AllowHandOff { get; init; }

    /// <summary>The user explicitly chose this route. Never substitute another lane, including on startup failure.</summary>
    public bool SelectionPinned { get; init; }

    /// <summary>Retain a refusal and stop instead of automatically trying another model.</summary>
    public bool StopOnRefusal { get; init; }

    /// <summary>
    /// The host owns every visible retry: the inner router never substitutes another provider or model for a lane's
    /// route. A pinned selection or <see cref="StopOnRefusal"/> implies it; a host that lets refusals hand off to the
    /// next lane sets it so the hand-off is the only route change.
    /// </summary>
    public bool DisableRouteFallback { get; init; }

    /// <summary>Host-authenticated request to change repository files. A prose-only answer cannot complete this turn.</summary>
    public bool RequireWorkspaceChange { get; init; }

    /// <summary>
    /// The repository this request works in. Null asks the host's <see cref="ICodingWorkspaceResolver"/>
    /// for the conversation's workspace; with neither, the turn does not start.
    /// </summary>
    public CodingWorkspace? Workspace { get; init; }

    /// <summary>
    /// Communication authority for the turn, chosen by the host. The harness never creates or
    /// widens authority itself: null runs the turn as <see cref="AgentCommunicationScope.None"/>,
    /// so side-effecting tools go through the host's <see cref="IPermissionBroker"/> and native
    /// lanes (which bypass the broker) are not used. A host whose direct user request authorizes
    /// its own local tool use (SwarmCode) passes <see cref="AgentCommunicationScope.UserRequested"/>.
    /// </summary>
    public AgentCommunicationScope? CommunicationScope { get; init; }

    /// <summary>
    /// The person's permission policy for this request (mode plus allow/deny rules), chosen by the host. The harness
    /// applies it through <see cref="Permissions.CodingPermissionGuard"/> while the turn runs: denied tools are refused,
    /// tools the mode asks about go to the host's <see cref="IPermissionBroker"/>. A policy that can ask or refuse also
    /// takes native lanes out of the rotation (they run their own tools outside the broker). Null applies no policy.
    /// It only narrows <see cref="CommunicationScope"/>; it never widens it.
    /// </summary>
    public Permissions.CodingPermissionPolicy? Permissions { get; init; }

    /// <summary>
    /// Profile the turn runs under. Null uses <see cref="CodingOptions.ProfileId"/>. A host with
    /// its own profiles (Talavine) passes the conversation's profile so its resolver, stores,
    /// and admission policy apply.
    /// </summary>
    public Guid? ProfileId { get; init; }

    /// <summary>
    /// Display name of that profile, supplied by the host that resolved it. Native lanes report it in
    /// <see cref="TurnStarted.ProfileName"/> (and so in the durable progress record). Null uses
    /// <see cref="CodingOptions.ProfileName"/> when <see cref="ProfileId"/> is also null.
    /// </summary>
    public string? ProfileName { get; init; }

    /// <summary>
    /// Host proof that the working-context sources named by the given tool calls may be
    /// checkpointed and restored. Null keeps the agent runtime's default (no unproven restore).
    /// SwarmCode supplies an always-true validator because its sources are the user's own
    /// repository and shell; a host that admits communication tools into coding turns must not.
    /// </summary>
    public Func<IReadOnlyList<string>, CancellationToken, Task<bool>>? ValidateWorkingContextSourceCallsAsync { get; init; }

    /// <summary>Explicit per-job override of configured execution limits.</summary>
    public CodingExecutionLimits? ExecutionLimits { get; init; }
    /// <summary>Hosts disable both response-history reads and writes for temporary/private turns.</summary>
    public bool RememberModelResponses { get; init; } = true;

    /// <summary>
    /// Host correlation copied onto every lane attempt (<see cref="AgentTurnRequest.Metadata"/> for
    /// engine lanes, <see cref="NativeLaneTurn.Metadata"/> for native lanes), so a host's durable
    /// observers join all attempts of one request (hand-offs, acceptance repairs) to one objective.
    /// Host state only; never model output.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>
    /// True when no person is waiting on this turn (a host continuation, scheduled, worker, employee or
    /// sub-agent turn). Native lanes never run for such a turn, whatever its authority.
    /// </summary>
    public bool UnattendedAgentTurn { get; init; }

    /// <summary>
    /// Host admission for native lanes on this request (for example a profile capability). False skips
    /// every native lane with <see cref="CodingTurnOrchestrator.NativeLaneNotAdmitted"/>. Default true.
    /// </summary>
    public bool AllowNativeLanes { get; init; } = true;

    /// <summary>Containment for native lanes on this request. Null uses the host's configured default.</summary>
    public NativeLaneContainment? NativeContainment { get; init; }

    /// <summary>
    /// Host-owned plan mode for this request (the person's per-session toggle; never model output): the agent investigates
    /// read-only and ends with a proposed plan. Engine lanes lose every side-effecting tool except the task list
    /// (<see cref="Planning.CodingPlanModeToolFilter"/>); a native lane runs only when its runner supports a read-only plan
    /// mode (<see cref="INativeLaneRunner.SupportsPlanMode"/>). Workspace-change and acceptance checks do not apply.
    /// </summary>
    public bool PlanMode { get; init; }

    /// <summary>
    /// Host-owned request to compact the conversation's working context before this turn (the person's <c>/compact</c>).
    /// Engine lanes summarize the provider-facing history once before their first model call
    /// (<see cref="AgentTurnRequest.CompactWorkingContextFirst"/>); the durable transcript is never rewritten. Native
    /// CLI lanes manage their own context and ignore it.
    /// </summary>
    public bool CompactWorkingContextFirst { get; init; }

    /// <summary>
    /// An exact provider/model the user chose for this conversation. When both are set and no lane is
    /// named, the request starts on that route (lane <see cref="CodingTurnOrchestrator.SelectedRouteLane"/>).
    /// </summary>
    public string? RouteProvider { get; init; }
    public string? RouteModel { get; init; }

    /// <summary>Passed to engine lanes (<see cref="AgentTurnRequest.CurrentView"/>).</summary>
    public CurrentViewSnapshot? CurrentView { get; init; }

    /// <summary>Passed to engine lanes (<see cref="AgentTurnRequest.RoutingDemand"/>).</summary>
    public SwarmCore.Abstractions.LlmRouting.LlmTaskDemand? RoutingDemand { get; init; }
}

/// <summary>
/// Runs a coding turn across the lanes an <see cref="ICodingLaneSource"/> supplies: pins the
/// requested lane, detects a refusal or inability in the result, and hands the remaining work
/// to the next lane as a continuation turn in the same conversation.
/// </summary>
public interface ICodingTurnOrchestrator
{
    /// <summary>Current lanes from the lane source, ordered by priority.</summary>
    IReadOnlyList<CodingLane> Lanes { get; }

    CodingLane? ResolveLane(string? name);

    IAsyncEnumerable<TurnEvent> RunAsync(CodingTurnRequest request, CancellationToken ct = default);
}

/// <summary>
/// Optional host seam: rewrites the provider transcript so the next lane does not read a
/// previous lane's refusal verbatim. The default file and in-memory session stores in a
/// host implement this; without it the orchestrator leaves the transcript as is.
/// </summary>
public interface ITranscriptRewriter
{
    /// <summary>
    /// Replaces the text of the most recent assistant message whose text equals (or contains)
    /// <paramref name="matchText"/> with <paramref name="replacementText"/>. Tool receipts are kept.
    /// Returns false when no such message exists.
    /// </summary>
    Task<bool> ReplaceLastAssistantTextAsync(string conversationId, string matchText, string replacementText, CancellationToken ct = default);
}

/// <summary>Emitted before a lane's turn starts.</summary>
public sealed record CodingLaneSelected(string TurnId, CodingLane Lane, int Attempt) : TurnEvent(TurnId);

/// <summary>Emitted when the orchestrator moves the request to another lane.</summary>
public sealed record CodingLaneHandOff(string TurnId, CodingLane From, CodingLane To, string Reason) : TurnEvent(TurnId)
{
    /// <summary>Why the previous lane stopped, as a typed outcome.</summary>
    public LaneOutcomeKind Outcome { get; init; }
}

/// <summary>Emitted when a lane is passed over because its route failed recently and is cooling down.</summary>
public sealed record CodingLaneSkipped(string TurnId, CodingLane Lane, string Reason) : TurnEvent(TurnId);

/// <summary>
/// Something a lane reports that the user should see but that is not part of the answer, such as which
/// MCP servers a native agent was asked to start or a start-up warning it printed.
/// </summary>
public sealed record CodingLaneNotice(string TurnId, string Lane, string Message, string? Details = null) : TurnEvent(TurnId);

/// <summary>Emitted when every eligible lane declined or failed.</summary>
public sealed record CodingLanesExhausted(string TurnId, string Reason) : TurnEvent(TurnId)
{
    /// <summary>A refusal is terminal, not a request to reassess the model on the next prompt.</summary>
    public bool ReassessModel { get; init; } = true;
}

/// <summary>Host verification is separate from a model's finished answer.</summary>
public sealed record CodingAcceptanceEvaluated(string TurnId, CodingAcceptanceResult Result) : TurnEvent(TurnId);

/// <summary>Comparable liveness and usage across native and API lanes; null tokens means unavailable, not zero.</summary>
public sealed record CodingTurnProgress(string TurnId, string Lane, TimeSpan Elapsed, int CompletedToolCalls,
    long? ReportedTokens, string Phase) : TurnEvent(TurnId);

/// <summary>The job stopped at a user-configured boundary; completion has not been verified.</summary>
public sealed record CodingTurnLimitReached(string TurnId, string Reason) : TurnEvent(TurnId);

/// <summary>Host-owned receipt: every requested pre-work opinion has returned successfully.</summary>
public sealed record CodingOpinionsObtained(string TurnId) : TurnEvent(TurnId);
