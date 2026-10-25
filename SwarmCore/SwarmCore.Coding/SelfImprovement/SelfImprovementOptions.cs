using SwarmCore.Coding.Routing;

namespace SwarmCore.Coding.SelfImprovement;

/// <summary>
/// Options for the self-improvement loop (<c>Coding:SelfImprovement</c>). The loop is on by default but inert until the
/// host finds the product's own source repository (<see cref="RepositoryRoot"/>, or the host's detection).
/// </summary>
public sealed class SelfImprovementOptions
{
    public const string SectionName = "Coding:SelfImprovement";

    public bool Enabled { get; set; } = true;

    /// <summary>Explicitly opt in to automatically installing verified goal-linked repairs at a safe idle point.</summary>
    public bool AutoApplyGoalRepairs { get; set; }

    /// <summary>The product's source checkout that cycles repair. Null lets the host detect it; an installed app without source stays inert.</summary>
    public string? RepositoryRoot { get; set; }

    /// <summary>How long the person must have been away (no input) before a cycle may start. Default 10 minutes.</summary>
    public TimeSpan UserIdleAfter { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Upper bound between checks for work (the loop also wakes on new items and edits).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Wait before the first check after the host starts.</summary>
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Cycles per item before it is handed to a person. Default 2.</summary>
    public int MaxAttemptsPerItem { get; set; } = 2;

    /// <summary>Wait after a failed cycle before the item is tried again. Default 6 hours.</summary>
    public TimeSpan RetryCooldown { get; set; } = TimeSpan.FromHours(6);

    /// <summary>A whole cycle (agent turn with its build and test checks, commit) must finish within this. Default 90 minutes.</summary>
    public TimeSpan CycleTimeout { get; set; } = TimeSpan.FromMinutes(90);

    /// <summary>New sightings are not queued once this many items are open. Default 200.</summary>
    public int MaxQueued { get; set; } = 200;

    /// <summary>Lane and model for cycles; null uses the person's defaults.</summary>
    public string? Lane { get; set; }
    public string? Model { get; set; }

    /// <summary>Let native CLI lanes (Claude Code, Codex) run cycles. They act without approval prompts, like scheduled runs.</summary>
    public bool AllowNativeLanes { get; set; } = true;

    /// <summary>Cancel a running cycle when the person comes back; the item returns to the queue without counting an attempt.</summary>
    public bool StopWhenUserReturns { get; set; }

    /// <summary>
    /// Let cycles start while a debugger is attached to the host. Off by default, so an ordinary F5 session is never
    /// interrupted; a dedicated "self-improvement" launch profile turns it on.
    /// </summary>
    public bool AllowUnderDebugger { get; set; }

    /// <summary>Fast-forward a verified fix into the source checkout when the checkout is clean and still at the cycle's base commit.</summary>
    public bool MergeVerifiedFixes { get; set; }

    /// <summary>Keep the worktree of a failed cycle for inspection (otherwise it is removed; the session transcript stays).</summary>
    public bool KeepFailedWorktrees { get; set; }

    /// <summary>Build and test commands run in the cycle's worktree. Empty: discovered from the repository layout.</summary>
    public List<CodingAcceptanceCommand> Checks { get; set; } = [];

    /// <summary>Observe turn and tool failures that look like product defects.</summary>
    public bool CaptureDefects { get; set; } = true;

    /// <summary>Observe capability gaps: declines that name a missing capability, and unknown slash commands.</summary>
    public bool CaptureFeatureRequests { get; set; } = true;

    public int MaxEvidenceCharacters { get; set; } = 6000;
    public int MaxDetailCharacters { get; set; } = 4000;
}
