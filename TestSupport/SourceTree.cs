using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;

namespace Talavine.TestSupport;

/// <summary>
/// The repository a test assembly was compiled from, for tests that read
/// sources, scripts, docs, or data from the tree rather than from their own
/// output. Every test project compiles this file (see Directory.Build.targets).
/// <para>
/// Walking up from <see cref="AppContext.BaseDirectory"/> finds the tree only
/// while the test output sits inside it. A build with <c>--artifacts-path</c>,
/// the norm in a checkout that several sessions share because in-tree
/// <c>bin</c> folders lock each other, puts the output elsewhere. The compiler
/// records this file's path at build time, so the tree it was built from is
/// found wherever the output goes. The output and working directories remain
/// fallbacks for binaries copied away from their source.
/// </para>
/// </summary>
internal static class SourceTree
{
    private const string RootMarker = "TalavineCode.slnx";

    private static readonly Lazy<string> RootDirectory = new(Locate);

    // What an exported copy's inventory skips: the build and tool output the
    // repository ignores, and the routing gate's copies of the files it
    // mutates, which sit in whatever results folder it was given.
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".build", ".claude", "bin", "obj", "node_modules", "TestResults", "artifacts", "packages", "originals",
    };

    /// <summary>The repository root: the directory holding TalavineCode.slnx.</summary>
    public static string Root => RootDirectory.Value;

    /// <summary>A path inside the repository, which need not exist.</summary>
    public static string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    /// <summary>A repository file that must exist.</summary>
    public static string File(params string[] parts)
    {
        var path = Path(parts);
        return System.IO.File.Exists(path)
            ? path
            : throw new FileNotFoundException($"Repository file was not found: {Relative(parts)}", path);
    }

    /// <summary>A repository directory that must exist.</summary>
    public static string Directory(params string[] parts)
    {
        var path = Path(parts);
        return System.IO.Directory.Exists(path)
            ? path
            : throw new DirectoryNotFoundException($"Repository directory was not found: {Relative(parts)} (under {Root})");
    }

    /// <summary>The text of a repository file that must exist.</summary>
    public static string ReadAllText(params string[] parts) => System.IO.File.ReadAllText(File(parts));

    /// <summary>
    /// The text of a source file with line endings normalized to "\n", so substring guards
    /// that span lines behave the same on CRLF and LF checkouts.
    /// </summary>
    public static string ReadNormalizedText(string path) => System.IO.File.ReadAllText(path).ReplaceLineEndings("\n");

    /// <summary>
    /// The source with C# and Razor comments removed (<c>/* */</c>, <c>@* *@</c>,
    /// <c>&lt;!-- --&gt;</c> and <c>//</c> comments that start a line or follow whitespace), so a
    /// structural guard is satisfied only by code, never by a word that appears in a comment. A
    /// <c>//</c> inside a URL literal such as <c>https://</c> is kept.
    /// </summary>
    public static string StripComments(string source)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(source, @"/\*.*?\*/|@\*.*?\*@|<!--.*?-->", " ",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        return System.Text.RegularExpressions.Regex.Replace(text, @"(^|\s)//.*$", "$1",
            System.Text.RegularExpressions.RegexOptions.Multiline);
    }

    /// <summary>True only when both snippets are present and <paramref name="first"/> starts before <paramref name="second"/>.</summary>
    public static bool AppearsBefore(string text, string first, string second)
    {
        var firstIndex = text.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = text.IndexOf(second, StringComparison.Ordinal);
        return firstIndex >= 0 && secondIndex >= 0 && firstIndex < secondIndex;
    }

    /// <summary>
    /// The repository's files that match any of <paramref name="patterns"/>
    /// (such as "*.cs"), relative to <see cref="Root"/> with '/' separators. A
    /// git checkout lists them with git: tracked and untracked, never ignored.
    /// An exported copy has no .git of its own, and git would answer for the
    /// checkout around it instead (which ignores .build), so its tree is walked,
    /// skipping build and tool output.
    /// </summary>
    public static IReadOnlyList<string> Files(params string[] patterns)
    {
        var root = Root;
        return System.IO.Directory.Exists(System.IO.Path.Combine(root, ".git"))
               || System.IO.File.Exists(System.IO.Path.Combine(root, ".git"))
            ? ListedByGit(root, patterns)
            : Walked(root, patterns);
    }

    private static IReadOnlyList<string> ListedByGit(string root, string[] patterns)
    {
        var start = SwarmCore.Testing.TestGit.StartInfo(root,
            new[] { "ls-files", "-z", "--cached", "--others", "--exclude-standard", "--" }.Concat(patterns));

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start git to list the repository's files.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("git did not list the repository's files within 30 seconds.");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException("git could not list the repository's files: " + errors.GetAwaiter().GetResult());

        return output.GetAwaiter().GetResult()
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<string> Walked(string root, string[] patterns)
    {
        var files = new List<string>();
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var file in System.IO.Directory.EnumerateFiles(directory))
            {
                var name = System.IO.Path.GetFileName(file);
                if (patterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, name)))
                    files.Add(System.IO.Path.GetRelativePath(root, file).Replace('\\', '/'));
            }

            foreach (var child in System.IO.Directory.EnumerateDirectories(directory))
            {
                if (!SkippedDirectories.Contains(System.IO.Path.GetFileName(child)))
                    pending.Push(child);
            }
        }

        return files.Order(StringComparer.Ordinal).ToList();
    }

    private static string Locate()
    {
        var compiled = CompiledDirectory();
        foreach (var start in new[] { compiled, AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            if (string.IsNullOrEmpty(start) || !System.IO.Directory.Exists(start)) continue;
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, RootMarker)))
                    return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"No directory holding {RootMarker} was found above this file's compiled path ({compiled}), "
            + $"the test output ({AppContext.BaseDirectory}), or the working directory ({Environment.CurrentDirectory}).");
    }

    private static string Relative(string[] parts) => string.Join('/', parts).Replace('\\', '/');

    private static string CompiledDirectory([CallerFilePath] string sourceFilePath = "")
        => System.IO.Path.GetDirectoryName(sourceFilePath) ?? string.Empty;
}
