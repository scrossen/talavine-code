namespace SwarmCode.Hosting;

/// <summary>
/// Housekeeping that runs only while the workspace is idle, on the same schedule as memory consolidation ("dreaming").
/// A step owns its own switch, cadence and safety checks; the loop only guarantees no turn is running when it calls.
/// </summary>
public interface IIdleMaintenanceStep
{
    /// <summary>Short name for log lines.</summary>
    string Name { get; }

    /// <summary>True when the step wants to run now (enabled, and its interval has passed).</summary>
    bool IsDue(DateTimeOffset now);

    Task RunAsync(CancellationToken ct);
}
