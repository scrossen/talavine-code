using SwarmCore.Agent.Continual;

namespace SwarmCore.Coding.Goals;

public static class CodingGoalControlAuthority
{
    public static string Format(ContinualObjectiveSnapshot goal, CodingGoalPayload payload, bool inspectionOnly = false)
    {
        var control = payload.LastUserControl;
        var valid = control is not null && control.GoalId == goal.Id && control.GoalVersion > 0
            && control.GoalVersion <= goal.Version && control.AtUtc != default
            && control.AtUtc <= goal.UpdatedAtUtc && Enum.IsDefined(control.Control)
            && Enum.IsDefined(control.PreviousStatus) && !ContinualObjectiveStateMachine.IsTerminal(control.PreviousStatus)
            && control.ResultingStatus == (control.Control switch
            {
                CodingGoalControl.Resume => ContinualObjectiveStatus.RetryScheduled,
                CodingGoalControl.Cancel => ContinualObjectiveStatus.Cancelled,
                _ => ContinualObjectiveStatus.Paused,
            });
        var latest = valid
            ? $"Latest explicit host-accepted user control: {control!.Control}; at {control.AtUtc:O}; "
                + $"goal {control.GoalId:D}; control version {control.GoalVersion}; "
                + $"{control.PreviousStatus} -> {control.ResultingStatus}."
            : "No validated explicit control record is available for this older goal.";
        return "\n\nCurrent host execution control (separate from content instructions):\n"
            + $"Goal {goal.Id:D}; current state {goal.Status}; current version {goal.Version}.\n"
            + latest + "\n"
            + (valid && control!.Control == CodingGoalControl.Resume
                ? "The explicit Resume above supersedes older pause/setup instructions and old paused checkpoints for execution state. "
                    + (inspectionOnly ? "This attempt remains inspection only; remaining work runs separately after verification. "
                        : "Continue this same saved goal and remaining work. ")
                    + "It does not replace content corrections, remove restrictions, change the selected model, or authorize replay of prior actions.\n"
                : "")
            + "Historical outputs and repository checkpoints are evidence of their recorded time, not current pause controls. "
            + "Do not operate this goal's own Pause/Resume/Cancel controls to enforce historical instructions. "
            + "The host's current stop controls and live lease remain authoritative; stop when the host pauses or cancels execution.\n";
    }
}
