namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Answers "can this route actually make a tool call?" by asking the model,
/// instead of refusing because nobody has written it down.
/// <para>
/// Routing treats tool-use support as a nullable bool with three meanings —
/// yes, no, and <b>unknown</b> — but the gate collapsed unknown into no, so a
/// model that had simply never been tested was rejected exactly like one known
/// to fail ("model-level tool-use capability is not proven"). A local model
/// could be refused in 0.1s without a single token ever being sent to it. This
/// turns that verdict into a question: probe once, cache the answer, and let
/// the evidence decide.
/// </para>
/// </summary>
public interface IToolUseCapabilityProver
{
    /// <summary>
    /// Probe a provider/model for real tool-calling ability.
    /// <list type="bullet">
    /// <item><c>true</c> — the model emitted a well-formed tool call.</item>
    /// <item><c>false</c> — it answered but would not call the tool. Genuine
    /// negative evidence; cached so the route is rejected quickly next time.</item>
    /// <item><c>null</c> — inconclusive (transport error, timeout, provider
    /// offline). Deliberately NOT cached as a capability verdict: an outage
    /// must never be recorded as a permanent capability verdict. See
    /// <see cref="GetLastProbeFailureCategory"/> for the one inconclusive
    /// shape the router still needs to distinguish.</item>
    /// </list>
    /// </summary>
    Task<bool?> ProveToolUseAsync(string provider, string model, CancellationToken ct = default);

    /// <summary>
    /// Records affirmative evidence observed during a normal agent call. This
    /// avoids making a second synthetic probe just to rediscover a tool call
    /// the model has already emitted for the user.
    /// </summary>
    void RecordSuccessfulToolCall(string provider, string model);

    /// <summary>
    /// A verdict already established for this route, without probing. Null when
    /// the route has not been probed (or the last probe was inconclusive).
    /// </summary>
    bool? GetCachedVerdict(string provider, string model);

    /// <summary>
    /// Why the most recent probe for this route was inconclusive, when the
    /// failure is one the router must not treat as "discovery still pending".
    /// <para>
    /// Today the only category reported is
    /// <see cref="LlmFailureCategory.ModelNotFound"/>: the provider itself said
    /// the model is not installed. That is not a capability question waiting
    /// on evidence, so callers that poll "temporarily unavailable" routes must
    /// stop polling and either heal the route or fail the request. The
    /// verdict is remembered for a bounded time so a missing model is not
    /// re-probed on every routing decision, and it clears on its own once the
    /// user (or an agent) pulls the model. Returns null when no such failure
    /// is on record.
    /// </para>
    /// </summary>
    LlmFailureCategory? GetLastProbeFailureCategory(string provider, string model) => null;
}
