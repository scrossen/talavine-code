using System.Diagnostics;
using System.Text;

namespace SwarmCore.Agent.Git;

/// <summary>A repository's git configuration cannot be fenced safely, so the git call is refused.</summary>
public sealed class GitConfigurationRefusedException(string message) : InvalidOperationException(message);

/// <summary>
/// The one place a host-owned git invocation is built (the coding harness tools, <c>CodingTaskService</c> and
/// <c>CodingWorkspaceService</c>): the resolved git (<see cref="GitExecutable"/>), <c>--no-pager</c>,
/// <see cref="HardeningConfig"/>, the repository's own filter and diff drivers blanked
/// (<see cref="RepositoryDriverOverrides"/>), and a child environment with no inherited git redirection
/// (<see cref="ScrubEnvironment"/>).
/// <para>
/// What this guarantees: the files a coding turn can write (the work tree, including any file a repository config
/// reaches through <c>include.path</c>/<c>includeIf</c>) cannot make these git calls run a command. Hooks (pointed at
/// <see cref="NoHooksDirectory"/>, a host-owned empty directory: an empty <c>core.hooksPath</c> resolves to the drive
/// root on Git for Windows), <c>core.fsmonitor</c>, commit/tag signing and signature display (<c>gpg.program</c>),
/// credential helpers, submodule recursion (so a submodule's own config and drivers are never consulted) and every
/// transport are off on the command line, which wins over any config file; git-lfs checkout downloads are skipped
/// (<c>GIT_LFS_SKIP_SMUDGE=1</c>); filter (<c>clean</c>,
/// <c>smudge</c>, <c>process</c>) and diff (<c>textconv</c>, <c>command</c>) drivers defined by the repository
/// (local or worktree scope, which includes every file those include) or by any config file inside the work tree
/// are blanked per call; <c>core.attributesFile</c> and <c>core.excludesFile</c> are blanked, so a work-tree file
/// cannot become an attributes source. What it does not cover: drivers from the user's own global/system config
/// files outside the work tree (for example git-lfs) still run, and a coding turn's ability to write
/// <c>.git/</c>, <c>.gitattributes</c> and <c>.gitmodules</c> is fenced separately by the sandbox.
/// </para>
/// </summary>
public static class GitInvocation
{
    private static readonly Lazy<string> NoHooks = new(() =>
        Directory.CreateTempSubdirectory("swarmcore-git-no-hooks-").FullName.Replace('\\', '/'));

    /// <summary>
    /// A host-owned empty directory, created once per process, that every host-owned git call uses as
    /// <c>core.hooksPath</c>. An empty value is not "no hooks" on every git: Git for Windows 2.47 resolves
    /// <c>core.hooksPath=</c> to the drive root (<c>/pre-commit</c>), so a program planted there ran from a commit.
    /// </summary>
    public static string NoHooksDirectory => NoHooks.Value;

    private static readonly Lazy<IReadOnlyList<string>> Hardening = new(() =>
    [
        "core.hooksPath=" + NoHooksDirectory,
        "core.fsmonitor=false",
        "safe.bareRepository=explicit",
        "credential.helper=",
        "protocol.allow=never",
        "commit.gpgSign=false",
        "tag.gpgSign=false",
        "log.showSignature=false",
        "core.attributesFile=",
        "core.excludesFile=",
        // Never recurse into submodules: a submodule's own config (.git/modules/<sub>/config) can include a work-tree
        // file and define drivers the per-repository listing does not see. Status and diff also get
        // --ignore-submodules=all (see Arguments).
        "diff.ignoreSubmodules=all",
        "status.submoduleSummary=false",
        "submodule.recurse=false",
    ]);

    /// <summary>Command-line configuration every host-owned git call carries (command-line <c>-c</c> wins over config files).</summary>
    public static IReadOnlyList<string> HardeningConfig => Hardening.Value;

    // Commands that would otherwise inspect submodule work trees (running git inside each submodule).
    private static readonly HashSet<string> SubmoduleInspectingCommands = new(StringComparer.Ordinal) { "status", "diff" };

    /// <summary>Config keys that name a command git runs on file content: filter and diff drivers.</summary>
    public const string RepositoryDriverPattern = @"^(filter\..+\.(clean|smudge|process|required)|diff\..+\.(textconv|command))$";

    /// <summary>Arguments (after the git executable) that list every driver key with its scope and origin, NUL-separated.</summary>
    public static IReadOnlyList<string> RepositoryDriverQuery { get; } =
        ["config", "--show-scope", "--show-origin", "--name-only", "-z", "--get-regexp", RepositoryDriverPattern];

    /// <summary>Environment variables never inherited by a host-owned git: they redirect the repository, index or config.</summary>
    public static IReadOnlyList<string> ScrubbedEnvironment { get; } =
    [
        "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES",
        "GIT_NAMESPACE", "GIT_EXTERNAL_DIFF", "GIT_DIFF_OPTS", "GIT_PAGER", "GIT_EDITOR", "GIT_SEQUENCE_EDITOR", "GIT_ASKPASS",
        "GIT_SSH", "GIT_SSH_COMMAND", "GIT_PROXY_COMMAND", "GIT_EXEC_PATH", "GIT_TEMPLATE_DIR",
    ];

    /// <summary>Prefix of every inherited config-injection variable (<c>GIT_CONFIG</c>, <c>GIT_CONFIG_PARAMETERS</c>, <c>GIT_CONFIG_COUNT</c>, <c>GIT_CONFIG_KEY_n</c>, <c>GIT_CONFIG_GLOBAL</c>, ...).</summary>
    public const string ScrubbedEnvironmentPrefix = "GIT_CONFIG";

    // Commands that never run a filter or diff driver, so they need no driver query first.
    private static readonly HashSet<string> DriverFreeCommands = new(StringComparer.Ordinal)
    {
        "rev-parse", "config", "symbolic-ref", "ls-tree", "ls-files", "branch", "read-tree", "var", "version", "--version",
    };

    /// <summary>The arguments after the git executable: <c>--no-pager</c>, <see cref="HardeningConfig"/>, the driver overrides, then <paramref name="args"/>.</summary>
    public static IReadOnlyList<string> Arguments(IEnumerable<string> args, IReadOnlyList<string>? driverOverrides = null)
    {
        var list = new List<string> { "--no-pager" };
        foreach (var setting in HardeningConfig.Concat(driverOverrides ?? []))
        {
            list.Add("-c");
            list.Add(setting);
        }
        var arguments = args.ToList();
        if (VerbIndex(arguments) is { } verb && SubmoduleInspectingCommands.Contains(arguments[verb])
            && !arguments.Any(a => a.StartsWith("--ignore-submodules", StringComparison.Ordinal)))
        {
            // After the command's own options, before any "--" pathspec separator.
            var separator = arguments.IndexOf("--", verb + 1);
            arguments.Insert(separator < 0 ? arguments.Count : separator, "--ignore-submodules=all");
        }
        list.AddRange(arguments);
        return list;
    }

    /// <summary>Index of the git subcommand in <paramref name="args"/> (after global options), or null.</summary>
    private static int? VerbIndex(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a is "-c" or "-C") { i++; continue; }
            if (a.StartsWith('-') && a != "--version") continue;
            return i;
        }
        return null;
    }

    /// <summary>The resolved git with <see cref="Arguments"/> and a scrubbed environment; stdin redirected (close it), no terminal prompt.</summary>
    public static ProcessStartInfo CreateStartInfo(string workingDirectory, IEnumerable<string> args, IReadOnlyList<string>? driverOverrides = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = GitExecutable.Resolve(),
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in Arguments(args, driverOverrides)) psi.ArgumentList.Add(argument);
        ScrubEnvironment(psi.Environment);
        return psi;
    }

    /// <summary>Removes <see cref="ScrubbedEnvironment"/> and every <see cref="ScrubbedEnvironmentPrefix"/> variable, and disables terminal prompts.</summary>
    public static void ScrubEnvironment(IDictionary<string, string?> environment)
    {
        foreach (var change in EnvironmentChanges(environment.Keys))
        {
            if (change.Value is null) environment.Remove(change.Key);
            else environment[change.Key] = change.Value;
        }
    }

    /// <summary>The changes <see cref="ScrubEnvironment"/> makes to an environment holding <paramref name="inheritedNames"/> (a null value removes the variable).</summary>
    public static IReadOnlyDictionary<string, string?> EnvironmentChanges(IEnumerable<string>? inheritedNames = null)
    {
        var names = inheritedNames ?? System.Environment.GetEnvironmentVariables().Keys.Cast<object>().Select(k => k.ToString() ?? "");
        var changes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names.ToList())
        {
            if (ScrubbedEnvironment.Contains(name, StringComparer.OrdinalIgnoreCase)
                || name.StartsWith(ScrubbedEnvironmentPrefix, StringComparison.OrdinalIgnoreCase))
                changes[name] = null;
        }
        changes["GIT_TERMINAL_PROMPT"] = "0";
        // git-lfs downloads through its own HTTP client during a checkout smudge, which protocol.allow does not
        // govern: host checkouts keep LFS pointers instead (git-lfs's documented switch).
        changes["GIT_LFS_SKIP_SMUDGE"] = "1";
        return changes;
    }

    /// <summary>
    /// Whether <paramref name="args"/> may run a filter or diff driver (status, diff, add, commit, show, log, checkout,
    /// worktree, ...), so the caller must run <see cref="RepositoryDriverQuery"/> first and pass
    /// <see cref="RepositoryDriverOverrides"/>.
    /// </summary>
    public static bool RunsRepositoryDrivers(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a is "-c" or "-C") { i++; continue; }
            if (a.StartsWith('-') && a != "--version") continue;
            if (a == "cat-file") return args.Skip(i + 1).Any(x => x is "--filters" or "--textconv" || x.StartsWith("--path=", StringComparison.Ordinal));
            return !DriverFreeCommands.Contains(a);
        }
        return false;
    }

    /// <summary>
    /// The <c>-c</c> settings that blank the drivers listed in <paramref name="queryOutput"/> (the NUL-separated output of
    /// <see cref="RepositoryDriverQuery"/> run in <paramref name="workingDirectory"/>): every driver from local, worktree,
    /// command or unknown scope (the repository's own config and anything it includes), and every driver from a
    /// global/system file that lies inside the repository's work tree. <c>*.required</c> becomes false, the commands
    /// become empty. Throws <see cref="GitConfigurationRefusedException"/> for a driver key that cannot be overridden
    /// on the command line unambiguously (its name contains '=' or a line break).
    /// </summary>
    public static IReadOnlyList<string> RepositoryDriverOverrides(string queryOutput, string workingDirectory)
    {
        var overrides = new List<string>();
        if (string.IsNullOrEmpty(queryOutput)) return overrides;
        var fields = queryOutput.Split('\0');
        string? workTree = null;
        var workTreeKnown = false;
        for (var i = 0; i + 2 < fields.Length; i += 3)
        {
            var scope = fields[i].Trim('\n', '\r');
            var origin = fields[i + 1];
            var name = fields[i + 2];
            if (name.Length == 0) continue;
            var blank = scope is not ("global" or "system");
            if (!blank)
            {
                if (!workTreeKnown) { workTree = FindWorkTree(workingDirectory); workTreeKnown = true; }
                blank = !origin.StartsWith("file:", StringComparison.Ordinal)
                        || workTree is not null && IsWithin(workTree, Path.GetFullPath(origin["file:".Length..], workingDirectory));
            }
            if (!blank) continue;
            if (name.IndexOfAny(['=', '\n', '\r', '\0']) >= 0)
                throw new GitConfigurationRefusedException(
                    $"The repository's git configuration defines a filter or diff driver whose name cannot be overridden safely ('{name.Replace('\n', ' ')}'), so git was not run.");
            var setting = name + "=" + (name.EndsWith(".required", StringComparison.OrdinalIgnoreCase) ? "false" : "");
            if (!overrides.Contains(setting, StringComparer.Ordinal)) overrides.Add(setting);
        }
        return overrides;
    }

    /// <summary>The nearest directory at or above <paramref name="directory"/> holding a <c>.git</c> entry; null when none.</summary>
    private static string? FindWorkTree(string directory)
    {
        try
        {
            for (var dir = new DirectoryInfo(Path.GetFullPath(directory)); dir is not null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
                    return dir.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return null;
    }

    private static bool IsWithin(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var r = Path.TrimEndingDirectorySeparator(root.Replace('/', Path.DirectorySeparatorChar));
        var p = Path.TrimEndingDirectorySeparator(path.Replace('/', Path.DirectorySeparatorChar));
        return string.Equals(r, p, comparison) || p.StartsWith(r + Path.DirectorySeparatorChar, comparison);
    }
}
