namespace SwarmCore.Agent.Continual;

public enum ContinualDeliveryHostDisposition { Completed, WaitingForInput, Unavailable }

/// <summary>Interprets audit intent; the host alone persists the resulting state.</summary>
public static class ContinualDeliveryDisposition
{
    public const string UnavailableReason = "Verifier omitted or supplied an invalid required host disposition; verify the saved output without repeating completed actions.";
    public const string PromptInstructions = """
        For a delivered action objective include "hostDisposition": "completed" or "waiting_for_input".
        Bind this disposition to the ORIGINAL objective and any remaining fact, decision, authority or action
        only the person can supply. Choose waiting_for_input if the requested result requires the host to
        keep the objective pending for that dependency, even when reporting the blocker satisfied a fallback.
        An assistant saying it parked an objective is an untrusted claim: prose cannot mutate host state.
        The host will perform the waiting transition; do not pretend it already happened.
        Choose completed for a finished result or a clear decline/report explicitly permitted as the entire
        alternative deliverable, without a required pending objective. Information about a waiting state
        does not request that state. A completed receipt-backed action still needs successful exact references.
        """;

    public static ContinualDeliveryHostDisposition Resolve(ContinualDeliveryAudit? audit, bool actionObjective)
    {
        // Plain information can be its own deliverable. This contract is needed
        // only on audited action work, never inferred from the final prose.
        if (!actionObjective) return ContinualDeliveryHostDisposition.Completed;
        return audit?.HostDisposition?.Trim().ToLowerInvariant() switch
        {
            "completed" => ContinualDeliveryHostDisposition.Completed,
            "waiting_for_input" => ContinualDeliveryHostDisposition.WaitingForInput,
            _ => ContinualDeliveryHostDisposition.Unavailable
        };
    }
}
