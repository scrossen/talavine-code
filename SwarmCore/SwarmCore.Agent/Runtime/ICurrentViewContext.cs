namespace SwarmCore.Agent.Runtime;

/// <summary>
/// A snapshot of what the user is currently looking at in the host
/// application. Pushed by UI surfaces when their active content changes
/// (e.g., a message is selected in a list) and consumed by a context
/// injector so the agent can answer questions about "this thing" /
/// "what I'm looking at".
/// </summary>
/// <param name="Kind">Short machine token for the kind of thing in view
/// (e.g., "email-message", "inbox-list", "document"). Lowercase by convention.</param>
/// <param name="Title">One-line human-readable label shown to the user in
/// the chat panel (e.g., "Email: RE: Meeting notes"). Kept short.</param>
/// <param name="Details">Multi-line block inlined into the system prompt.
/// Caller decides what to include. Null or empty means "surface the title
/// only".</param>
/// <param name="AllowedSourceAccountIds">Optional communication-source boundary
/// owned by the current surface. Message/compose/account views set this to the
/// selected account; general app pages leave it null.</param>
public sealed record CurrentViewSnapshot(
    string Kind,
    string Title,
    string? Details,
    IReadOnlySet<Guid>? AllowedSourceAccountIds = null);

/// <summary>
/// Well-known <see cref="CurrentViewSnapshot.Kind"/> values that carry
/// cross-component meaning. Pages may publish any kind they like; only the
/// generic fallback kind is special: structured page-published snapshots
/// must not be overwritten by the generic DOM text scrape.
/// </summary>
public static class CurrentViewSnapshotKinds
{
    /// <summary>
    /// Generic navigation-time capture of the main window (route + title +
    /// visible text). The lowest-fidelity snapshot — any structured snapshot
    /// published by a page wins over it.
    /// </summary>
    public const string GenericAppPage = "app-page";

    /// <summary>
    /// A single loaded message published by the message viewer. Shared so
    /// the producer and the teardown retraction check cannot drift apart.
    /// </summary>
    public const string EmailMessage = "email-message";

    /// <summary>
    /// The Model Scout System Agent workspace. This selects its voice and
    /// control-plane context; it is not a capability boundary. Routing claims
    /// and mutations still go through typed Model Scout tools, while mixed
    /// user questions may use other profile-authorized tools in the same turn.
    /// </summary>
    public const string ModelScoutSystemAgent = "system-agent:model-scout";
}

/// <summary>
/// App-wide, best-effort view signal used for UI chips and page handoff.
/// Separate windows share the DI container, so this is deliberately not a
/// security boundary and must never be read as the source scope of a turn.
/// The initiating surface copies an immutable snapshot into
/// <see cref="AgentTurnRequest.CurrentView"/> instead.
/// </summary>
public interface ICurrentViewContext
{
    /// <summary>Current snapshot, or null when no surface has set one.</summary>
    CurrentViewSnapshot? Current { get; }

    /// <summary>
    /// Raised after <see cref="Set"/>, <see cref="Clear"/>, or a successful
    /// <see cref="ClearIf"/>. Subscribers
    /// that touch UI state must marshal to the dispatcher themselves
    /// (<c>InvokeAsync(StateHasChanged)</c> in Blazor).
    /// </summary>
    event Action<CurrentViewSnapshot?>? Changed;

    void Set(CurrentViewSnapshot snapshot);
    void Clear();

    /// <summary>
    /// Clears the current snapshot only when it is the exact instance the
    /// caller observed or published. This makes page/window teardown atomic:
    /// a closing surface cannot erase a newer snapshot another surface set
    /// between a <see cref="Current"/> read and the clear attempt.
    /// </summary>
    /// <returns><see langword="true"/> when <paramref name="expected"/> was
    /// current and was cleared; otherwise <see langword="false"/>.</returns>
    bool ClearIf(CurrentViewSnapshot expected);
}
