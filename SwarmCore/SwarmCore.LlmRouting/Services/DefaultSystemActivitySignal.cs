using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCore.LlmRouting.Services;

/// <summary>
/// Cross-platform default that knows the wall clock but nothing else. Apps that want
/// idle-aware or battery-aware routing (MAUI/Windows) register a richer implementation
/// over this default — for example, polling <c>GetLastInputInfo</c> on Windows.
/// </summary>
public sealed class DefaultSystemActivitySignal : ISystemActivitySignal
{
    public SystemActivitySnapshot Current() =>
        new(
            Now: DateTimeOffset.Now,
            IdleFor: null,
            Power: PowerSource.Unknown,
            UserActive: true);
}
