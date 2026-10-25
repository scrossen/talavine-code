using System.Text.Json.Nodes;

namespace SwarmCore.Agent.Catalog;

/// <summary>
/// Self-describing tool metadata. Agents register descriptors with the
/// <see cref="ICapabilityCatalog"/> at startup; the LLM loop later picks among
/// them on a per-iteration basis.
/// </summary>
public sealed record ToolDescriptor
{
    /// <summary>Host-stamped implementation contract for exact, immutable capability admission.</summary>
    public string? ImplementationFingerprint { get; init; }
    /// <summary>Reviewed by the owning host/provider for use by a user-connected profile client.
    /// Defaults to local-only; this is metadata, not a separate interactive permission prompt.</summary>
    public bool RemoteProfileAvailable { get; init; }
    public bool NativeComputerUseEnabled { get; init; }
    public SwarmCore.Abstractions.Computer.IComputerSession? NativeComputerSession { get; init; }

    /// <summary>
    /// Stable identifier the LLM uses when selecting this tool.
    /// Conventionally namespaced (e.g. "spam.list-classified-spam-in-inbox").
    /// </summary>
    public required string ToolId { get; init; }

    /// <summary>
    /// Semantic description of what this tool does, when to use it, and
    /// what it returns. This is read by the LLM, so write it for an LLM.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Owning agent's AgentId. Used to scope tool selection per agent.
    /// </summary>
    public required string OwnerAgentId { get; init; }

    /// <summary>
    /// Type the LLM-supplied JSON input is deserialized into before invocation.
    /// </summary>
    public required Type InputType { get; init; }

    /// <summary>
    /// Type the invoke delegate returns. Used for structural validation.
    /// </summary>
    public required Type OutputType { get; init; }

    /// <summary>
    /// Cost class — read by the planner LLM and printed in the prompt so the
    /// model prefers cheap deterministic tools over expensive LLM ones.
    /// </summary>
    public required ToolCostClass Cost { get; init; }

    /// <summary>
    /// Side effect class — read by the planner LLM. Destructive/external tools
    /// should be picked only when the goal demands it.
    /// </summary>
    public required ToolSideEffect SideEffect { get; init; }

    /// <summary>
    /// Optional keyword bag used by the catalog's lightweight semantic search.
    /// </summary>
    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>
    /// Complete user commands that select this tool without a lexical contest.
    /// Hosts match only authenticated user input against already authorized
    /// candidates. Selection never invokes a tool or bypasses its approval checks.
    /// </summary>
    public IReadOnlyList<string> UserInvocationPhrases { get; init; } = [];

    /// <summary>
    /// Optional host-owned descriptive tags (for example the business-cycle stage a tool serves).
    /// Metadata never grants authority, changes approval, or affects invocation.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = EmptyMetadata;

    private static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    /// <summary>
    /// Declares whether invoking this descriptor can read user data outside
    /// the supplied prompt/current view. Trusted built-in tools default to
    /// <see cref="ToolDataAccessClass.None"/>; external/generic gateways must
    /// classify themselves explicitly, and unknown external access is
    /// intentionally distinguishable so hosts can fail closed.
    /// </summary>
    public ToolDataAccessClass DataAccess { get; init; } = ToolDataAccessClass.None;

    /// <summary>
    /// JSON Schema describing <see cref="InputType"/>. Sent to the LLM provider
    /// as part of the tool definition (Anthropic <c>input_schema</c>, OpenAI
    /// <c>parameters</c>). Populated automatically by <see cref="Create{TIn, TOut}"/>
    /// via <see cref="ToolSchemaGenerator"/>; callers constructing descriptors
    /// manually may supply their own.
    /// </summary>
    public JsonObject? InputSchema { get; init; }

    /// <summary>
    /// Invocation delegate. Input is already deserialized to <see cref="InputType"/>
    /// before being passed in.
    /// </summary>
    public required Func<object, CancellationToken, Task<object?>> Invoke { get; init; }

    /// <summary>
    /// Optional deterministic formatter for tools whose result is already enough
    /// to answer the user. When the host selected only this tool and allows a
    /// terminal result, the loop can finish after its first response instead
    /// of spending another LLM call on summarization. Multi-tool workflows
    /// return to synthesis regardless of tool execution order.
    /// </summary>
    public Func<object?, string?>? FormatResultForUser { get; init; }

    /// <summary>
    /// Optional argument-aware side-effect resolver. When set, the engine computes the
    /// effective side effect (and therefore the approval gate) from the actual invocation
    /// input instead of the static <see cref="SideEffect"/> — e.g. an email move escalates
    /// to <see cref="ToolSideEffect.Destructive"/> when the target folder is Trash/Junk.
    /// Returning null falls back to <see cref="SideEffect"/>. The engine stays
    /// domain-agnostic; tool authors encode any domain semantics in this delegate.
    /// </summary>
    public Func<object, ToolSideEffect?>? ResolveSideEffect { get; init; }

    /// <summary>
    /// Optional argument-aware egress test: true when this invocation carries model-composed data (a query, headers,
    /// a body, a credential reference) to its destination even though it is read-only. Such a call keeps its read-only
    /// side effect for approval and replay (an authenticated GET is still safe to repeat), but the invocation guard
    /// inspects it like a send, so data cannot leave for a destination only untrusted content supplied.
    /// </summary>
    public Func<object, bool>? TransmitsComposedData { get; init; }

    /// <summary>True when the invocation guard must inspect this call: any non-read-only call, or a read that
    /// <see cref="TransmitsComposedData"/>.</summary>
    public bool RequiresInvocationGuard(object input, ToolSideEffect effectiveSideEffect)
        => effectiveSideEffect != ToolSideEffect.ReadOnly || TransmitsComposedData?.Invoke(input) == true;

    /// <summary>
    /// True when a denied invocation is itself recorded by the tool through the
    /// action ledger. The engine may invoke only these tools with an ambient
    /// denied consent context; ordinary tools remain completely uninvoked.
    /// </summary>
    public bool RecordsDeniedAction { get; init; }
    /// <summary>Host-supplied approval permission and wording; null derives both from the side effect.</summary>
    public ToolApproval? Approval { get; init; }
    /// <summary>Host-declared loop behaviours for this tool (see <see cref="ToolEngineRoles"/>).</summary>
    public ToolEngineRoles EngineRoles { get; init; }
    /// <summary>Host adapter enforces concrete mission effects/resources at physical execution.</summary>
    public bool EnforcesMissionResourceBoundary { get; init; }
    /// <summary>Trusted host operation owns atomic idempotency and may re-evaluate an identical mission request.
    /// Does not bypass invocation receipts, authority fences, or the tool own persistence checks.</summary>
    public bool SupportsIdempotentMissionReplay { get; init; }

    /// <summary>Host-reviewed reentrancy and concrete resource/dependency declarations.
    /// The default remains serial even for read-only tools. The delegate must own its DI scope
    /// or use only independently created contexts before declaring reentrancy.</summary>
    public Func<JsonObject, ToolUseEngine.ToolSchedulingCapability>? Scheduling { get; init; }
    public bool AllowsNativeAsync { get; init; }
    /// <summary>Host-reviewed pure read whose typed execution evidence can verify a reusable retrieval
    /// prefix. This never authorizes a final answer; normal model synthesis and output guards still run.</summary>
    public bool SupportsProcedureEvidencePreparation { get; init; }

    /// <summary>
    /// The side effect that should gate a specific invocation, honoring
    /// <see cref="ResolveSideEffect"/> when present.
    /// </summary>
    public ToolSideEffect EffectiveSideEffect(object input)
        => ResolveSideEffect?.Invoke(input) ?? SideEffect;

    /// <summary>
    /// Strongly-typed factory for ergonomic registration. Wraps the typed
    /// invoke delegate so callers don't write any casts.
    /// </summary>
    public static ToolDescriptor Create<TIn, TOut>(
        string toolId,
        string ownerAgentId,
        string description,
        ToolCostClass cost,
        ToolSideEffect sideEffect,
        Func<TIn, CancellationToken, Task<TOut>> invoke,
        IReadOnlyList<string>? keywords = null,
        Func<TOut, string?>? formatResultForUser = null,
        Func<TIn, ToolSideEffect?>? resolveSideEffect = null,
        ToolDataAccessClass dataAccess = ToolDataAccessClass.None,
        bool enforcesMissionResourceBoundary = false,
        bool remoteProfileAvailable = false,
        Func<TIn, bool>? transmitsComposedData = null)
    {
        return new ToolDescriptor
        {
            ToolId = toolId,
            OwnerAgentId = ownerAgentId,
            Description = description,
            InputType = typeof(TIn),
            OutputType = typeof(TOut),
            Cost = cost,
            SideEffect = sideEffect,
            Keywords = keywords ?? [],
            DataAccess = dataAccess,
            EnforcesMissionResourceBoundary = enforcesMissionResourceBoundary,
            RemoteProfileAvailable = remoteProfileAvailable,
            InputSchema = ToolSchemaGenerator.Generate(typeof(TIn)),
            Invoke = async (input, ct) =>
            {
                var typed = (TIn)input;
                var result = await invoke(typed, ct);
                return result;
            },
            FormatResultForUser = formatResultForUser is null
                ? null
                : result => result is TOut typedResult ? formatResultForUser(typedResult) : null,
            ResolveSideEffect = resolveSideEffect is null
                ? null
                : input => input is TIn typedInput ? resolveSideEffect(typedInput) : null,
            // An input of an unexpected type is inspected rather than waved through.
            TransmitsComposedData = transmitsComposedData is null
                ? null
                : input => input is not TIn typedInput || transmitsComposedData(typedInput)
        };
    }
}

/// <summary>
/// Data domain a tool may read. This is an enforcement attribute, not prompt
/// advice: external tool bridges must never leave the value implicit.
/// </summary>
public enum ToolDataAccessClass
{
    None = 0,
    Communication = 1,
    VerifiedNonCommunicationExternal = 2,
    UnclassifiedExternal = 3,
}

/// <summary>
/// How expensive a tool is to invoke. The LLM planner reads this and prefers
/// cheaper tools when both will satisfy the goal.
/// </summary>
public enum ToolCostClass
{
    /// <summary>Pure data lookup, no I/O. Sub-millisecond.</summary>
    Trivial = 0,

    /// <summary>Local DB / repository read. Milliseconds.</summary>
    Cheap = 1,

    /// <summary>Multi-step local computation, batch DB ops, file I/O.</summary>
    Moderate = 2,

    /// <summary>External API call (no LLM). Hundreds of ms to seconds.</summary>
    External = 3,

    /// <summary>One LLM call. Seconds, budget impact.</summary>
    LlmCall = 4,

    /// <summary>Multiple LLM calls or unbounded loop. Use sparingly.</summary>
    Expensive = 5
}

/// <summary>
/// What kind of side effect a tool has on the world. The planner uses this
/// to avoid destructive tools unless the goal explicitly authorizes them.
/// </summary>
public enum ToolSideEffect
{
    /// <summary>Reads only — safe to retry, safe to call speculatively.</summary>
    ReadOnly = 0,

    /// <summary>Writes local state (DB, journal). Reversible.</summary>
    WritesLocal = 1,

    /// <summary>Sends data to an external system (email, API call).</summary>
    ExternalSend = 2,

    /// <summary>Hard delete / irreversible mutation. Requires user approval.</summary>
    Destructive = 3,

    /// <summary>
    /// Runs arbitrary caller-supplied instructions on the machine — a shell
    /// command, a C# script. The blast radius is whatever the process can reach,
    /// so this is its own tier rather than a flavour of
    /// <see cref="WritesLocal"/>: "the agent ran something" is the question the
    /// audit ledger must be able to answer on its own.
    /// <para>
    /// Appended, not inserted, because the tiers are not a severity ladder — no
    /// code orders them — and renumbering would rewrite the meaning of any
    /// value already recorded elsewhere.
    /// </para>
    /// </summary>
    Exec = 4,

    /// <summary>Host-owned execution/inbox bookkeeping only. Cannot mutate requested resources,
    /// send externally, authorize/start work or change authority. Still a local mutation, not read-only.</summary>
    ExecutionMetadata = 5
}
