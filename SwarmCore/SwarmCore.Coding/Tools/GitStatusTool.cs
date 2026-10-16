using System.ComponentModel;
using System.Text.Json.Serialization;
using SwarmCore.Agent.Catalog;
using SwarmCore.Missions;

namespace SwarmCore.Coding.Tools;

/// <summary>Working-tree status of a git repository.</summary>
public sealed class GitStatusTool
{
    public const string ToolId = "git.status";

    public sealed class Input
    {
        [Description("Optional repository directory. Defaults to the repository root.")]
        public string? Path { get; set; }
    }

    public sealed class Entry
    {
        [Description("Two-character porcelain status code, e.g. \" M\", \"A \", \"??\".")]
        public required string Status { get; init; }
        public required string Path { get; init; }
    }

    public sealed class Output : IToolExecutionEvidence
    {
        public string? Branch { get; init; }
        public string? Upstream { get; init; }
        public int Ahead { get; init; }
        public int Behind { get; init; }
        public List<Entry> Entries { get; init; } = [];
        public bool IsClean { get; init; }
        public string? Error { get; init; }
        [JsonIgnore]
        public bool Succeeded => Error is null;
        [JsonIgnore]
        public bool LocalProcessCompleted { get; init; }
    }

    private readonly ISandboxPathResolver _sandbox;

    public GitStatusTool(ISandboxPathResolver sandbox) { _sandbox = sandbox; }

    public ToolDescriptor Descriptor => ToolDescriptor.Create<Input, Output>(
        toolId: ToolId,
        enforcesMissionResourceBoundary: true,
        ownerAgentId: "coding",
        description: "Show git branch, upstream tracking (ahead/behind) and changed files for the repository (git status --porcelain).",
        cost: ToolCostClass.Cheap,
        sideEffect: ToolSideEffect.ReadOnly,
        keywords: ["git", "status", "changes", "branch", "repository"],
        invoke: async (input, ct) =>
        {
            var dir = _sandbox.Resolve(string.IsNullOrWhiteSpace(input.Path) ? "." : input.Path, SwarmCore.Agent.Continual.MissionEffect.Inspect);
            await MissionPrimitiveFence.ValidateAsync(ct);
            var (exit, stdout, stderr) = await GitProcess.RunAsync(
                dir, ["status", "--porcelain=v1", "-b", "-z"], TimeSpan.FromSeconds(30), ct);
            if (exit != 0)
                return new Output { Error = GitProcess.ErrorText(exit, stdout, stderr), LocalProcessCompleted = exit != -1 };
            return Parse(stdout);
        });

    internal static Output Parse(string stdout)
    {
        string? branch = null, upstream = null;
        int ahead = 0, behind = 0;
        var entries = new List<Entry>();
        var parts = stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.StartsWith("## ", StringComparison.Ordinal))
            {
                var h = p[3..];
                var bracket = h.IndexOf(" [", StringComparison.Ordinal);
                if (bracket >= 0 && h.EndsWith(']'))
                {
                    foreach (var piece in h[(bracket + 2)..^1].Split(',', StringSplitOptions.TrimEntries))
                    {
                        if (piece.StartsWith("ahead ", StringComparison.Ordinal) && int.TryParse(piece[6..], out var a)) ahead = a;
                        else if (piece.StartsWith("behind ", StringComparison.Ordinal) && int.TryParse(piece[7..], out var b)) behind = b;
                    }
                    h = h[..bracket];
                }
                if (h.StartsWith("No commits yet on ", StringComparison.Ordinal)) branch = h["No commits yet on ".Length..];
                else if (h.StartsWith("Initial commit on ", StringComparison.Ordinal)) branch = h["Initial commit on ".Length..];
                else
                {
                    var sep = h.IndexOf("...", StringComparison.Ordinal);
                    if (sep >= 0) { branch = h[..sep]; upstream = h[(sep + 3)..]; }
                    else branch = h;
                }
                continue;
            }
            if (p.Length < 4) continue;
            var status = p[..2];
            entries.Add(new Entry { Status = status, Path = p[3..] });
            // Rename/copy entries carry the original path as the next NUL-separated field.
            if (status[0] is 'R' or 'C' || status[1] is 'R' or 'C') i++;
        }
        return new Output
        {
            Branch = branch,
            Upstream = upstream,
            Ahead = ahead,
            Behind = behind,
            Entries = entries,
            IsClean = entries.Count == 0,
            LocalProcessCompleted = true,
        };
    }
}
