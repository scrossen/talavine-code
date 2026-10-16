namespace SwarmCore.Abstractions.Agents;

/// <summary>One agent asking another for help. Hop is part of the payload, not metadata — it travels with the ask.</summary>
public sealed record PeerAsk(
    Guid Id,
    string FromAgentId,
    string ToAgentId,
    string Subject,
    string Body,
    int Hop,
    DateTimeOffset CreatedAt);

/// <summary>The consulted agent's reply, produced by its own cycle from its own knowledge.</summary>
public sealed record PeerAnswer(
    Guid Id,
    Guid AskId,
    string FromAgentId,
    string ToAgentId,
    string Body,
    DateTimeOffset CreatedAt);

/// <summary>A peer an agent may ask, as the directory presents it.</summary>
public sealed record PeerAgentCard(string AgentId, string Name, string Role, string Summary);

/// <summary>Outcome of an ask/answer attempt; refusals explain themselves in agent-readable terms.</summary>
public sealed record ConsultationOutcome(bool Ok, string? Error = null, PeerAsk? Ask = null, PeerAnswer? Answer = null);

/// <summary>
/// Agent-to-agent consultation with the loop-protection rails SEALED and the transport
/// abstract — the template-method cut agreed for keeping the desktop and hosted runtimes in
/// parity where it matters.
///
/// <para>
/// THE RAILS ARE THE SECURITY, NOT THE TRANSPORT. The desktop proved this design over
/// HMAC-signed agent email; the hosted CRM rides tenant rows and the trigger queue; both
/// inherit the SAME non-virtual rail logic, so neither host can accidentally ship a
/// consultation lane where asks forward (hop), agents interrogate instead of answering
/// (answer-before-ask), a retried plan mints duplicate asks (one-per-cycle latch), stale asks
/// lock an agent out (freshness), or chatter compounds (daily cap). A host supplies only:
/// who my peers are, how many asks I sent today, and how an ask/answer physically travels.
/// </para>
///
/// <para>
/// LIFETIME: one instance per agent CYCLE. The one-ask-per-cycle latch is instance state and
/// is deliberately never reset by any code path other than construction — the desktop learned
/// that resetting it in dispatch let plan retries mint a second ask.
/// </para>
/// </summary>
public abstract class PeerConsultationBase
{
    /// <summary>Asks never forward: hop is fixed at 1 and validated on receipt, so A→B→C relay chains cannot form.</summary>
    public const int RequiredHop = 1;

    /// <summary>Per-agent daily ask budget. Consultation is for genuine blockers, not a chat channel.</summary>
    public const int DailyAskLimit = 5;

    /// <summary>At most this many pending asks surface per cycle, oldest first, so a backlog cannot flood a prompt.</summary>
    public const int MaxPendingAsksPerCycle = 3;

    /// <summary>Asks older than this are ignored rather than answered — a stale question answered late misleads.</summary>
    public static readonly TimeSpan AskFreshnessWindow = TimeSpan.FromDays(7);

    private bool _askDispatchedThisCycle;

    // ── The sealed rails ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ask a peer for help. Every rail runs before anything travels; the first refusal wins
    /// and is phrased for the asking agent to relay into its report.
    /// </summary>
    public async Task<ConsultationOutcome> TryAskAsync(
        string fromAgentId,
        string toAgentId,
        string subject,
        string body,
        int pendingAskCount,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(body))
            return new(false, "An ask needs a subject and a question.");

        // Answer-before-ask: an agent with unanswered questions in front of it answers first.
        // This also structurally blocks ask-in-response-to-ask loops.
        if (pendingAskCount > 0)
            return new(false, "You have peer questions waiting for YOUR answer — answer those before asking anything yourself.");

        // One ask per cycle, latched at construction scope: a plan retry must not mint a second.
        if (_askDispatchedThisCycle)
            return new(false, "You already asked a peer this cycle. Wait for the answer before asking again.");

        if (string.Equals(fromAgentId, toAgentId, StringComparison.OrdinalIgnoreCase))
            return new(false, "You cannot ask yourself.");

        var roster = await ResolveRosterAsync(fromAgentId, cancellationToken).ConfigureAwait(false);
        var target = roster.FirstOrDefault(p => string.Equals(p.AgentId, toAgentId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
            return new(false, $"'{toAgentId}' is not a peer you can ask. Your directory: "
                + string.Join(", ", roster.Select(p => $"{p.Name} ({p.AgentId})")));

        var askedToday = await CountAsksSinceAsync(fromAgentId, nowUtc.AddDays(-1), cancellationToken).ConfigureAwait(false);
        if (askedToday >= DailyAskLimit)
            return new(false, $"You have used all {DailyAskLimit} peer asks for today. Note the open question in your report instead.");

        var ask = new PeerAsk(Guid.NewGuid(), fromAgentId, toAgentId,
            subject.Trim(), body.Trim(), RequiredHop, nowUtc);

        await DeliverAskAsync(ask, cancellationToken).ConfigureAwait(false);
        _askDispatchedThisCycle = true;
        return new(true, Ask: ask);
    }

    /// <summary>
    /// Answer a pending ask. The answer goes to the ASKER and nobody else, and only fresh,
    /// hop-valid asks are answerable.
    /// </summary>
    public async Task<ConsultationOutcome> TryAnswerAsync(
        PeerAsk ask,
        string answeringAgentId,
        string body,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(body))
            return new(false, "An answer needs content.");

        if (!string.Equals(ask.ToAgentId, answeringAgentId, StringComparison.OrdinalIgnoreCase))
            return new(false, "That question was not addressed to you.");

        if (ask.Hop != RequiredHop)
            return new(false, "That ask has an invalid hop marker and must not be answered — asks never forward.");

        if (nowUtc - ask.CreatedAt > AskFreshnessWindow)
            return new(false, "That question is stale; answering it now would mislead more than help.");

        var answer = new PeerAnswer(Guid.NewGuid(), ask.Id, answeringAgentId, ask.FromAgentId, body.Trim(), nowUtc);
        await DeliverAnswerAsync(answer, cancellationToken).ConfigureAwait(false);
        return new(true, Answer: answer);
    }

    /// <summary>
    /// The asks a cycle should actually see: fresh, hop-valid, oldest first, capped. Sealed so
    /// a host cannot accidentally prompt an agent with a flood or a forwarded ask.
    /// </summary>
    public static IReadOnlyList<PeerAsk> FilterActionableAsks(
        IEnumerable<PeerAsk> pending,
        DateTimeOffset nowUtc)
        => pending
            .Where(a => a.Hop == RequiredHop)
            .Where(a => nowUtc - a.CreatedAt <= AskFreshnessWindow)
            .OrderBy(a => a.CreatedAt)
            .Take(MaxPendingAsksPerCycle)
            .ToList();

    // ── What a host supplies ─────────────────────────────────────────────────────────────────

    /// <summary>The peers this agent may ask — excluding itself is the caller's rail, not the roster's job.</summary>
    protected abstract Task<IReadOnlyList<PeerAgentCard>> ResolveRosterAsync(
        string forAgentId, CancellationToken cancellationToken);

    /// <summary>How many asks this agent has sent since <paramref name="sinceUtc"/> — an accurate count, not a window guess.</summary>
    protected abstract Task<int> CountAsksSinceAsync(
        string agentId, DateTimeOffset sinceUtc, CancellationToken cancellationToken);

    /// <summary>Physically deliver an ask (desktop: signed agent mail; hosted: tenant row + trigger).</summary>
    protected abstract Task DeliverAskAsync(PeerAsk ask, CancellationToken cancellationToken);

    /// <summary>Physically deliver an answer back to the asker.</summary>
    protected abstract Task DeliverAnswerAsync(PeerAnswer answer, CancellationToken cancellationToken);
}
