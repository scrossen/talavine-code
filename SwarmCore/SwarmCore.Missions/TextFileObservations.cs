using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using SwarmCore.Agent.Runtime;

namespace SwarmCore.Missions;

/// <summary>Automatically retain read/write byte identities within one agent turn, without retaining ended turns.</summary>
public static class TextFileObservations
{
    private static readonly ConditionalWeakTable<CurrentTurnToolAccessState, ConcurrentDictionary<string, string>> Observations = new();

    public static string? ExpectedSha256(string path)
    {
        var state = new AsyncLocalCurrentTurnContextAccessor().Current?.ToolAccessState;
        return state is not null && Observations.TryGetValue(state, out var files) && files.TryGetValue(Path.GetFullPath(path), out var digest) ? digest : null;
    }

    public static void Observe(string path, string sha256)
    {
        var state = new AsyncLocalCurrentTurnContextAccessor().Current?.ToolAccessState;
        if (state is null) return;
        var files = Observations.GetValue(state, _ => new ConcurrentDictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal));
        files[Path.GetFullPath(path)] = sha256;
    }
}
