namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Host seam driven by the reusable Model Scout maintenance scheduler
/// (SwarmCore.LlmRouting). The scheduler owns the clock — an initial delay and
/// a short poll loop — while the host owns what a cycle does and any durable
/// cross-process checkpointing that prevents duplicate work.
/// </summary>
public interface IModelScoutMaintenancePass
{
    /// <summary>
    /// Process-level kill switch (environment/diagnostic guards). Checked before
    /// every cycle; returning true stops the scheduler for this process.
    /// </summary>
    bool IsDisabledForProcess();

    /// <summary>
    /// Whether automatic Scout management is currently on. A false read skips
    /// the cycle without stopping the scheduler.
    /// </summary>
    Task<bool> IsEnabledAsync(CancellationToken ct = default);

    /// <summary>
    /// One scheduled cycle: run the due recovery checkpoint and, when the
    /// host's own cadence says so, the full maintenance pass. Called on every
    /// poll tick while enabled; the host decides internally what is due.
    /// </summary>
    Task RunCycleAsync(CancellationToken ct = default);
}
