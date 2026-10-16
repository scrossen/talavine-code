namespace SwarmCore.Agent.Marketing;

public enum BusinessAutomationMode { Review, Autonomous }

public enum BusinessAutomationActionKind
{
    Research, SaveProspect, QualifyProspect, FollowProspect, SendMessage,
    PublishContent, ScheduleMeeting, IssueProposal
}

/// <summary>
/// Owner-selected business automation settings. A mode is not an authority grant.
/// PartitionId is the host's tenant/profile identity, never a value taken from researched content.
/// Null ceilings defer to existing host ceilings; they do not disable those ceilings.
/// </summary>
public sealed record BusinessAutomationPolicy(string PartitionId, BusinessAutomationMode Mode,
    bool Paused = false, int? DailyExternalActionLimit = null, decimal? DailyCloudBudgetUsd = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PartitionId) || PartitionId.Length > 200)
            throw new ArgumentException("A bounded tenant or profile identity is required.");
        if (!Enum.IsDefined(Mode)) throw new ArgumentException("Choose Review or Autonomous.");
        if (DailyExternalActionLimit < 0 || DailyCloudBudgetUsd < 0)
            throw new ArgumentException("Daily limits cannot be negative.");
    }
}

/// <summary>
/// A host-classified operation. ExternalActionUnits includes every recipient/action in a batch.
/// CloudCostUsd is the amount the host will reserve, including an applicable provider upper bound;
/// this policy never estimates provider usage or replaces the shared spend ledger.
/// </summary>
public sealed record BusinessAutomationRequest(string PartitionId, BusinessAutomationActionKind Kind,
    int ExternalActionUnits = 1, decimal CloudCostUsd = 0);

/// <summary>
/// Fresh host evidence evaluated under the existing dispatch transaction or serialized lease.
/// Committed usage includes reservations and uncertain dispatched work, not only completed work.
/// ExactActionReviewed means the host verified approval of this precise frozen action/target/payload
/// and current policy revision; neither model wording nor a generic approval may supply it.
/// ExistingAuthorityGranted includes principal, capability, resource/account and eligibility checks.
/// </summary>
public sealed record BusinessAutomationContext(bool ExistingAuthorityGranted,
    bool Suppressed = false, bool ExactActionReviewed = false,
    int ExternalActionsCommittedToday = 0, decimal CloudSpendCommittedTodayUsd = 0);

public enum BusinessAutomationDecisionCode
{
    Allowed, InvalidRequest, PartitionMismatch, Paused, AuthorityDenied, Suppressed,
    ActionLimitReached, SpendLimitReached, ReviewRequired
}

public sealed record BusinessAutomationDecision(BusinessAutomationDecisionCode Code, string Detail)
{
    public bool Allowed => Code == BusinessAutomationDecisionCode.Allowed;
}

/// <summary>
/// Shared choice between review and execution within existing authority. Hosts must re-evaluate
/// fresh settings/evidence and atomically reserve the action BEFORE dispatch, preserving their
/// durable idempotency/reconciliation ledger. A successful evaluation alone is not a reservation.
/// Review applies to external effects; research and internal lead work can continue in either mode.
/// </summary>
public static class BusinessAutomationPolicyEvaluator
{
    public static bool HasExternalEffect(BusinessAutomationActionKind kind) => kind switch
    {
        BusinessAutomationActionKind.Research or BusinessAutomationActionKind.SaveProspect
            or BusinessAutomationActionKind.QualifyProspect => false,
        BusinessAutomationActionKind.FollowProspect or BusinessAutomationActionKind.SendMessage
            or BusinessAutomationActionKind.PublishContent or BusinessAutomationActionKind.ScheduleMeeting
            or BusinessAutomationActionKind.IssueProposal => true,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static BusinessAutomationDecision Evaluate(BusinessAutomationPolicy policy,
        BusinessAutomationRequest request, BusinessAutomationContext context)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        policy.Validate();
        if (string.IsNullOrWhiteSpace(request.PartitionId) || !Enum.IsDefined(request.Kind)
            || request.ExternalActionUnits < 1 || request.CloudCostUsd < 0
            || context.ExternalActionsCommittedToday < 0 || context.CloudSpendCommittedTodayUsd < 0)
            return new(BusinessAutomationDecisionCode.InvalidRequest, "The host action or usage evidence is invalid.");
        if (!string.Equals(policy.PartitionId, request.PartitionId, StringComparison.Ordinal))
            return new(BusinessAutomationDecisionCode.PartitionMismatch, "The action belongs to a different workspace.");
        if (policy.Paused)
            return new(BusinessAutomationDecisionCode.Paused, "Business automation is paused.");
        if (!context.ExistingAuthorityGranted)
            return new(BusinessAutomationDecisionCode.AuthorityDenied, "The current action lacks its existing authority or eligibility.");

        var external = HasExternalEffect(request.Kind);
        if (external && context.Suppressed)
            return new(BusinessAutomationDecisionCode.Suppressed, "The target is excluded from further external actions.");
        // Subtraction avoids overflow when a malicious or corrupt counter is near its maximum.
        if (external && policy.DailyExternalActionLimit is { } actionLimit
            && (context.ExternalActionsCommittedToday > actionLimit
                || request.ExternalActionUnits > actionLimit - context.ExternalActionsCommittedToday))
            return new(BusinessAutomationDecisionCode.ActionLimitReached, "The daily external action allowance is exhausted.");
        if (request.CloudCostUsd > 0 && policy.DailyCloudBudgetUsd is { } spendLimit
            && (context.CloudSpendCommittedTodayUsd > spendLimit
                || request.CloudCostUsd > spendLimit - context.CloudSpendCommittedTodayUsd))
            return new(BusinessAutomationDecisionCode.SpendLimitReached, "The cloud spend reservation exceeds the daily allowance.");
        if (external && policy.Mode == BusinessAutomationMode.Review && !context.ExactActionReviewed)
            return new(BusinessAutomationDecisionCode.ReviewRequired, "Review the exact proposed action before dispatch.");
        return new(BusinessAutomationDecisionCode.Allowed, external
            ? "The action fits the selected mode and existing authority; reserve it before dispatch."
            : "Research or internal lead work fits the current workspace authority.");
    }
}
