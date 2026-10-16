namespace SwarmCore.Abstractions.LlmRouting;

/// <summary>
/// Power state of the host machine. Platform-specific implementations populate this;
/// the abstraction stays cross-platform.
/// </summary>
public enum PowerSource
{
    Unknown = 0,
    Battery = 1,
    Plugged = 2,
}

/// <summary>
/// Snapshot of host-machine state relevant to dynamic routing. The router reads this
/// per call and forwards it to advisors so they can shift routing based on wall-clock
/// time, idle state, and power source (e.g., prefer local at night, prefer cloud on AC).
/// </summary>
public sealed record SystemActivitySnapshot(
    DateTimeOffset Now,
    TimeSpan? IdleFor,
    PowerSource Power,
    bool UserActive)
{
    public static SystemActivitySnapshot Active(DateTimeOffset? now = null) =>
        new(now ?? DateTimeOffset.Now, IdleFor: TimeSpan.Zero, Power: PowerSource.Unknown, UserActive: true);
}

/// <summary>
/// Provides system-activity snapshots for dynamic routing decisions. Default
/// implementation in <c>SwarmCore.LlmRouting</c> returns "user active, clock = now,
/// power = unknown"; platform-specific apps (MAUI/Windows) can register a richer
/// implementation that reads idle time and battery state.
/// </summary>
public interface ISystemActivitySignal
{
    SystemActivitySnapshot Current();
}
