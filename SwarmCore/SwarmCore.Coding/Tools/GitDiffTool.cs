using System.ComponentModel;
using System.Text;
using System.Text.Json.Serialization;
using SwarmCore.Agent.Catalog;
using SwarmCore.Missions;

namespace SwarmCore.Coding.Tools;

/// <summary>Read-only git diff.</summary>
public sealed class GitDiffTool
{
    public const string ToolId = "git.diff";

    public sealed class Input
    {
        [Description("Optional repository directory. Defaults to the repository root.")]
        public string? Path { get; set; }

        [Description("Diff the index against HEAD (git diff --staged).")]
        public bool Staged { get; set; }

        [Description("Optional revision or range, e.g. \"HEAD~1\" or \"main...HEAD\".")]
        public string? Ref { get; set; }

        [Description("Optional pathspecs limiting the diff.")]
        public List<string>? Paths { get; set; }

        [Description("Only show a --stat summary.")]
        public bool Stat { get; set; }

        [Description("Maximum diff bytes to return. Default 65536.")]
        public int? MaxBytes { get; set; }
    }

    public sealed class Output : IToolExecutionEvidence
    {
        public string Diff { get; init; } = "";
        public bool Truncated { get; init; }
        public string? Error { get; init; }
        [JsonIgnore]
        public bool Succeeded => Error is null;
        [JsonIgnore]
        public bool LocalProcessCompleted { get; init; }
    }

    private readonly ISandboxPathResolver _sandbox;

    public GitDiffTool(ISandboxPathResolver sandbox) { _sandbox = sandbox; }

    public ToolDescriptor Descriptor => ToolDescriptor.Create<Input, Output>(
        toolId: ToolId,
        enforcesMissionResourceBoundary: true,
        ownerAgentId: "coding",
        description: "Show a git diff of working-tree, staged, or revision changes. Output is truncated to maxBytes; use stat for a summary only.",
        cost: ToolCostClass.Cheap,
        sideEffect: ToolSideEffect.ReadOnly,
        keywords: ["git", "diff", "changes", "patch", "review"],
        invoke: async (input, ct) =>
        {
            var dir = _sandbox.Resolve(string.IsNullOrWhiteSpace(input.Path) ? "." : input.Path, SwarmCore.Agent.Continual.MissionEffect.Inspect);
            await MissionPrimitiveFence.ValidateAsync(ct);
            var args = new List<string> { "diff", "--no-color", "--no-ext-diff", "--no-textconv" };
            if (input.Staged) args.Add("--staged");
            if (input.Stat) args.Add("--stat");
            if (!string.IsNullOrWhiteSpace(input.Ref))
            {
                if (input.Ref.StartsWith('-')) throw new ArgumentException("Ref must not start with '-'.", nameof(input));
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

            var max = Math.Clamp(input.MaxBytes ?? 64 * 1024, 1, 4 * 1024 * 1024);
            var bytes = Encoding.UTF8.GetBytes(stdout);
            if (bytes.Length <= max) return new Output { Diff = stdout, LocalProcessCompleted = true };
            var cut = Encoding.UTF8.GetString(bytes, 0, max).TrimEnd('�');
            return new Output { Diff = cut, Truncated = true, LocalProcessCompleted = true };
        });
}
