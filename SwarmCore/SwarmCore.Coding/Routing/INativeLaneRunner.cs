using SwarmCore.Agent.Runtime;

namespace SwarmCore.Coding.Routing;

/// <summary>One turn for a lane that runs an external coding agent (Claude Code, Codex) natively.</summary>
public sealed record NativeLaneTurn(
    string ConversationId,
    string UserMessage,
    CodingLane Lane,
    string RepositoryRoot,
    string Authority,
    bool IsContinuation)
{
    public CodingExecutionLimits? ExecutionLimits { get; init; }
    /// <summary>Value-free host intake evidence; never model-authored metadata.</summary>
    public SwarmCore.Coding.Secrets.CredentialIntakeReceipt? CredentialIntake { get; init; }
    /// <summary>Profile the native turn persists under. Null uses <see cref="CodingOptions.ProfileId"/>.</summary>
    public Guid? ProfileId { get; init; }
    /// <summary>
    /// Display name of the resolved agent profile, reported in <see cref="TurnStarted.ProfileName"/>. Never the
    /// lane name. Null uses <see cref="CodingOptions.ProfileName"/> for the host's own profile.
    /// </summary>
    public string? ProfileName { get; init; }

    /// <summary>The profile identity and name this turn reports, given the host's options.</summary>
    public (Guid Id, string Name) ReportedProfile(CodingOptions options)
    {
        var id = ProfileId ?? options.ProfileId;
        var name = !string.IsNullOrWhiteSpace(ProfileName) ? ProfileName!
            : id == options.ProfileId ? options.ProfileName
            : id.ToString(); // a host profile whose name the host did not supply: its identity, never the lane
        return (id, name);
    }
    public IReadOnlyList<SwarmCore.Coding.Files.CodingAttachment> Attachments { get; init; } = [];
    /// <summary>Host correlation from <see cref="CodingTurnRequest.Metadata"/>.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
    /// <summary>Containment for this turn. Null uses <see cref="NativeAgentOptions.DefaultContainment"/>.</summary>
    public NativeLaneContainment? Containment { get; init; }
    /// <summary>Host plan mode (<see cref="CodingTurnRequest.PlanMode"/>): run the CLI in its read-only plan mode.</summary>
    public bool PlanMode { get; init; }

    /// <summary>
    /// Host context blocks for the CLI prompt (from <see cref="CodingOptions.NativeLaneInjectorNames"/>): reference data such
    /// as the user's memory digest or the repository memory index. Never authorization.
    /// </summary>
    public IReadOnlyList<NativeHostContextBlock> HostContext { get; init; } = [];

    /// <summary>The effective containment given the host's native-agent options.</summary>
    public NativeLaneContainment EffectiveContainment(NativeAgentOptions options)
        => Containment ?? (string.Equals(options.CodexSandbox, "workspace-write", StringComparison.OrdinalIgnoreCase)
            ? NativeLaneContainment.WorkspaceConfined : options.DefaultContainment);
}

/// <summary>One named block of host context for a native CLI prompt.</summary>
public sealed record NativeHostContextBlock(string Name, string Text);

/// <summary>
/// Runs a native coding-agent lane: the external CLI owns its tools and loop inside the
/// repository, and the runner translates its event stream into <see cref="TurnEvent"/>s and
/// persists the user/assistant turn plus tool receipts so other lanes can continue the work.
/// Like an engine turn, the stream opens with <see cref="TurnStarted"/> carrying the persisted
/// presentation row (<see cref="TurnStarted.ConversationTurnId"/>) once the user turn is saved, so a
/// host can join the native attempt to its durable objective.
/// </summary>
public interface INativeLaneRunner
{
    /// <summary>The registered provider whose catalog contains models understood by this CLI.</summary>
    string? ModelCatalogProvider => null;
    bool Supports(CodingLane lane);

    /// <summary>
    /// Whether this CLI can be held to <paramref name="containment"/>. A runner that cannot guarantee
    /// <see cref="NativeLaneContainment.WorkspaceConfined"/> returns false, and the orchestrator never
    /// uses its lane for a confined request.
    /// </summary>
    bool SupportsContainment(NativeLaneContainment containment) => true;

    /// <summary>
    /// Whether this CLI has a verified read-only plan mode the runner applies for <see cref="NativeLaneTurn.PlanMode"/>.
    /// The orchestrator never uses a lane without one for a plan-mode request.
    /// </summary>
    bool SupportsPlanMode => false;

    IAsyncEnumerable<TurnEvent> RunAsync(NativeLaneTurn turn, CancellationToken ct = default);
}
