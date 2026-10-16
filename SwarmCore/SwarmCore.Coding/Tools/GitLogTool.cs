using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Serialization;
using SwarmCore.Agent.Catalog;
using SwarmCore.Missions;

namespace SwarmCore.Coding.Tools;

/// <summary>Read-only git commit history.</summary>
public sealed class GitLogTool
{
    public const string ToolId = "git.log";
    public const int DefaultMaxCount = 20;
    public const int MaxMaxCount = 200;

    private const char FieldSeparator = '\u001f';

    public sealed class Input
    {
        [Description("Optional repository directory. Defaults to the repository root.")]
        public string? Path { get; set; }

        [Description("Maximum number of commits to return. Default 20, max 200.")]
        public int? MaxCount { get; set; }

        [Description("Optional revision or range, e.g. \"HEAD~5..HEAD\" or \"main\".")]
        public string? Ref { get; set; }

        [Description("Optional pathspecs; only commits touching these paths are listed.")]
        public List<string>? Paths { get; set; }

        [Description("Optional author filter (git log --author), matched as a pattern against name/email.")]
        public string? Author { get; set; }
    }

    public sealed class Entry
    {
        [Description("Full 40-character commit sha.")]
        public required string Sha { get; init; }
        public required string ShortSha { get; init; }
        public required string AuthorName { get; init; }
        [Description("Author date in ISO 8601 (strict) format.")]
        public required string Date { get; init; }
        public required string Subject { get; init; }
    }

    public sealed class Output : IToolExecutionEvidence
    {
        [Description("Commits, newest first.")]
        public List<Entry> Entries { get; init; } = [];
        public int Total { get; init; }
        public string? Error { get; init; }
        [JsonIgnore]
        public bool Succeeded => Error is null;
        [JsonIgnore]
        public bool LocalProcessCompleted { get; init; }
    }

    private readonly ISandboxPathResolver _sandbox;

    public GitLogTool(ISandboxPathResolver sandbox) { _sandbox = sandbox; }

    public ToolDescriptor Descriptor => ToolDescriptor.Create<Input, Output>(
        toolId: ToolId,
        enforcesMissionResourceBoundary: true,
        ownerAgentId: "coding",
        description: "List recent git commits (newest first) with sha, author, date and subject. Optionally limit by revision range, paths, or author.",
        cost: ToolCostClass.Cheap,
        sideEffect: ToolSideEffect.ReadOnly,
        keywords: ["git", "log", "history", "commits", "repository"],
        invoke: async (input, ct) =>
        {
            var dir = _sandbox.Resolve(string.IsNullOrWhiteSpace(input.Path) ? "." : input.Path, SwarmCore.Agent.Continual.MissionEffect.Inspect);
            await MissionPrimitiveFence.ValidateAsync(ct);
            var max = Math.Clamp(input.MaxCount ?? DefaultMaxCount, 1, MaxMaxCount);
            var args = new List<string>
            {
                "log", "--no-color", "--date=iso-strict",
                "--format=%H%x1f%h%x1f%an%x1f%ad%x1f%s",
                "-n", max.ToString(CultureInfo.InvariantCulture),
            };
            if (!string.IsNullOrWhiteSpace(input.Author)) args.Add("--author=" + input.Author);
            if (!string.IsNullOrWhiteSpace(input.Ref))
            {
                if (input.Ref.StartsWith('-'))
                    return new Output { Error = "Ref must not start with '-'." };
                args.Add(input.Ref);
            }
            if (input.Paths is { Count: > 0 })
            {
                args.Add("--");
                args.AddRange(input.Paths.Where(p => !string.IsNullOrWhiteSpace(p)));
            }
            var (exit, stdout, stderr) = await GitProcess.RunAsync(dir, [.. args], TimeSpan.FromSeconds(60), ct);
            if (exit != 0)
                return new Output { Error = GitProcess.ErrorText(exit, stdout, stderr), LocalProcessCompleted = exit != -1 };
            return Parse(stdout);
        });

    internal static Output Parse(string stdout)
    {
        var entries = new List<Entry>();
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var f = line.Split(FieldSeparator);
            if (f.Length < 5) continue;
            entries.Add(new Entry
            {
                Sha = f[0],
                ShortSha = f[1],
                AuthorName = f[2],
                Date = f[3],
                // A subject cannot normally contain the separator, but rejoin defensively.
                Subject = string.Join(FieldSeparator, f.Skip(4)),
            });
        }
        return new Output { Entries = entries, Total = entries.Count, LocalProcessCompleted = true };
    }
}
