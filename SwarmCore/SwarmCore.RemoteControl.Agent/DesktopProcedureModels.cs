using System.Text.Json.Nodes;
using SwarmCore.Agent.Procedures;

namespace SwarmCore.RemoteControl.Agent;

/// <summary>Host proof for a desktop task beyond the procedure's own success condition (for example an order total read from the host's API).</summary>
public sealed record DesktopProcedureProof(bool Succeeded, string Status);

/// <summary>
/// What a desktop procedure is for. The host owns every value: the key and binding (see <see cref="DesktopProcedureKeys"/>),
/// the per-run inputs (item name, quantity, URL), and any extra proof. Every saved procedure ends with a
/// <c>desktop.verify</c> step whose wait-for condition the host re-checks against the live desktop before it
/// accepts a run; <see cref="Verify"/> adds a stronger host contract when one exists.
/// </summary>
public sealed record DesktopProcedureTask
{
    public required AgentProcedureKey Key { get; init; }
    public required string BindingFingerprint { get; init; }
    /// <summary>Per-run values. Recorded arguments equal to one of these become bindings, never literals.</summary>
    public JsonObject Inputs { get; init; } = new();
    /// <summary>
    /// Private slots this task resolves through <see cref="ResolveSlotAsync"/> (for example <see cref="DesktopSlots.EmailCode"/>).
    /// Names are durable literals in steps; include them in the binding fingerprint.
    /// </summary>
    public IReadOnlyCollection<string> SlotNames { get; init; } = [];
    /// <summary>
    /// Host callback that supplies a slot value at the moment the step runs: a one-time code is read from the host's own
    /// mailbox then (never stored). Null, or a null result, stops the run with an authentication-required boundary.
    /// </summary>
    public Func<string, CancellationToken, Task<string?>>? ResolveSlotAsync { get; init; }
    /// <summary>
    /// How long a <c>desktop.person_step</c> boundary waits for its condition while a person acts (enters a code sent by
    /// text). Zero (the default) stops replay at the boundary with an authentication-required stop instead.
    /// </summary>
    public TimeSpan PersonTimeout { get; init; } = TimeSpan.Zero;
    /// <summary>Optional host alert when a run reaches a person boundary (reason and the step's description).</summary>
    public Func<string, CancellationToken, Task>? NotifyPersonAsync { get; init; }
    /// <summary>Optional host proof; when set, both it and the procedure's success condition must pass.</summary>
    public Func<CancellationToken, Task<DesktopProcedureProof>>? Verify { get; init; }
    public string VerifierId { get; init; } = "desktop-verify-v1";
    public string SuccessMessage { get; init; } = "The desktop task completed and its success condition was re-checked on the live desktop.";
    /// <summary>How long replay waits (read-only) for a named control or window to appear before an action step.</summary>
    public TimeSpan ReadinessTimeout { get; init; } = TimeSpan.FromSeconds(8);
    /// <summary>How long the desktop.verify step waits for its condition.</summary>
    public TimeSpan VerifyTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>How long the host's fresh re-check of the condition may wait.</summary>
    public TimeSpan RecheckTimeout { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Bounds for the observation returned with a stop (kept small enough for a prompt).</summary>
    public int MaxObservedWindows { get; init; } = 30;
    public int MaxObservedControls { get; init; } = 120;
}

public enum DesktopProcedureStatus
{
    /// <summary>Every step ran and the host re-check passed; the run was accepted.</summary>
    Completed,
    /// <summary>No active verified procedure exists for this key and binding: do the task with the model, then save it.</summary>
    NoProcedure,
    /// <summary>Replay stopped at a step; see <see cref="DesktopProcedureOutcome.Stop"/> to repair.</summary>
    Stopped,
    /// <summary>An earlier action's effect is unknown; inspect the desktop and finish by hand, then save (which records the inspection).</summary>
    InspectionRequired,
    /// <summary>
    /// The run reached a step only a person can complete (a sign-in code sent by text or call, or a private value the
    /// host could not supply). The saved revision stays active and will stop here deliberately again.
    /// </summary>
    PersonRequired,
    /// <summary>A recorded run was compiled, verified and promoted to the active revision.</summary>
    Saved,
    /// <summary>The recorded run could not be saved; the message says why.</summary>
    Refused,
}

public sealed record DesktopObservedWindow(string Alias, string Process, string Title, bool Foreground);

public sealed record DesktopObservedControl(string Alias, string Type, string Name, string? Value, bool Enabled);

/// <summary>A fresh, bounded look at the desktop for repair: the window list and the controls of the current window.</summary>
public sealed record DesktopObservation(IReadOnlyList<DesktopObservedWindow> Windows, string? CurrentWindow,
    IReadOnlyList<DesktopObservedControl> Controls, bool Truncated)
{
    public static readonly DesktopObservation Empty = new([], null, [], false);
}

/// <summary>Where and why replay stopped. <see cref="Step"/> is the parameterized stored step (never a secret value).</summary>
public sealed record DesktopProcedureStop(int StepIndex, AgentProcedureStep? Step, string Reason, string? FailureCategory,
    string Message, DesktopObservation Observation);

public sealed record DesktopProcedureOutcome(DesktopProcedureStatus Status, string Message)
{
    public DesktopProcedureStop? Stop { get; init; }
    public Guid? RevisionId { get; init; }
    public int StepsRun { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>One desktop tool call the host itself executed and observed (never a model's claim).</summary>
public sealed record DesktopRecordedCall(string ToolId, JsonObject Arguments, DesktopToolResult Result);
