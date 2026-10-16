using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Missions;

namespace SwarmCore.Coding.Tools;

/// <summary>Regex search across repository files.</summary>
public sealed class GrepTool
{
    public const string ToolId = "code.grep";
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    public sealed class Input
    {
        [Description("Pattern to search for (.NET regular expression, or a literal when fixedString is true).")]
        public required string Pattern { get; set; }

        [Description("Optional directory or file to search. Defaults to the repository root.")]
        public string? Path { get; set; }

        [Description("Optional file wildcard such as \"*.cs\" (matches file names anywhere) or \"src/**/*.razor\" (matches the relative path).")]
        public string? Glob { get; set; }

        public bool CaseInsensitive { get; set; }

        [Description("Maximum matches to return. Default 200, max 2000.")]
        public int? MaxResults { get; set; }

        [Description("Lines of context before and after each match, 0-5. Default 0.")]
        public int? ContextLines { get; set; }

        [Description("Treat Pattern as a literal string instead of a regex.")]
        public bool FixedString { get; set; }
    }

    public sealed class Match
    {
        public required string Path { get; init; }
        public required int Line { get; init; }
        public required string Text { get; init; }
        public required string[] Before { get; init; }
        public required string[] After { get; init; }
    }

    public sealed class Output
    {
        public required List<Match> Matches { get; init; }
        public required int TotalMatches { get; init; }
        public required bool Truncated { get; init; }
        public required int FilesScanned { get; init; }
    }

    private readonly ISandboxPathResolver _sandbox;

    public GrepTool(ISandboxPathResolver sandbox) { _sandbox = sandbox; }

    public ToolDescriptor Descriptor => ToolDescriptor.Create<Input, Output>(
        toolId: ToolId,
        enforcesMissionResourceBoundary: true,
        ownerAgentId: "coding",
        description: "Search repository files for a regex (or literal) pattern. Returns file path, 1-based line number and line text, with optional context lines. Skips .git, bin, obj, node_modules and binary or very large files.",
        cost: ToolCostClass.Cheap,
        sideEffect: ToolSideEffect.ReadOnly,
        keywords: ["grep", "search", "find", "regex", "code", "text"],
        invoke: async (input, ct) =>
        {
            if (string.IsNullOrEmpty(input.Pattern)) throw new ArgumentException("Pattern must not be empty.", nameof(input));
            var start = _sandbox.Resolve(string.IsNullOrWhiteSpace(input.Path) ? "." : input.Path, SwarmCore.Agent.Continual.MissionEffect.Inspect);
            await MissionPrimitiveFence.ValidateAsync(ct);

            var max = Math.Clamp(input.MaxResults ?? 200, 1, 2000);
            var ctx = Math.Clamp(input.ContextLines ?? 0, 0, 5);
            var options = RegexOptions.CultureInvariant | (input.CaseInsensitive ? RegexOptions.IgnoreCase : RegexOptions.None);
            var regex = new Regex(input.FixedString ? Regex.Escape(input.Pattern) : input.Pattern, options, TimeSpan.FromSeconds(2));
            var glob = string.IsNullOrWhiteSpace(input.Glob) ? null : input.Glob.Replace('\\', '/');
            var globRegex = glob is null ? null : RepoWalk.GlobToRegex(glob);
            var globByName = glob is not null && !glob.Contains('/');
            var root = _sandbox.Root;

            var matches = new List<Match>();
            var total = 0;
            var scanned = 0;
            var truncated = false;
            var clock = Stopwatch.StartNew();

            await Task.Run(() =>
            {
                foreach (var file in RepoWalk.EnumerateFiles(start))
                {
                    ct.ThrowIfCancellationRequested();
                    if (clock.Elapsed > Budget) { truncated = true; break; }
                    var rel = RepoWalk.Relative(root, file);
                    if (globRegex is not null)
                    {
                        var subject = globByName ? System.IO.Path.GetFileName(file) : rel;
                        if (!globRegex.IsMatch(subject)) continue;
                    }
                    long length;
                    try { length = new FileInfo(file).Length; } catch { continue; }
                    if (length > MaxFileBytes || RepoWalk.LooksBinary(file)) continue;
                    string[] lines;
                    // An unreadable file (in use, ACL-protected) is skipped like an unreadable directory, not fatal to the search.
                    try { lines = File.ReadAllLines(file); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                    scanned++;
                    for (var i = 0; i < lines.Length; i++)
                    {
                        bool hit;
                        try { hit = regex.IsMatch(lines[i]); } catch (RegexMatchTimeoutException) { continue; }
                        if (!hit) continue;
                        total++;
                        if (matches.Count >= max) { truncated = true; continue; }
                        matches.Add(new Match
                        {
                            Path = rel,
                            Line = i + 1,
                            Text = Trim(lines[i]),
                            Before = lines[Math.Max(0, i - ctx)..i].Select(Trim).ToArray(),
                            After = lines[(i + 1)..Math.Min(lines.Length, i + 1 + ctx)].Select(Trim).ToArray(),
                        });
                    }
                    if (matches.Count >= max && total > max) break;
                }
            }, ct);

            return new Output { Matches = matches, TotalMatches = total, Truncated = truncated, FilesScanned = scanned };
        });

    private static string Trim(string s)
    {
        s = s.Trim();
        return s.Length <= 400 ? s : s[..400];
    }
}
