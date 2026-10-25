using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;

namespace SwarmCore.Coding.Goals;

/// <summary>
/// A trusted host protocol reads current state without replaying the uncertain operation.
/// Register implementations only for outcomes the host can independently establish. Model
/// assertions, an exit code, or a matching error string do not establish a physical outcome.
/// Return null for unsupported outcomes. A failed inspection leaves the receipt unresolved and
/// allows other registered host inspectors to try; cancellation and lease loss stop recovery.
/// </summary>
public interface ICodingGoalOutcomeInspector
{
    string Identity { get; }
    Task<CodingGoalOutcomeInspection?> InspectAsync(ContinualObjectiveSnapshot goal,
        ContinualToolReceipt target, CancellationToken ct = default);
}

public sealed record CodingGoalOutcomeInspection(ContinualReceiptResolutionOutcome Outcome, string Evidence);

/// <summary>Actual inspection progress; never a claim that the original action succeeded.</summary>
public sealed record CodingGoalRecoveryProgress(string TurnId, Guid GoalId, int Total, int Resolved,
    string Summary) : TurnEvent(TurnId);
