using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SwarmCore.Missions;

/// <summary>Explicit structured-command overrides, independent of inherited host secrets.</summary>
internal static class MissionCommandEnvironment
{
    private static readonly ConditionalWeakTable<ProcessStartInfo, Dictionary<string, string>> Values = new();
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    { "PATH", "HOME", "USERPROFILE", "TEMP", "TMP", "TMPDIR", "APPDATA", "LOCALAPPDATA", "COMSPEC", "SYSTEMROOT", "WINDIR",
        "DOTNET_ROOT", "DOTNET_CLI_HOME", "NUGET_PACKAGES", "LD_PRELOAD", "LD_LIBRARY_PATH", "DYLD_INSERT_LIBRARIES", "DYLD_LIBRARY_PATH" };
    public static void Register(ProcessStartInfo start, IReadOnlyDictionary<string, string>? values)
    {
        if (values is null || values.Count == 0) return;
        if (values.Count > 32) throw new ArgumentException("At most 32 environment overrides are supported.");
        var safe = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values)
        {
            if (name.Length is < 1 or > 128 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_'))
                || char.IsDigit(name[0]) || Protected.Contains(name) || value is null || value.Length > 8192 || value.Contains('\0'))
                throw new ArgumentException("Invalid or reserved command environment override.");
            safe.Add(name, value);
        }
        Values.Add(start, safe);
    }
    public static IReadOnlyDictionary<string, string> Get(ProcessStartInfo start)
        => Values.TryGetValue(start, out var values) ? values : new Dictionary<string, string>();
    public static void Apply(ProcessStartInfo start)
    { foreach (var (name, value) in Get(start)) start.Environment[name] = value; }
}
