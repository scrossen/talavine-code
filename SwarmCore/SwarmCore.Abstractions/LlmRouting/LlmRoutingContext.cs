namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Whether a call is blocking on a user or running in the background. The router uses this
/// to trade off latency vs cost — interactive calls accept higher cost for lower latency,
/// background calls trade latency for cost savings.
/// </summary>
public enum LlmInteractivity
{
    /// <summary>Unspecified by the caller; router will apply the function-type default.</summary>
    Unspecified = 0,

    /// <summary>Background/batch work — nobody is waiting; cost dominates.</summary>
    Background = 1,

    /// <summary>A user is blocked on the response; latency dominates.</summary>
    UserWaiting = 2,
}

/// <summary>
/// Whether the prompt/data may leave the machine. LocalOnly forces the router to a local
/// provider (Ollama / LM Studio / a custom provider whose local flag and endpoint pass
/// <see cref="LlmProviderLocalityPolicy"/>); cloud
/// models are skipped entirely.
/// </summary>
public enum LlmPrivacyClass
{
    /// <summary>Unspecified by the caller; router will apply the function-type default.</summary>
    Unspecified = 0,

    /// <summary>Cloud providers are allowed.</summary>
    CloudOk = 1,

    /// <summary>Must route to a local provider; prompt must not leave the machine.</summary>
    LocalOnly = 2,
}

/// <summary>
/// How aggressively to spend on a given call. Separate axis from
/// <see cref="LlmInteractivity"/> — interactivity answers "is a user waiting"; urgency
/// answers "how much should we spend to make this fast and smart". Defaults to
/// <see cref="Normal"/> for user-waiting calls and <see cref="Batch"/> for background work,
/// configurable per function via <see cref="FunctionTypeProfile.DefaultUrgency"/>.
/// </summary>
public enum LlmUrgency
{
    /// <summary>Unspecified by the caller; router applies the function-type default.</summary>
    Unspecified = 0,

    /// <summary>"This can run cheap and slow." Background drudgery; prefer local / cheapest cloud.</summary>
    Batch = 1,

    /// <summary>"Use what you normally use." Standard tier per function preference.</summary>
    Normal = 2,

    /// <summary>"I'm watching, make it fast and smart." Promote one tier above baseline.</summary>
    Urgent = 3,
}

/// <summary>
/// Resolved per-call routing context after the router merges caller options with function-type
/// defaults. Kept internal-facing because callers express intent via <see cref="LlmRouterOptions"/>.
/// </summary>
public sealed record LlmCallContext(
    LlmInteractivity Interactivity,
    LlmPrivacyClass PrivacyClass,
    int? LatencyBudgetMs = null,
    LlmUrgency Urgency = LlmUrgency.Normal,
    LlmWorkloadKind Workload = LlmWorkloadKind.Unspecified)
{
    public string InteractivityKey => Interactivity switch
    {
        LlmInteractivity.UserWaiting => "interactive",
        _ => "background"
    };
}

/// <summary>
/// Default routing profile for a function type. Applies when the caller leaves a field
/// <see cref="LlmInteractivity.Unspecified"/> or <see cref="LlmPrivacyClass.Unspecified"/>.
/// </summary>
/// <param name="LatencyBudgetMs">
/// Advisory latency objective for telemetry and future route selection. The router no longer uses
/// this as a wall-clock fallback trigger; selected routes are allowed to finish unless the caller's
/// cancellation token, provider failure, or quality gate ends the request.
/// </param>
/// <param name="IsConversationalAssistant">
/// The function is a person talking with the assistant. When someone is
/// waiting and cloud use is allowed, healthy subscription routes serve it ahead
/// of local routes; see <see cref="LlmTaskDemandPolicy"/>.
/// </param>
public sealed record FunctionTypeProfile(
    LlmInteractivity Interactivity,
    LlmPrivacyClass PrivacyClass,
    bool IsReasoningHeavy,
    int? LatencyBudgetMs = null,
    LlmUrgency DefaultUrgency = LlmUrgency.Normal,
    bool IsConversationalAssistant = false);

/// <summary>
/// Static registry of default routing profiles per function type. This is the single place
/// that encodes "spam detection is background work that can run on a local model" and
/// "agent chat is a user-waiting task that needs a fast cloud model."
///
/// Consumers read the profile for a function type; callers can still override any field via
/// <see cref="LlmRouterOptions"/> at the call site.
/// </summary>
public static class FunctionTypeProfiles
{
    private static readonly FunctionTypeProfile DefaultProfile =
        new(LlmInteractivity.Background, LlmPrivacyClass.CloudOk, IsReasoningHeavy: true, DefaultUrgency: LlmUrgency.Batch);

    private static readonly Dictionary<string, FunctionTypeProfile> Profiles = BuildProfiles();

    /// <summary>Resolve the default profile for a function type. Unknown types fall back to background + cloud-ok + reasoning-heavy.</summary>
    public static FunctionTypeProfile Resolve(string functionType)
    {
        if (string.IsNullOrWhiteSpace(functionType)) return DefaultProfile;
        return Profiles.TryGetValue(functionType, out var profile) ? profile : DefaultProfile;
    }

    /// <summary>
    /// Merge caller options with the function-type profile: caller wins when specified,
    /// profile fills in the gaps. Latency budget is advisory only and is not used to cancel
    /// or reroute an in-flight request.
    /// </summary>
    public static LlmCallContext Resolve(string functionType, LlmInteractivity callerInteractivity, LlmPrivacyClass callerPrivacy)
        => Resolve(functionType, callerInteractivity, callerPrivacy, LlmUrgency.Unspecified);

    /// <summary>
    /// Merge caller options with the function-type profile, including urgency. Caller wins
    /// when specified; profile fills in the gaps.
    /// </summary>
    public static LlmCallContext Resolve(
        string functionType,
        LlmInteractivity callerInteractivity,
        LlmPrivacyClass callerPrivacy,
        LlmUrgency callerUrgency,
        LlmWorkloadKind callerWorkload = LlmWorkloadKind.Unspecified)
    {
        var profile = Resolve(functionType);
        var interactivity = callerInteractivity == LlmInteractivity.Unspecified ? profile.Interactivity : callerInteractivity;
        var privacy = callerPrivacy == LlmPrivacyClass.Unspecified ? profile.PrivacyClass : callerPrivacy;
        var urgency = callerUrgency == LlmUrgency.Unspecified ? profile.DefaultUrgency : callerUrgency;
        var budget = profile.LatencyBudgetMs;
        return new LlmCallContext(interactivity, privacy, budget, urgency,
            LlmWorkloadPolicy.Resolve(callerWorkload, interactivity));
    }

    public static bool IsReasoningHeavy(string functionType) => Resolve(functionType).IsReasoningHeavy;

    private static Dictionary<string, FunctionTypeProfile> BuildProfiles()
    {
        // Convention: most things default to Background + CloudOk + ReasoningHeavy, so we
        // only list the exceptions where interactivity, privacy, or reasoning-weight differ.
        var map = new Dictionary<string, FunctionTypeProfile>(StringComparer.OrdinalIgnoreCase);

        // --- Interactive: user is waiting. ---
        Add(map, FunctionTypes.AgentChat,        LlmInteractivity.UserWaiting, LlmPrivacyClass.CloudOk,   reasoning: true, assistant: true);
        Add(map, FunctionTypes.AgentLoop,        LlmInteractivity.Background,  LlmPrivacyClass.CloudOk,   reasoning: true);
        Add(map, FunctionTypes.CodingAssistant,  LlmInteractivity.UserWaiting, LlmPrivacyClass.CloudOk,   reasoning: true);
        Add(map, FunctionTypes.AgentSelection,   LlmInteractivity.UserWaiting, LlmPrivacyClass.CloudOk,   reasoning: false); // id lookup, not reasoning
        Add(map, FunctionTypes.Chatbot,          LlmInteractivity.UserWaiting, LlmPrivacyClass.CloudOk,   reasoning: true, assistant: true);
        Add(map, FunctionTypes.TicketResponse,   LlmInteractivity.UserWaiting, LlmPrivacyClass.CloudOk,   reasoning: true);
        Add(map, FunctionTypes.Autoresponder,    LlmInteractivity.UserWaiting, LlmPrivacyClass.CloudOk,   reasoning: true);
        Add(map, FunctionTypes.Troubleshooting,  LlmInteractivity.UserWaiting, LlmPrivacyClass.CloudOk,   reasoning: true);
        Add(map, FunctionTypes.FAQAnswering,     LlmInteractivity.UserWaiting, LlmPrivacyClass.CloudOk,   reasoning: true);
        // Guided business setup/prospecting: the owner waits on a short-deadline
        // structured draft (callers disable thinking), so these must not inherit
        // the Background/Batch default profile.
        Add(map, FunctionTypes.BusinessSetupPlanning,       LlmInteractivity.UserWaiting, LlmPrivacyClass.CloudOk, reasoning: false);
        Add(map, FunctionTypes.BusinessProspectingPlanning, LlmInteractivity.UserWaiting, LlmPrivacyClass.CloudOk, reasoning: false);
        Add(map, FunctionTypes.BusinessProspectingDrafting, LlmInteractivity.UserWaiting, LlmPrivacyClass.CloudOk, reasoning: false);

        // --- Background, private-eligible: classify/extract drudgery we'd love to run locally. ---
        Add(map, FunctionTypes.SpamDetection,         LlmInteractivity.Background, LlmPrivacyClass.CloudOk, reasoning: true);
        Add(map, FunctionTypes.Classification,        LlmInteractivity.Background, LlmPrivacyClass.CloudOk, reasoning: true);
        Add(map, FunctionTypes.SentimentAnalysis,     LlmInteractivity.Background, LlmPrivacyClass.CloudOk, reasoning: false);
        Add(map, FunctionTypes.KnowledgeExtraction,   LlmInteractivity.Background, LlmPrivacyClass.CloudOk, reasoning: true);
        Add(map, FunctionTypes.DecisionExtraction,    LlmInteractivity.Background, LlmPrivacyClass.CloudOk, reasoning: true);
        Add(map, FunctionTypes.ActionItemExtraction,  LlmInteractivity.Background, LlmPrivacyClass.CloudOk, reasoning: true);
        Add(map, FunctionTypes.EmailProcessing,       LlmInteractivity.Background, LlmPrivacyClass.CloudOk, reasoning: true);
        Add(map, FunctionTypes.Summarization,         LlmInteractivity.Background, LlmPrivacyClass.CloudOk, reasoning: true);
        Add(map, FunctionTypes.ContextAnalysis,       LlmInteractivity.Background, LlmPrivacyClass.CloudOk, reasoning: true);
        Add(map, FunctionTypes.ConversationAnalysis,  LlmInteractivity.Background, LlmPrivacyClass.CloudOk, reasoning: true);
        Add(map, FunctionTypes.MessagePipeline,       LlmInteractivity.Background, LlmPrivacyClass.CloudOk, reasoning: true);
        Add(map, FunctionTypes.ChatSegmentation,      LlmInteractivity.Background, LlmPrivacyClass.LocalOnly, reasoning: false);

        // Embeddings are deterministic, small, and already cheap — no A/B value, no reasoning.
        Add(map, FunctionTypes.TextEmbedding,  LlmInteractivity.Background, LlmPrivacyClass.CloudOk, reasoning: false);
        Add(map, FunctionTypes.ImageEmbedding, LlmInteractivity.Background, LlmPrivacyClass.CloudOk, reasoning: false);

        return map;
    }

    private static void Add(Dictionary<string, FunctionTypeProfile> map, string key,
        LlmInteractivity interactivity, LlmPrivacyClass privacy, bool reasoning, bool assistant = false)
    {
        // User-waiting work defaults to Normal urgency (use the configured tier);
        // background work defaults to Batch (use the cheap tier). Caller can still
        // override per call with LlmRouterOptions.Urgency.
        var defaultUrgency = interactivity == LlmInteractivity.UserWaiting
            ? LlmUrgency.Normal
            : LlmUrgency.Batch;
        map[key] = new FunctionTypeProfile(interactivity, privacy, reasoning, DefaultUrgency: defaultUrgency,
            IsConversationalAssistant: assistant);
    }
}
