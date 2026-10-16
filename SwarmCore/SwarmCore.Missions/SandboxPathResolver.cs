using Microsoft.Extensions.Options;
using SwarmCore.Agent.Continual;
using SwarmCore.Agent.Runtime;

namespace SwarmCore.Missions;

/// <summary>
/// Options controlling the sandbox root for file-touching primitives.
/// </summary>
public sealed class SandboxOptions
{
    /// <summary>
    /// Absolute default workspace. Relative paths used by primitive tools are
    /// resolved here. A direct user request or durable boss-authorized
    /// continuation may also name an explicit absolute path.
    /// </summary>
    public string Root { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SwarmCore", "missions", "workspace");

    /// <summary>
    /// When a turn runs in a host-selected workspace (<c>CurrentTurnContext.WorkspaceRoot</c>), may an explicit
    /// absolute path outside it be used on user-initiated authority? True keeps SwarmCode's single-repository
    /// policy (a direct request may name any path). A governed host sets false: a workspace turn is fenced to
    /// its workspace whatever authority it carries.
    /// </summary>
    public bool AllowAbsolutePathsOutsideTurnWorkspace { get; set; } = true;

    /// <summary>
    /// May a turn running in a host-selected workspace write git metadata (a <c>.git</c> path component,
    /// <c>.gitmodules</c>, <c>.gitattributes</c>)? Default false in every host: those files configure commands git
    /// runs (hooks, <c>core.fsmonitor</c>, filter/diff/merge drivers, submodule commands), so a file write there
    /// would turn a coding turn's edit tools into command execution, background continuations included. Reading
    /// them is unaffected. A host may set true only for a known direct-request use.
    /// </summary>
    public bool AllowGitMetadataWritesInTurnWorkspace { get; set; }
}

/// <summary>
/// Resolves a user-supplied path against the default workspace. Relative paths
/// remain workspace-local; an explicit absolute path is honored when the
/// active turn carries user-initiated authority, including durable employee
/// continuations.
/// </summary>
public interface ISandboxPathResolver
{
    /// <summary>Absolute sandbox root, ensured to exist.</summary>
    string Root { get; }

    /// <summary>
    /// Convert a user-supplied path to an absolute path. Relative paths stay
    /// inside the workspace. Explicit absolute paths outside it require an
    /// active user-initiated objective.
    /// </summary>
    string Resolve(string userPath);

    string Resolve(string userPath, MissionEffect effect) => Resolve(userPath);
}

/// <remarks>
/// The root is per turn: when the ambient turn carries a host-selected
/// <see cref="CurrentTurnContext.WorkspaceRoot"/> (for example a coding conversation's
/// repository), that directory is the root and the fence for the turn; otherwise the
/// configured <see cref="SandboxOptions.Root"/> applies. One process can therefore serve
/// many conversations with different workspaces without rebuilding the container.
/// </remarks>
public sealed class SandboxPathResolver : ISandboxPathResolver
{
    private const int MaxCachedWorkspaceRoots = 256;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _workspaceRoots =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public SandboxPathResolver(IOptions<SandboxOptions> options)
    {
        _allowOutsideTurnWorkspace = options.Value.AllowAbsolutePathsOutsideTurnWorkspace;
        _allowGitMetadataWrites = options.Value.AllowGitMetadataWritesInTurnWorkspace;
        var root = Path.GetFullPath(options.Value.Root);
        Directory.CreateDirectory(root);
        ConfiguredRoot = ResolvePhysicalPath(root);
    }

    private readonly bool _allowOutsideTurnWorkspace;
    private readonly bool _allowGitMetadataWrites;

    /// <summary>
    /// True when <paramref name="full"/> (a resolved physical path) is git metadata: any <c>.git</c> component
    /// (a repository's or a submodule's git directory, or a worktree's gitdir file), or a <c>.gitmodules</c> or
    /// <c>.gitattributes</c> file. Inside <paramref name="root"/> only the components below it count. Components are
    /// compared ignoring case, trailing dots/spaces and NTFS stream suffixes, and 8.3 short names are expanded, so
    /// <c>.GIT</c>, <c>.git.</c>, <c>.gitattributes::$DATA</c> and <c>GIT~1</c> are the same file to this check.
    /// </summary>
    public static bool IsGitMetadata(string full, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var basePath = full.StartsWith(rootWithSep, comparison) ? rootWithSep : Path.GetPathRoot(full) ?? "";
        var parent = basePath;
        var segments = full[basePath.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            var name = CanonicalSegment(parent, segments[i]);
            var last = i == segments.Length - 1;
            if (name.Equals(".git", StringComparison.OrdinalIgnoreCase)) return true;
            if (last && (name.Equals(".gitmodules", StringComparison.OrdinalIgnoreCase)
                         || name.Equals(".gitattributes", StringComparison.OrdinalIgnoreCase))) return true;
            parent = Path.Combine(parent, segments[i]);
        }
        return false;
    }

    private static string CanonicalSegment(string parent, string segment)
    {
        var name = segment;
        if (OperatingSystem.IsWindows())
        {
            var colon = name.IndexOf(':');
            if (colon >= 0) name = name[..colon];
            name = name.TrimEnd('.', ' ');
            if (name.Contains('~') && Directory.Exists(parent))
            {
                try
                {
                    // Windows matches a search pattern against short names too, so the exact short name finds
                    // the entry and the enumeration reports its long name.
                    var match = Directory.EnumerateFileSystemEntries(parent, name).FirstOrDefault();
                    if (match is not null) name = Path.GetFileName(match);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
        }
        return name;
    }

    /// <summary>The host-configured default root, used outside turns that select a workspace.</summary>
    public string ConfiguredRoot { get; }

    public string Root => AsyncLocalCurrentTurnContextAccessor.Ambient?.WorkspaceRoot is { Length: > 0 } workspace
        ? ResolveWorkspaceRoot(workspace)
        : ConfiguredRoot;

    private string ResolveWorkspaceRoot(string workspace)
    {
        if (_workspaceRoots.TryGetValue(workspace, out var cached)) return cached;
        var full = Path.GetFullPath(workspace);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"The turn's workspace '{full}' does not exist.");
        var physical = ResolvePhysicalPath(full);
        if (_workspaceRoots.Count >= MaxCachedWorkspaceRoots) _workspaceRoots.Clear();
        _workspaceRoots[workspace] = physical;
        return physical;
    }

    public string Resolve(string userPath) => Resolve(userPath, MissionEffect.Inspect);

    public string Resolve(string userPath, MissionEffect effect)
    {
        if (string.IsNullOrWhiteSpace(userPath))
        {
            throw new ArgumentException("Path is empty.", nameof(userPath));
        }

        var root = Root;
        var combined = Path.IsPathRooted(userPath)
            ? userPath
            : Path.Combine(root, userPath);
        var full = ResolvePhysicalPath(combined);
        var turn = AsyncLocalCurrentTurnContextAccessor.Ambient;
        if (!_allowGitMetadataWrites && (effect & (MissionEffect.Modify | MissionEffect.Remove)) != 0
            && !string.IsNullOrWhiteSpace(turn?.WorkspaceRoot) && IsGitMetadata(full, root))
            throw new UnauthorizedAccessException(
                $"Path '{userPath}' is git metadata (.git, .gitmodules or .gitattributes). A coding turn does not write it: " +
                "it configures commands git runs (hooks, fsmonitor, filter and diff drivers). Ask the person to change it.");
        if (turn?.MissionObjectiveId is not null)
        {
            var decision = turn.MissionResources?.Evaluate(new(
                new(Directory.Exists(full) ? MissionResourceKind.Directory : MissionResourceKind.File, full),
                effect, turn.ProfileId?.ToString("D") ?? ""));
            if (decision?.Allowed != true)
                throw new MissionActionSkippedException(decision?.ReasonCode ?? "missing-resource-authority");
            return full;
        }

        // StartsWith must include the terminal separator to avoid '/rootX' matching '/root'.
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var insideWorkspace = full.StartsWith(rootWithSep, comparison)
                              || string.Equals(full, root, comparison);
        var explicitlyAbsolute = Path.IsPathRooted(userPath);
        var turnWorkspace = !string.IsNullOrWhiteSpace(turn?.WorkspaceRoot);
        var objectiveAuthorized = turn is { CommunicationScopeUserInitiated: true }
                                  && (!turnWorkspace || _allowOutsideTurnWorkspace);

        if (!insideWorkspace && (!explicitlyAbsolute || !objectiveAuthorized))
        {
            throw new UnauthorizedAccessException(
                $"Path '{userPath}' is outside the default workspace '{root}'. " +
                (turnWorkspace && !_allowOutsideTurnWorkspace
                    ? "This turn is fenced to its workspace; use a path inside it (relative paths resolve there)."
                    : "Use a relative workspace path, or name an explicit absolute path from a user-authorized objective."));
        }

        return full;
    }

    // Walk every existing component: resolving only the final file misses parent-directory
    // symlinks and Windows junctions. Return the resolved path so the primitive operates on
    // the path that was checked. This is not an OS sandbox or protection from rename races.
    public static string ResolvePhysicalPath(string path, int linkDepth = 0)
    {
        if (linkDepth > 64) throw new UnauthorizedAccessException("Filesystem link chain is cyclic or too deep to resolve.");
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var component in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true)
                    ?? throw new UnauthorizedAccessException("Cannot resolve a filesystem link target.");
                if (!target.Exists) throw new UnauthorizedAccessException("A dangling filesystem link cannot be used as a target.");
                current = ResolvePhysicalPath(target.FullName, linkDepth + 1);
            }
        }
        return current;
    }
}
