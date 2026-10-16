using System.ComponentModel;
using System.Text.Json.Serialization;
using SwarmCore.Agent.Catalog;
using SwarmCore.Missions;

namespace SwarmCore.Coding.Tools;

/// <summary>Stage and commit locally. Never pushes.</summary>
public sealed class GitCommitTool
{
    public const string ToolId = "git.commit";

    public sealed class Input
    {
        [Description("Commit message.")]
        public required string Message { get; set; }

        [Description("Required explicit individual file paths to stage and commit. Directories and implicit stage-all are rejected. Unrelated staged changes remain staged and are not committed.")]
        public List<string>? Paths { get; set; }

        [Description("Optional repository directory. Defaults to the repository root.")]
        public string? Path { get; set; }

        [Description("Allow a commit with no changes.")]
        public bool AllowEmpty { get; set; }
    }

    public sealed class Output : IToolExecutionEvidence
    {
        public string? CommitSha { get; init; }
        public string? Summary { get; init; }
        public string? Error { get; init; }
        [JsonIgnore]
        public bool Succeeded => Error is null && CommitSha is not null;
        [JsonIgnore]
        public bool LocalProcessCompleted { get; init; }
    }

    private readonly ISandboxPathResolver _sandbox;

    public GitCommitTool(ISandboxPathResolver sandbox) { _sandbox = sandbox; }

    public ToolDescriptor Descriptor => ToolDescriptor.Create<Input, Output>(
        toolId: ToolId,
        enforcesMissionResourceBoundary: true,
        ownerAgentId: "coding",
        description: "Stage explicit individual files and create a local git commit containing only those paths. Never pushes, never stages everything, and preserves unrelated staged and unstaged changes. Paths must be named; directories are not accepted.",
        cost: ToolCostClass.Cheap,
        sideEffect: ToolSideEffect.WritesLocal,
        keywords: ["git", "commit", "stage", "save", "checkpoint"],
        invoke: async (input, ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.Message)) throw new ArgumentException("Message must not be empty.", nameof(input));
            var dir = string.IsNullOrWhiteSpace(input.Path)
                ? _sandbox.Resolve(".", SwarmCore.Agent.Continual.MissionEffect.Modify)
                : _sandbox.Resolve(input.Path, SwarmCore.Agent.Continual.MissionEffect.Modify);
            await MissionPrimitiveFence.ValidateAsync(ct);

            var paths = input.Paths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? [];
            if (paths.Count == 0) return new Output { Error = "Specify the individual file paths to commit; implicit staging of all changes is disabled." };
            foreach (var path in paths)
            {
                var absolute = System.IO.Path.GetFullPath(path, dir);
                var relative = System.IO.Path.GetRelativePath(dir, absolute);
                if (relative == ".." || relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal) || System.IO.Path.IsPathRooted(relative))
                    return new Output { Error = "Commit paths must be files inside the selected repository directory." };
                _sandbox.Resolve(absolute, SwarmCore.Agent.Continual.MissionEffect.Modify);
                if (Directory.Exists(absolute)) return new Output { Error = "Commit paths must name individual files, not directories." };
            }
            var nativeCommit = Routing.NativeCommitScope.Current;
            if (nativeCommit is not null && await nativeCommit.ValidateAsync(input, dir, ct) is { } refusal)
                return new Output { Error = refusal };
            Routing.NativeCommitFiles? prepared = null;
            try
            {
                if (nativeCommit is not null) prepared = await nativeCommit.PrepareFilesAsync(paths, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return new Output { Error = "The native commit snapshot could not be verified; nothing was staged: " + ex.Message };
            }
            using var immutableFiles = prepared;
            if (immutableFiles is not null && await nativeCommit!.ValidateRepositoryAsync(ct) is { } repositoryChanged)
                return new Output { Error = repositoryChanged };
            if (immutableFiles is not null) paths = immutableFiles.Paths;
            string[] addArgs = ["--literal-pathspecs", "add", "--", .. paths];
            if (immutableFiles is not null) addArgs = immutableFiles.Arguments(addArgs);
            var add = await GitProcess.RunAsync(dir, addArgs, TimeSpan.FromSeconds(60), ct);
            if (add.ExitCode != 0)
                return new Output { Error = GitProcess.ErrorText(add.ExitCode, add.Stdout, add.Stderr), LocalProcessCompleted = add.ExitCode != -1 };

            var commitArgs = new List<string> { "--literal-pathspecs", "commit", "--only", "-m", input.Message };
            if (input.AllowEmpty) commitArgs.Add("--allow-empty");
            commitArgs.Add("--");
            commitArgs.AddRange(paths);
            var commit = await GitProcess.RunAsync(dir, immutableFiles?.Arguments(commitArgs) ?? [.. commitArgs], TimeSpan.FromSeconds(60), ct);
            if (commit.ExitCode != 0)
                return new Output { Error = GitProcess.ErrorText(commit.ExitCode, commit.Stdout, commit.Stderr), LocalProcessCompleted = commit.ExitCode != -1 };

            var sha = await GitProcess.RunAsync(dir, ["rev-parse", "HEAD"], TimeSpan.FromSeconds(30), ct);
            var show = await GitProcess.RunAsync(dir, ["show", "--no-textconv", "--no-ext-diff", "--stat", "--oneline", "-1"], TimeSpan.FromSeconds(30), ct);
            return new Output
            {
                CommitSha = sha.ExitCode == 0 ? sha.Stdout.Trim() : null,
                Summary = show.ExitCode == 0 ? GitProcess.FirstLine(show.Stdout) : null,
                Error = sha.ExitCode == 0 ? null : GitProcess.ErrorText(sha.ExitCode, sha.Stdout, sha.Stderr),
                LocalProcessCompleted = true,
            };
        });
}
