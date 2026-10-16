namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Tracks LLM response quality per function type and auto-reverts to known-good config
/// when a configuration change causes quality degradation (consecutive failures/empty responses).
/// Optional dependency: if not registered, the router operates without this feature.
/// </summary>
public interface IKnownGoodConfigTracker
{
    /// <summary>
    /// Record a successful LLM response for a function type.
    /// After consecutive successes, the current config becomes the "known good" config.
    /// </summary>
    void RecordSuccess(string functionType, string provider, string model);

    /// <summary>
    /// Record a failed LLM response for a function type.
    /// After consecutive failures exceeding threshold, triggers auto-revert if a known-good config exists
    /// and the current config was recently changed.
    /// </summary>
    /// <returns>True if an auto-revert was triggered, false otherwise</returns>
    Task<bool> RecordFailureAndCheckRevertAsync(string functionType, string provider, string model);

    /// <summary>
    /// Notify the tracker that a function preference was changed by the user or system.
    /// Sets the "config changed" timestamp so degradation can be correlated with the change.
    /// </summary>
    void NotifyConfigChanged(string functionType, string newProvider, string newModel);
}
