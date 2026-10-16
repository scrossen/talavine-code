using System.ComponentModel;
using SwarmCore.Agent.Catalog;

namespace SwarmCore.Missions;

/// <summary>
/// Primitive: read a text file from the sandbox.
/// </summary>
public sealed class ReadFileTool
{
    public const string ToolId = "fs.read_file";

    public sealed class Input
    {
        [Description("File path. Relative paths use the worker workspace; a user-authorized objective may name an absolute path.")]
        public required string Path { get; set; }

        [Description("Optional max number of bytes to read. Defaults to 256 KB to keep tool results reasonable.")]
        public int? MaxBytes { get; set; }
    }

    public sealed class Output
    {
        public required string Path { get; init; }
        public required long ByteCount { get; init; }
        public required bool Truncated { get; init; }
        public required string Content { get; init; }
        [Description("SHA256 of the complete file bytes. Pass this as expectedSha256 to edits/writes to reject stale changes.")]
        public required string Sha256 { get; init; }
    }

    private readonly ISandboxPathResolver _sandbox;

    public ReadFileTool(ISandboxPathResolver sandbox)
    {
        _sandbox = sandbox;
    }

    public ToolDescriptor Descriptor => ToolDescriptor.Create<Input, Output>(
        toolId: ToolId,
        enforcesMissionResourceBoundary: true,
        ownerAgentId: "primitive",
        description: "Read a text file. Relative paths use the worker workspace; direct or durable boss-authorized work may use explicit absolute paths. Returns content (truncated to MaxBytes).",
        cost: ToolCostClass.Cheap,
        sideEffect: ToolSideEffect.ReadOnly,
        keywords: ["file", "read", "open", "cat"],
        invoke: async (input, ct) =>
        {
            var abs = _sandbox.Resolve(input.Path);
            await MissionPrimitiveFence.ValidateAsync(ct);
            var info = new FileInfo(abs);
            if (!info.Exists)
            {
                throw new FileNotFoundException($"File not found: {input.Path}");
            }
            var limit = input.MaxBytes ?? 256 * 1024;
            if (limit < 4 || limit > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(input.MaxBytes), "MaxBytes must be between 4 bytes and 16 MB.");
            await using var stream = new FileStream(abs, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            var length = stream.Length;
            var sha256 = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct));
            stream.Position = 0;
            var bytes = new byte[(int)Math.Min(length, limit)];
            await stream.ReadExactlyAsync(bytes, ct);
            var snapshot = AtomicTextFile.Decode(bytes, truncated: length > limit);
            TextFileObservations.Observe(abs, sha256);
            return new Output
            {
                Path = input.Path,
                ByteCount = length,
                Truncated = length > limit,
                Content = snapshot.Content,
                Sha256 = sha256
            };
        });
}
