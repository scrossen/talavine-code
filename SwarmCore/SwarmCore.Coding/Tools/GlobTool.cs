using System.ComponentModel;
using SwarmCore.Agent.Catalog;
using SwarmCore.Missions;

namespace SwarmCore.Coding.Tools;

/// <summary>Find files by glob pattern.</summary>
public sealed class GlobTool
{
    public const string ToolId = "code.glob";

    public sealed class Input
    {
        [Description("Glob pattern relative to Path, supporting **, * and ?. Examples: \"**/*.csproj\", \"SwarmCore/**/Tool*.cs\".")]
        public required string Pattern { get; set; }

        [Description("Optional root directory. Defaults to the repository root.")]
        public string? Path { get; set; }

        [Description("Maximum files to return. Default 500, max 5000.")]
        public int? MaxResults { get; set; }
    }

    public sealed class Output
    {
        [Description("Paths relative to the repository root with forward slashes, newest first.")]
        public required List<string> Files { get; init; }
        public required int Total { get; init; }
        public required bool Truncated { get; init; }
    }

    private readonly ISandboxPathResolver _sandbox;

    public GlobTool(ISandboxPathResolver sandbox) { _sandbox = sandbox; }

    public ToolDescriptor Descriptor => ToolDescriptor.Create<Input, Output>(
        toolId: ToolId,
        enforcesMissionResourceBoundary: true,
        ownerAgentId: "coding",
        description: "Find files by glob pattern (supports **, *, ?). Returns repository-relative paths sorted by most recently modified. Skips .git, bin, obj, node_modules and similar directories.",
        cost: ToolCostClass.Cheap,
        sideEffect: ToolSideEffect.ReadOnly,
        keywords: ["glob", "find", "files", "list", "path", "code"],
        invoke: async (input, ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Pattern)) throw new ArgumentException("Pattern must not be empty.", nameof(input));
            var start = _sandbox.Resolve(string.IsNullOrWhiteSpace(input.Path) ? "." : input.Path, SwarmCore.Agent.Continual.MissionEffect.Inspect);
            await MissionPrimitiveFence.ValidateAsync(ct);
            var max = Math.Clamp(input.MaxResults ?? 500, 1, 5000);
            var regex = RepoWalk.GlobToRegex(input.Pattern);
            var root = _sandbox.Root;

            var found = await Task.Run(() =>
            {
                var list = new List<(string Rel, DateTime Written)>();
                foreach (var file in RepoWalk.EnumerateFiles(start))
                {
                    ct.ThrowIfCancellationRequested();
                    // Patterns are relative to the search root.
                    var rel = RepoWalk.Relative(start, file);
                    if (!regex.IsMatch(rel)) continue;
                    DateTime written;
                    try { written = File.GetLastWriteTimeUtc(file); } catch { continue; }
                    list.Add((RepoWalk.Relative(root, file), written));
                }
                return list;
            }, ct);

            var sorted = found.OrderByDescending(f => f.Written).ThenBy(f => f.Rel, StringComparer.Ordinal).ToList();
            return new Output
            {
                Files = sorted.Take(max).Select(f => f.Rel).ToList(),
                Total = sorted.Count,
                Truncated = sorted.Count > max,
            };
        });
}
