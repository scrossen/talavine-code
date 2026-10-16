using System.ComponentModel;
using SwarmCore.Agent.Catalog;

namespace SwarmCore.Missions;

/// <summary>
/// Primitive: write a text file. Relative paths use the worker workspace;
/// user-authorized objectives can use explicit absolute paths.
/// </summary>
public sealed class WriteFileTool
{
    public const string ToolId = "fs.write_file";

    public sealed class Input
    {
        [Description("File path. Relative paths use the worker workspace; a user-authorized objective may name an absolute path.")]
        public required string Path { get; set; }

        [Description("Text content to write. Existing UTF-8/UTF-16/UTF-32 BOM and encoding are preserved; new files use UTF-8.")]
        public required string Content { get; set; }

        [Description("SHA256 from fs.read_file to reject stale overwrites, or 'missing' to create only. Omit only when no earlier file read exists.")]
        public string? ExpectedSha256 { get; set; }
    }

    public sealed class Output
    {
        public required string Path { get; init; }
        public required long BytesWritten { get; init; }
        public required string Sha256 { get; init; }
    }

    private readonly ISandboxPathResolver _sandbox;

    public WriteFileTool(ISandboxPathResolver sandbox) { _sandbox = sandbox; }

    public ToolDescriptor Descriptor => ToolDescriptor.Create<Input, Output>(
        toolId: ToolId,
        enforcesMissionResourceBoundary: true,
        ownerAgentId: "primitive",
        description: "Atomically write a text file, preserving its encoding and BOM. Supply expectedSha256 from the earlier fs.read_file result to reject changed files. Unsupported/binary encodings fail without changing the target. Relative paths use the worker workspace; authorized work may use explicit absolute paths.",
        cost: ToolCostClass.Cheap,
        sideEffect: ToolSideEffect.WritesLocal,
        keywords: ["file", "write", "save", "create"],
        invoke: async (input, ct) =>
        {
            var abs = _sandbox.Resolve(input.Path, SwarmCore.Agent.Continual.MissionEffect.Modify);
            await MissionPrimitiveFence.ValidateAsync(ct);
            var context = new SwarmCore.Agent.Runtime.AsyncLocalCurrentTurnContextAccessor().Current;
            var expected = context?.GetMissionWriteBaseAsync is { } getBase ? await getBase(abs, ct) : null;
            var snapshot = await AtomicTextFile.ReadAsync(abs, ct);
            var actual = snapshot?.Sha256 ?? "missing";
            if (expected is not null && !actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw AtomicTextFile.Conflict();
            if (input.ExpectedSha256 is not null && !actual.Equals(input.ExpectedSha256, StringComparison.OrdinalIgnoreCase)) throw AtomicTextFile.Conflict();
            if (TextFileObservations.ExpectedSha256(abs) is { } observed && !actual.Equals(observed, StringComparison.OrdinalIgnoreCase)) throw AtomicTextFile.Conflict();
            var bytes = snapshot?.Encode(input.Content) ?? new System.Text.UTF8Encoding(false, true).GetBytes(input.Content);
            await AtomicTextFile.WriteAsync(abs, bytes, actual, ct);
            var writtenSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            TextFileObservations.Observe(abs, writtenSha256);
            return new Output
            {
                Path = input.Path,
                BytesWritten = bytes.Length,
                Sha256 = writtenSha256
            };
        });
}
