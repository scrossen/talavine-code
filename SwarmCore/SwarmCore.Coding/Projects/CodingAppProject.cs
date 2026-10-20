using System.Text.Json;
using SwarmCore.Missions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Coding.Permissions;

namespace SwarmCore.Coding.Projects;

public enum CodingProjectKind { ManagedApp, Project }

public sealed record CodingAppProject(string Id, string Name, string Description, string Root, DateTime CreatedUtc,
    CodingProjectKind Kind = CodingProjectKind.ManagedApp);

/// <summary>The host owns the catalog; project identity is independent of chats and launch revisions.</summary>
public sealed record CodingAppLibrary(string? AppsFolder, IReadOnlyList<CodingAppProject> Apps);

public static class CodingAppLaunchAuthority
{
    public static CodingPermissionDecision Decide(CodingPermissionPolicy policy)
    {
        var shell = policy.Decide("shell.exec", ToolSideEffect.Exec);
        var app = policy.Decide("apps.launch", ToolSideEffect.Exec);
        return shell.Verdict == CodingPermissionVerdict.Deny ? shell : app.Verdict == CodingPermissionVerdict.Deny ? app
            : shell.Verdict == CodingPermissionVerdict.Ask ? shell : app;
    }
}

public static class CodingAppPaths
{
    public static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(SandboxPathResolver.ResolvePhysicalPath(Path.GetFullPath(path)));
    public static bool Same(string left, string right) => string.Equals(Normalize(left), Normalize(right), Comparison);
    public static string Within(string root, string relative)
    {
        var fullRoot = Normalize(root);
        var full = Normalize(Path.Combine(fullRoot, relative));
        var prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!string.Equals(fullRoot, full, Comparison) && !full.StartsWith(prefix, Comparison))
            throw new InvalidOperationException("Choose a file or working folder inside this app.");
        return full;
    }

    public static string FolderName(string name)
    {
        var value = name.Trim();
        if (value.Length is 0 or > 80 || value is "." or ".." || value.EndsWith('.') || value.EndsWith(' ')
            || value.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c))
            || new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }
                .Contains(value.Split('.')[0], StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Give your app a name of 1–80 characters without file-path punctuation or reserved folder names.");
        return value;
    }
}

/// <summary>Executable arguments are an array, never an implicitly interpreted shell command.</summary>
public sealed record CodingAppLaunchPlan
{
    public string Program { get; init; } = "";
    public string[] Arguments { get; init; } = [];
    public string WorkingDirectory { get; init; } = ".";
    public string ReadyWhen { get; init; } = "http";
    public string? Url { get; init; }
    public int TimeoutSeconds { get; init; } = 60;

    public void Validate(string root)
    {
        if (string.IsNullOrWhiteSpace(Program) || Program.Any(char.IsControl) || Arguments is null || Arguments.Any(a => a is null)
            || Arguments.Length > 256 || TimeoutSeconds is < 1 or > 300 || ReadyWhen is not ("http" or "process" or "exit"))
            throw new InvalidDataException("The app's launch definition is incomplete. Use Set up Run app to repair it.");
        if (!Directory.Exists(CodingAppPaths.Within(root, WorkingDirectory)))
            throw new DirectoryNotFoundException("The app's launch working folder is missing.");
        if (ReadyWhen == "http")
        {
            if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
                || !uri.IsLoopback || uri.UserInfo.Length != 0)
                throw new InvalidDataException("An app preview must use an HTTP address on this computer, without credentials.");
        }
        else if (Url is not null) throw new InvalidDataException("Only HTTP readiness can have a preview address.");
    }
}

public static class CodingAppLaunchFile
{
    public const string RelativePath = ".swarmcode/app-run.json";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, PropertyNameCaseInsensitive = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public sealed record Manifest(int Version, Dictionary<string, CodingAppLaunchPlan> Targets);

    public static CodingAppLaunchPlan? Read(string root)
    {
        var path = CodingAppPaths.Within(root, RelativePath);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("The app launch definition is too large.");
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), Json);
        if (manifest is not { Version: 1, Targets: not null }) throw new InvalidDataException("Unsupported app launch definition.");
        var platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst() ? "macos" : "linux";
        if (!manifest.Targets.TryGetValue(platform, out var plan) && !manifest.Targets.TryGetValue("default", out plan)) return null;
        if (plan is null) throw new InvalidDataException("The app launch target is empty.");
        plan.Validate(root);
        return plan;
    }
}

public static class CodingAppPrompts
{
    public static string Create(CodingAppProject app) => $"Build this app in the current project folder.\nApp name: {app.Name}\nWhat I want: {app.Description}\n\nComplete the implementation, test it, and prepare Run app.\n{LaunchContract}";
    public static string SetupRun => $"Set up Run app for this existing project. Inspect its actual files and documentation, prepare required local dependencies under the current permission policy, and verify a working launch. Preserve existing code and data. Stop any test server you started before finishing so Run app can own its next launch.\n{LaunchContract}";
    public const string LaunchContract = """
        Save a credential-free .swarmcode/app-run.json with this schema:
        {"version":1,"targets":{"default":{"program":"executable","arguments":["argument"],"workingDirectory":".","readyWhen":"http","url":"http://127.0.0.1:PORT","timeoutSeconds":60}}}
        Targets may instead use windows, macos, or linux for platform-specific commands. Use actual executable names and separate arguments; no implicit shell parsing. Use readyWhen=http for a web app and a verified loopback URL, process for an interactive native app (launch survival proves only that it started), or exit for a finite utility whose zero exit code proves completion. Omit url for process/exit. Working directories must remain inside this project. Never store secrets or authentication codes here. Prefer the project's existing runtime and ensure a command works on the current computer. Do not claim Run app is ready until you have actually verified it. Future launch-definition or dependency changes require fresh verification by the host.
        """;
}
