namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Category a failed LLM call belongs to. One classification, shared by every
/// consumer, instead of each subsystem re-sniffing provider error strings.
///
/// Lives in SwarmCore.Abstractions (not SwarmCore.LlmRouting.Services, where
/// the classifier itself lives) so the category can travel TYPED across the
/// whole failure lane: the cooldown store (<see cref="LlmProviderCooldownRecord"/>),
/// failover exceptions, and UI presenters consume this enum directly rather
/// than re-deriving it from persisted reason strings.
/// </summary>
public enum LlmFailureCategory
{
    ProviderError = 0,
    RateLimit,
    QuotaExhausted,
    AuthInvalid,
    ContentPolicy,
    InvalidRequest,
    ModelNotFound,
    Timeout,
    /// <summary>
    /// The provider is up and answered — the REPLY was unusable (a malformed
    /// tool-use envelope, a tool call written as text, envelope debris, an
    /// empty completion). Retrying the same route usually recovers; cooling
    /// the provider down for it turns one flaky reply into a dead route.
    /// </summary>
    MalformedReply,
    /// <summary>
    /// A subscription CLI launch was DECLINED by our own cross-process co-run
    /// guard (another live Talavine process was using the CLI, or the
    /// pre-launch orphan sweep was blocked) — the provider itself was never
    /// consulted. Self-inflicted and transient: parallel calls on the same
    /// provider routinely succeed in the same second, so penalizing the
    /// provider for it produced false "provider paused" cooldowns.
    /// </summary>
    LaunchDeclined,
    /// <summary>
    /// The request did not fit the route's context window: the provider
    /// rejected it as too long, or (Ollama) silently truncated the oldest
    /// messages and then failed on the damaged transcript. It is the request's
    /// size against this route, not provider trouble — never a cooldown. The
    /// caller that owns the transcript recovers by compacting it and retrying.
    /// </summary>
    ContextOverflow,
}
