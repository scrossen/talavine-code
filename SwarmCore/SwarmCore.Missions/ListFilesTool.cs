using System.ComponentModel;
using SwarmCore.Agent.Catalog;

namespace SwarmCore.Missions;

/// <summary>
/// Primitive: list files in a sandbox directory.
/// </summary>
public sealed class ListFilesTool
{
    public const string ToolId = "fs.list_files";

    public sealed class Input
    {
        [Description("Directory to list. Relative paths use the worker workspace; a user-authorized objective may name an absolute path. Default '.'.")]
        public string? Path { get; set; }

        [Description("Glob pattern, e.g. '*.txt'. Default '*'.")]
        public string? Pattern { get; set; }

        [Description("Search subdirectories recursively.")]
        public bool Recursive { get; set; } = false;
    }

    public sealed class Entry
    {
        public required string Path { get; init; }
        public required long SizeBytes { get; init; }
        public required bool IsDirectory { get; init; }
    }

    public sealed class Output
    {
        public required string Root { get; init; }
        public required List<Entry> Entries { get; init; }
    }

    private readonly ISandboxPathResolver _sandbox;

    public ListFilesTool(ISandboxPathResolver sandbox) { _sandbox = sandbox; }

    public ToolDescriptor Descriptor => ToolDescriptor.Create<Input, Output>(
        toolId: ToolId,
        enforcesMissionResourceBoundary: true,
        ownerAgentId: "primitive",
        description: "List files in a directory. Relative paths use the worker workspace; direct or durable boss-authorized work may use explicit absolute paths. Supports glob and recursion.",
        cost: ToolCostClass.Cheap,
        sideEffect: ToolSideEffect.ReadOnly,
        keywords: ["list", "dir", "ls", "files", "browse"],
        invoke: async (input, ct) =>
        {
            var rel = input.Path ?? ".";
            var abs = _sandbox.Resolve(rel);
            await MissionPrimitiveFence.ValidateAsync(ct);
            var pattern = string.IsNullOrWhiteSpace(input.Pattern) ? "*" : input.Pattern;
            var entries = new List<Entry>();
            if (Directory.Exists(abs))
            {
                foreach (var file in Directory.EnumerateFileSystemEntries(abs, pattern, new EnumerationOptions
                {
                    RecurseSubdirectories = input.Recursive,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    IgnoreInaccessible = false,
                }))
                {
                    var isDir = Directory.Exists(file);
                    var size = isDir ? 0 : new FileInfo(file).Length;
                    entries.Add(new Entry
                    {
                        Path = Path.IsPathFullyQualified(rel)
                            ? file
                            : Path.GetRelativePath(_sandbox.Root, file).Replace('\\', '/'),
                        SizeBytes = size,
                        IsDirectory = isDir
                    });
                }
            }
            return new Output { Root = rel, Entries = entries };
        });
}
