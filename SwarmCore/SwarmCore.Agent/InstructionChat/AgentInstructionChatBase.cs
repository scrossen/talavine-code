namespace SwarmCore.Agent.InstructionChat;

/// <summary>A credential pulled out of the boss's chat text before any model sees it.</summary>
public sealed record InstructionCredential(string Name, string Secret);

/// <summary>Host extraction result: the text with secrets replaced by placeholders, plus what was pulled out.</summary>
public sealed record InstructionExtraction(
    string SanitizedText,
    IReadOnlyList<InstructionCredential> Credentials);

/// <summary>
/// Host amendment result for one chat turn: the agent's conversational reply,
/// the full revised recipe when the instruction changes standing behavior
/// (null when nothing should change), any clarifying questions, and — when
/// the boss asked for immediate one-off ACTION rather than a rule change — a
/// self-contained task brief the host should hand to the agent's real
/// (tool-equipped) work cycle right now.
/// </summary>
public sealed record InstructionAmendment(
    string Reply,
    string? RecipeText,
    IReadOnlyList<string> Questions,
    string? RunNowTask = null);

/// <summary>What one boss message produced, ready for the UI to render.</summary>
public sealed record InstructionChatTurn(
    string SanitizedUserText,
    string Acknowledgement,
    string Reply,
    IReadOnlyList<string> Questions,
    string? ProposedRecipeText,
    bool ProposalChanged,
    IReadOnlyList<string> PendingCredentialNames,
    IReadOnlyList<string> CapabilityGaps,
    string? Error,
    string? RunNowTask = null);

/// <summary>Outcome of applying the accumulated proposal.</summary>
public sealed record InstructionApplyOutcome(
    bool Applied,
    string Detail,
    string? Error = null,
    string? AppliedRecipeText = null);

/// <summary>One completed exchange line in the instruction chat (sanitized text only).</summary>
public sealed record InstructionChatMessage(bool FromBoss, string Text);

/// <summary>
/// A serializable snapshot of an instruction-chat session so a conversation
/// survives leaving the page. Contains everything the rails hold between
/// turns — including captured credential VALUES, so hosts must persist it
/// with the same care as any other secret (protected storage, never logs,
/// never prompts). <paramref name="ProposalBaseRecipeText"/> is the applied
/// recipe the proposal was drafted from; a restored proposal is kept only
/// while that base is still the current recipe.
/// </summary>
public sealed record InstructionChatSessionState(
    string? ProposedRecipeText,
    IReadOnlyList<InstructionChatMessage> Transcript,
    IReadOnlyList<InstructionCredential> PendingCredentials,
    string? ProposalBaseRecipeText = null);

/// <summary>
/// Sealed rails for the "chat with an agent to change how it works" surface —
/// the instruction-chat analogue of <c>PeerConsultationBase</c>. The rails own
/// the trust boundary and the state machine; hosts supply transport and
/// storage through the abstract methods.
///
/// Invariants the rails enforce (tested in AgentInstructionChatRailsTests):
/// - Secret extraction ALWAYS runs before the amend step; the raw text the
///   boss typed is never handed to <see cref="AmendAsync"/> and never returned
///   for display — only the sanitized form leaves this class.
/// - Extracted credentials accumulate across turns (deduped by name, latest
///   value wins) and travel to the host only at apply time; a successful apply
///   clears them, a failed or refused apply keeps them.
/// - Each turn amends the latest proposal (not the applied recipe), so the
///   conversation iterates on one evolving document.
/// - An amend failure keeps the prior proposal intact and reports the error.
/// - Apply without a proposal is refused without touching the host.
/// - A proposal remembers the applied recipe it was drafted from
///   (<see cref="ProposalBaseRecipeText"/>) so hosts can reject an apply over
///   a recipe that changed elsewhere, and a restored proposal whose base is no
///   longer current is dropped instead of silently reverting newer edits.
/// </summary>
public abstract class AgentInstructionChatBase
{
    private readonly List<InstructionCredential> _pendingCredentials = [];
    private readonly List<InstructionChatMessage> _transcript = [];

    /// <summary>The recipe the agent is actually running (last applied/loaded).</summary>
    public string CurrentRecipeText { get; private set; } = "";

    /// <summary>The evolving proposed recipe, or null when nothing is proposed yet.</summary>
    public string? ProposedRecipeText { get; private set; }

    public bool HasProposal => !string.IsNullOrWhiteSpace(ProposedRecipeText);

    /// <summary>
    /// The applied recipe the current proposal was drafted from (null when
    /// nothing is proposed). Hosts pass it as the expected base when applying.
    /// </summary>
    public string? ProposalBaseRecipeText { get; private set; }

    public IReadOnlyList<InstructionCredential> PendingCredentials => _pendingCredentials;

    /// <summary>
    /// Completed exchanges so far (sanitized boss messages + agent replies,
    /// oldest first). Kept by the rails so every amend sees the whole
    /// conversation, not just the latest message — otherwise the agent
    /// forgets answers it was given two turns ago.
    /// </summary>
    public IReadOnlyList<InstructionChatMessage> Transcript => _transcript;

    /// <summary>Load (or reload) the applied recipe and drop any in-flight proposal, credentials, and transcript.</summary>
    public void Reset(string currentRecipeText)
    {
        CurrentRecipeText = currentRecipeText ?? "";
        ProposedRecipeText = null;
        ProposalBaseRecipeText = null;
        _pendingCredentials.Clear();
        _transcript.Clear();
    }

    /// <summary>Drop the in-flight proposal and the credentials captured for it.</summary>
    public void DiscardProposal()
    {
        ProposedRecipeText = null;
        ProposalBaseRecipeText = null;
        _pendingCredentials.Clear();
    }

    /// <summary>Snapshot the session for persistence (see <see cref="InstructionChatSessionState"/>'s secrecy note).</summary>
    public InstructionChatSessionState ExportState() =>
        new(ProposedRecipeText, _transcript.ToList(), _pendingCredentials.ToList(), ProposalBaseRecipeText);

    /// <summary>
    /// Restore a previously exported session over freshly loaded recipe text.
    /// A restored proposal is dropped when it no longer differs from the
    /// applied recipe (it was applied since the session was saved) or when the
    /// recipe it was drafted from is no longer the applied one (or unknown):
    /// the recipe changed elsewhere, and applying the old draft would revert
    /// those newer edits. The transcript and captured credentials are kept.
    /// </summary>
    public void Restore(string currentRecipeText, InstructionChatSessionState state)
    {
        Reset(currentRecipeText);

        foreach (var message in state.Transcript ?? [])
        {
            if (!string.IsNullOrWhiteSpace(message?.Text))
            {
                _transcript.Add(message!);
            }
        }

        AccumulateCredentials(state.PendingCredentials ?? []);

        var proposal = state.ProposedRecipeText;
        if (!string.IsNullOrWhiteSpace(proposal)
            && !SameRecipe(proposal, CurrentRecipeText)
            && state.ProposalBaseRecipeText is { } proposalBase
            && SameRecipe(proposalBase, CurrentRecipeText))
        {
            ProposedRecipeText = proposal;
            ProposalBaseRecipeText = proposalBase;
        }
    }

    /// <summary>
    /// Run one boss message through the rails: extract secrets, amend the
    /// working proposal from the sanitized text, and report what changed.
    /// </summary>
    public async Task<InstructionChatTurn> HandleUserMessageAsync(string rawText, CancellationToken ct = default)
    {
        var extraction = ExtractSecrets(rawText ?? "");
        AccumulateCredentials(extraction.Credentials);

        var sanitized = extraction.SanitizedText ?? "";
        var acknowledgement = BuildAcknowledgement(extraction);

        if (string.IsNullOrWhiteSpace(sanitized) && extraction.Credentials.Count == 0)
        {
            return Turn(sanitized, acknowledgement,
                reply: "", questions: [], proposalChanged: false,
                capabilityGaps: [], error: "Say what the agent should do differently, or paste the instructions to follow.");
        }

        var baseline = ProposedRecipeText ?? CurrentRecipeText;
        var priorTurns = _transcript.ToList();
        InstructionAmendment amendment;
        try
        {
            amendment = await AmendAsync(baseline, priorTurns, sanitized, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The prior proposal (and captured credentials) survive a bad
            // turn, and the message still counts as said — the next turn's
            // model must see it even though this one errored.
            _transcript.Add(new InstructionChatMessage(FromBoss: true, sanitized));
            return Turn(sanitized, acknowledgement,
                reply: "", questions: [], proposalChanged: false,
                capabilityGaps: await SafeDetectGapsAsync(baseline, ct),
                error: ex.Message);
        }

        _transcript.Add(new InstructionChatMessage(FromBoss: true, sanitized));
        if (!string.IsNullOrWhiteSpace(amendment.Reply))
        {
            _transcript.Add(new InstructionChatMessage(FromBoss: false, amendment.Reply!));
        }

        var proposalChanged = false;
        if (!string.IsNullOrWhiteSpace(amendment.RecipeText)
            && !string.Equals(amendment.RecipeText!.Trim(), baseline.Trim(), StringComparison.Ordinal))
        {
            ProposalBaseRecipeText ??= CurrentRecipeText;
            ProposedRecipeText = amendment.RecipeText;
            proposalChanged = true;
        }

        var gaps = await SafeDetectGapsAsync(ProposedRecipeText ?? CurrentRecipeText, ct);
        return Turn(sanitized, acknowledgement,
            amendment.Reply ?? "", amendment.Questions ?? [], proposalChanged, gaps, error: null,
            runNowTask: string.IsNullOrWhiteSpace(amendment.RunNowTask) ? null : amendment.RunNowTask);
    }

    /// <summary>
    /// Record a deterministic, read-only answer supplied by the host. This lets a product answer
    /// questions about configuration it owns without asking a model to guess, while preserving
    /// the exchange in the same saved transcript as model-backed turns.
    /// </summary>
    public void RecordInformationalExchange(string bossMessage, string assistantReply)
    {
        if (!string.IsNullOrWhiteSpace(bossMessage))
            _transcript.Add(new InstructionChatMessage(FromBoss: true, bossMessage.Trim()));
        if (!string.IsNullOrWhiteSpace(assistantReply))
            _transcript.Add(new InstructionChatMessage(FromBoss: false, assistantReply.Trim()));
    }

    /// <summary>
    /// Apply the accumulated proposal (and captured credentials) through the
    /// host. A successful apply promotes the proposal to current and clears
    /// the pending credentials; anything else leaves the session untouched.
    /// </summary>
    public async Task<InstructionApplyOutcome> ApplyProposalAsync(CancellationToken ct = default)
    {
        if (!HasProposal)
        {
            return new InstructionApplyOutcome(false,
                "Nothing to apply yet — ask for a change first.",
                Error: "no-proposal");
        }

        var outcome = await ApplyRecipeAsync(ProposedRecipeText!, PendingCredentials, ct);
        if (outcome.Applied)
        {
            CurrentRecipeText = outcome.AppliedRecipeText ?? ProposedRecipeText!;
            ProposedRecipeText = null;
            ProposalBaseRecipeText = null;
            _pendingCredentials.Clear();
        }

        return outcome;
    }

    // ── Host closures ──────────────────────────────────────────────

    /// <summary>Deterministic, pre-model secret extraction over the boss's raw text.</summary>
    protected abstract InstructionExtraction ExtractSecrets(string rawText);

    /// <summary>
    /// Turn the sanitized instruction into a reply + (optionally) a revised
    /// recipe. May call an LLM. <paramref name="priorTurns"/> is the whole
    /// conversation before this message (sanitized, oldest first) — the host
    /// must give the model that context or the agent forgets earlier answers.
    /// </summary>
    protected abstract Task<InstructionAmendment> AmendAsync(
        string recipeText,
        IReadOnlyList<InstructionChatMessage> priorTurns,
        string sanitizedRequest,
        CancellationToken ct);

    /// <summary>Capability ids the recipe needs but the agent has not been granted (may be empty).</summary>
    protected abstract Task<IReadOnlyList<string>> DetectCapabilityGapsAsync(string recipeText, CancellationToken ct);

    /// <summary>Persist the proposal: recompile + store (or push to the running agent) and store the credentials.</summary>
    protected abstract Task<InstructionApplyOutcome> ApplyRecipeAsync(
        string recipeText, IReadOnlyList<InstructionCredential> credentials, CancellationToken ct);

    // ── Internals ─────────────────────────────────────────────────

    /// <summary>Recipe identity for base checks: line endings and outer whitespace do not count.</summary>
    public static bool SameRecipe(string? left, string? right) =>
        string.Equals(NormalizeRecipe(left), NormalizeRecipe(right), StringComparison.Ordinal);

    private static string NormalizeRecipe(string? text) =>
        (text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    private void AccumulateCredentials(IReadOnlyList<InstructionCredential> credentials)
    {
        foreach (var credential in credentials)
        {
            if (string.IsNullOrWhiteSpace(credential.Name))
            {
                continue;
            }

            _pendingCredentials.RemoveAll(c =>
                string.Equals(c.Name, credential.Name, StringComparison.OrdinalIgnoreCase));
            _pendingCredentials.Add(credential);
        }
    }

    private async Task<IReadOnlyList<string>> SafeDetectGapsAsync(string recipeText, CancellationToken ct)
    {
        try
        {
            return await DetectCapabilityGapsAsync(recipeText, ct) ?? [];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Gap detection is advisory; it must never sink a turn.
            return [];
        }
    }

    private InstructionChatTurn Turn(
        string sanitized,
        string acknowledgement,
        string reply,
        IReadOnlyList<string> questions,
        bool proposalChanged,
        IReadOnlyList<string> capabilityGaps,
        string? error,
        string? runNowTask = null) =>
        new(sanitized, acknowledgement, reply, questions,
            ProposedRecipeText, proposalChanged,
            _pendingCredentials.Select(c => c.Name).ToList(),
            capabilityGaps, error, runNowTask);

    private static string BuildAcknowledgement(InstructionExtraction extraction) =>
        extraction.Credentials.Count == 0
            ? ""
            : $"Captured {extraction.Credentials.Count} credential(s) into the locked store " +
              $"({string.Join(", ", extraction.Credentials.Select(c => c.Name))}) — values never shown to any model; " +
              "the instructions reference them as {{secret:name}}.";
}
