using SwarmCore.Coding;

namespace SwarmCode.Hosting.Mcp;

/// <summary>
/// Finds the command that starts the vault bridge (<c>swarmcode --mcp-server</c>) for native
/// coding agents. The console is itself the bridge; the desktop app looks for the console next
/// to its own files or in the shared data directory. A host can always pin the command through
/// configuration (<c>Coding:NativeAgents:McpServer:Command</c>).
/// </summary>
public static class McpBridgeLocator
{
    public const string McpServerFlag = "--mcp-server";

    public static McpServerLaunch? Resolve(SwarmCodeDataPaths? paths = null)
        => Resolve(paths, AppContext.BaseDirectory, Environment.ProcessPath, OperatingSystem.IsWindows());

    internal static McpServerLaunch? Resolve(SwarmCodeDataPaths? paths, string baseDirectory, string? processPath, bool windows)
    {
        var self = processPath;
        if (!string.IsNullOrEmpty(self) && File.Exists(self)
            && Path.GetFileName(self).Equals(windows ? "swarmcode.exe" : "swarmcode", StringComparison.OrdinalIgnoreCase))
            return Launch(self, []);

        foreach (var dir in CandidateDirectories(paths, baseDirectory))
        {
            var exe = Path.Combine(dir, windows ? "swarmcode.exe" : "swarmcode");
            if (File.Exists(exe)) return Launch(exe, []);
            var dll = Path.Combine(dir, "swarmcode.dll");
            if (File.Exists(dll) && DotnetHost() is { } dotnet) return Launch(dotnet, [dll]);
        }
        return null;
    }

    private static IEnumerable<string> CandidateDirectories(SwarmCodeDataPaths? paths, string baseDirectory)
    {
        if (Secrets.MacVaultBridge.BundledHelper(baseDirectory) is { } helper
            && Secrets.MacVaultBridge.ContentsDirectory(helper) is { } contents
            && Secrets.MacVaultBridge.IsUnlinkedOwnedFile(contents, helper)) yield return Path.GetDirectoryName(helper)!;
        yield return baseDirectory;
        if (paths is not null) yield return Path.Combine(paths.RootDirectory, "bin");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local)) yield return Path.Combine(local, "SwarmCode", "bin");
    }

    private static McpServerLaunch Launch(string command, IEnumerable<string> leadingArguments)
        => new() { Command = command, Arguments = [.. leadingArguments, McpServerFlag] };

    /// <summary>The dotnet host running this process, or one on PATH.</summary>
    private static string? DotnetHost()
    {
        var current = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(current) && Path.GetFileNameWithoutExtension(current).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return current;
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, name))) return Path.Combine(root, name);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim('"'), name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>What native lanes are told (once per workspace) when this host found no bridge to hand them.</summary>
    public const string BridgeUnavailableReason =
        "The Talavine Code vault bridge (swarmcode --mcp-server) was not found on this machine, so native coding agents get no "
        + "access to your stored credentials: credentials_list and run_with_secrets are unavailable to them. Install the "
        + "Talavine Code console (or set Coding:NativeAgents:McpServer:Command) to hand them the vault.";

    /// <summary>Configuration overrides a host adds so the harness launches the bridge for native lanes.</summary>
    public static void AddOverrides(IDictionary<string, string?> overrides, SwarmCodeDataPaths? paths = null)
        => AddOverrides(overrides, paths, Resolve(paths));

    /// <summary>
    /// Adds the bridge launch, or, when none was found (for example an incomplete desktop bundle),
    /// <c>Coding:NativeAgents:McpServerUnavailableReason</c> so native lanes say once per workspace that credentials
    /// are unavailable to them instead of silently running without the vault.
    /// </summary>
    internal static void AddOverrides(IDictionary<string, string?> overrides, SwarmCodeDataPaths? paths, McpServerLaunch? launch)
    {
        if (launch is null)
        {
            overrides["Coding:NativeAgents:McpServerUnavailableReason"] = BridgeUnavailableReason;
            return;
        }
        overrides["Coding:NativeAgents:McpServer:Command"] = launch.Command;
        for (var i = 0; i < launch.Arguments.Count; i++)
            overrides[$"Coding:NativeAgents:McpServer:Arguments:{i}"] = launch.Arguments[i];
        // The bridge must open the host's vault even when SWARMCODE_HOME is not set in the process environment.
        if (paths is not null) overrides["Coding:NativeAgents:McpServer:Environment:SWARMCODE_HOME"] = paths.RootDirectory;
    }
}
