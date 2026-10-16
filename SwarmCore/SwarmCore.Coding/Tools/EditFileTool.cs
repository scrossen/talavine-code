using System.ComponentModel;
using System.Text;
using SwarmCore.Agent.Catalog;
using SwarmCore.Missions;

namespace SwarmCore.Coding.Tools;

/// <summary>Exact-substring file edit that preserves encoding and newline style.</summary>
public sealed class EditFileTool
{
    public const string ToolId = "code.edit_file";
    private const long MaxFileBytes = 4 * 1024 * 1024;

    public sealed class Input
    {
        [Description("File path. Relative paths resolve against the repository root.")]
        public required string Path { get; set; }

        [Description("Exact text to find, including whitespace. Must be unique in the file unless replaceAll is true.")]
        public required string OldText { get; set; }

        [Description("Replacement text.")]
        public required string NewText { get; set; }

        [Description("Replace every occurrence instead of requiring a unique match. Default false.")]
        public bool ReplaceAll { get; set; }

        [Description("SHA256 from the earlier fs.read_file result. Rejects an edit if any file bytes changed since that read.")]
        public string? ExpectedSha256 { get; set; }
    }

    public sealed class Output
    {
        public required string Path { get; init; }
        public required int Replacements { get; init; }
        public required bool Succeeded { get; init; }
        public string? Error { get; init; }
        [Description("Unified-diff-like snippet around the first change ('-' removed, '+' added, ' ' context).")]
        public string? Preview { get; init; }
        public string? Sha256 { get; init; }
    }

    private readonly ISandboxPathResolver _sandbox;

    public EditFileTool(ISandboxPathResolver sandbox) { _sandbox = sandbox; }

    public ToolDescriptor Descriptor => ToolDescriptor.Create<Input, Output>(
        toolId: ToolId,
        enforcesMissionResourceBoundary: true,
        ownerAgentId: "coding",
        description: "Edit a file by replacing an exact text snippet (oldText) with newText. Fails if oldText is not found or is ambiguous (include more surrounding context, or set replaceAll). Preserves the file's newline style. Prefer this over rewriting whole files.",
        cost: ToolCostClass.Cheap,
        sideEffect: ToolSideEffect.WritesLocal,
        keywords: ["edit", "replace", "patch", "modify", "file", "code"],
        invoke: async (input, ct) =>
        {
            if (string.IsNullOrEmpty(input.OldText)) throw new ArgumentException("OldText must not be empty.", nameof(input));
            if (input.OldText == input.NewText) throw new ArgumentException("OldText and NewText are identical.", nameof(input));
            var abs = _sandbox.Resolve(input.Path, SwarmCore.Agent.Continual.MissionEffect.Modify);
            await MissionPrimitiveFence.ValidateAsync(ct);
            var info = new FileInfo(abs);
            if (!info.Exists) return Fail(input.Path, "File not found.");
            if (info.Length > MaxFileBytes) return Fail(input.Path, "File is larger than the 4 MB edit limit.");

            AtomicTextFile.Snapshot snapshot;
            try { snapshot = AtomicTextFile.Decode(await File.ReadAllBytesAsync(abs, ct)); }
            catch (Exception e) when (e is DecoderFallbackException or InvalidDataException)
            { return Fail(input.Path, "Unsupported or binary text encoding; file was left unchanged. " + e.Message); }
            if (input.ExpectedSha256 is not null && !snapshot.Sha256.Equals(input.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                return Fail(input.Path, "File changed since it was read; read the current file and integrate its changes.");
            if (TextFileObservations.ExpectedSha256(abs) is { } observed && !snapshot.Sha256.Equals(observed, StringComparison.OrdinalIgnoreCase))
                return Fail(input.Path, "File changed since it was read in this turn; read the current file and integrate its changes.");
            var context = new SwarmCore.Agent.Runtime.AsyncLocalCurrentTurnContextAccessor().Current;
            var expected = context?.GetMissionWriteBaseAsync is { } getBase ? await getBase(abs, ct) : null;
            if (expected is not null && !snapshot.Sha256.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw AtomicTextFile.Conflict();
            var text = snapshot.Content;

            var oldText = input.OldText;
            var newText = input.NewText;
            // Model-authored text is normally LF; adapt it to a CRLF file.
            if (text.Contains("\r\n") && !oldText.Contains('\r'))
            {
                oldText = oldText.Replace("\n", "\r\n");
                newText = newText.Replace("\r\n", "\n").Replace("\n", "\r\n");
            }

            var count = 0;
            var first = -1;
            for (var i = text.IndexOf(oldText, StringComparison.Ordinal); i >= 0;
                 i = text.IndexOf(oldText, i + oldText.Length, StringComparison.Ordinal))
            {
                if (first < 0) first = i;
                count++;
            }
            if (count == 0)
                return Fail(input.Path, "old_text not found; it must match exactly including whitespace" + Hint(text, input.OldText));
            if (count > 1 && !input.ReplaceAll)
                return Fail(input.Path, $"old_text matched {count} times; include more surrounding context or set replaceAll");

            var updated = input.ReplaceAll
                ? text.Replace(oldText, newText, StringComparison.Ordinal)
                : string.Concat(text.AsSpan(0, first), newText, text.AsSpan(first + oldText.Length));
            var preview = BuildPreview(text, first, oldText, newText);

            var output = snapshot.Encode(updated);
            await AtomicTextFile.WriteAsync(abs, output, snapshot.Sha256, ct);
            var writtenSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(output));
            TextFileObservations.Observe(abs, writtenSha256);
            return new Output
            {
                Path = input.Path,
                Replacements = input.ReplaceAll ? count : 1,
                Succeeded = true,
                Preview = preview,
                Sha256 = writtenSha256,
            };
        });

    private static Output Fail(string path, string error) =>
        new() { Path = path, Replacements = 0, Succeeded = false, Error = error };

    private static string Hint(string text, string oldText)
    {
        var firstLine = oldText.Split('\n')[0].Trim();
        if (firstLine.Length == 0) return "";
        var probe = firstLine.Length > 20 ? firstLine[..20] : firstLine;
        var hits = new List<string>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length && hits.Count < 3; i++)
        {
            if (!lines[i].Contains(probe, StringComparison.Ordinal)) continue;
            var l = lines[i].TrimEnd('\r');
            hits.Add($"{i + 1}: {(l.Length > 200 ? l[..200] : l)}");
        }
        return hits.Count == 0 ? "" : ". Nearby lines sharing the start of old_text:\n" + string.Join("\n", hits);
    }

    private static string BuildPreview(string text, int index, string oldText, string newText)
    {
        const int context = 3, cap = 40;
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var startLine = text.AsSpan(0, index).Count('\n');
        var oldLineCount = oldText.Count(c => c == '\n') + 1;
        var lineBegin = index == 0 ? 0 : text.LastIndexOf('\n', index - 1) + 1;
        var prefix = text[lineBegin..index];
        var endIdx = index + oldText.Length;
        var nl = text.IndexOf('\n', endIdx);
        var suffix = text[endIdx..(nl < 0 ? text.Length : nl)].TrimEnd('\r');

        var preview = new List<string>();
        for (var i = Math.Max(0, startLine - context); i < startLine; i++) preview.Add(" " + lines[i]);
        var oldBlock = (prefix + oldText.Replace("\r\n", "\n") + suffix).Split('\n');
        var newBlock = (prefix + newText.Replace("\r\n", "\n") + suffix).Split('\n');
        foreach (var l in oldBlock) preview.Add("-" + l);
        foreach (var l in newBlock) preview.Add("+" + l);
        var after = startLine + oldLineCount;
        for (var i = after; i < Math.Min(lines.Length, after + context); i++) preview.Add(" " + lines[i]);
        if (preview.Count > cap)
        {
            var extra = preview.Count - cap;
            preview = preview.Take(cap).ToList();
            preview.Add($"... ({extra} more lines)");
        }
        return string.Join("\n", preview);
    }
}
