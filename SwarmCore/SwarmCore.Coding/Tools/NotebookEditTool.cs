using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCore.Agent.Catalog;
using SwarmCore.Missions;

namespace SwarmCore.Coding.Tools;

/// <summary>
/// Cell-level Jupyter notebook edits (replace source, insert, delete, change type) that leave everything else
/// in the file as it was: metadata, key order, nbformat, indentation, escaping, line endings, trailing newline.
/// </summary>
public sealed class NotebookEditTool
{
    public const string ToolId = "notebook.edit";

    public static readonly IReadOnlyList<string> Operations = ["replace", "insert", "delete", "change_type"];
    private static readonly string[] CellTypes = ["code", "markdown", "raw"];

    public sealed class Input
    {
        [Description("Notebook path (.ipynb). Relative paths resolve against the repository root.")]
        public required string Path { get; set; }

        [Description("replace (cell source), insert (new cell), delete, or change_type. Default replace.")]
        public string? Operation { get; set; }

        [Description("Target cell id (from notebook.read). Preferred over cellIndex.")]
        public string? CellId { get; set; }

        [Description("Target cell index (0-based). For insert without a target the cell is appended.")]
        public int? CellIndex { get; set; }

        [Description("New cell source for replace/insert (optional for change_type). Plain text; stored as Jupyter's list of lines.")]
        public string? Source { get; set; }

        [Description("code, markdown or raw: required for change_type, default code for insert.")]
        public string? CellType { get; set; }

        [Description("For insert: before or after the target cell. Default after.")]
        public string? Position { get; set; }

        [Description("SHA256 from the earlier notebook.read result. Rejects the edit if the file changed since.")]
        public string? ExpectedSha256 { get; set; }
    }

    public sealed class Output
    {
        public required string Path { get; init; }
        public required bool Succeeded { get; init; }
        public string? Error { get; init; }
        public string? Operation { get; init; }
        [Description("Index of the edited/inserted cell (for delete, the removed index).")]
        public int? CellIndex { get; init; }
        public string? CellId { get; init; }
        public string? CellType { get; init; }
        public int CellCount { get; init; }
        [Description("Diff-like snippet of the cell source ('-' removed, '+' added).")]
        public string? Preview { get; init; }
        public string? Sha256 { get; init; }
    }

    private readonly ISandboxPathResolver _sandbox;

    public NotebookEditTool(ISandboxPathResolver sandbox) { _sandbox = sandbox; }

    public ToolDescriptor Descriptor => ToolDescriptor.Create<Input, Output>(
        toolId: ToolId,
        enforcesMissionResourceBoundary: true,
        ownerAgentId: "coding",
        description: "Edit a Jupyter notebook (.ipynb) by cell: operation replace (new source; a code cell's outputs and execution count are cleared), insert (cellType, before/after a cell id or index, or appended), delete, or change_type. Identify cells by id (preferred) or index from notebook.read. Everything else in the file is preserved.",
        cost: ToolCostClass.Cheap,
        sideEffect: ToolSideEffect.WritesLocal,
        keywords: ["notebook", "jupyter", "ipynb", "cell", "edit", "insert", "delete", "code"],
        invoke: async (input, ct) =>
        {
            var operation = (input.Operation ?? "replace").Trim().ToLowerInvariant().Replace('-', '_');
            if (operation == "changetype") operation = "change_type";
            if (!Operations.Contains(operation))
                return Fail(input.Path, $"Unknown operation '{input.Operation}'. Use replace, insert, delete or change_type.");
            var abs = _sandbox.Resolve(input.Path, SwarmCore.Agent.Continual.MissionEffect.Modify);
            await MissionPrimitiveFence.ValidateAsync(ct);
            var loaded = await NotebookFile.LoadAsync(abs, input.Path, ct);
            if (loaded.Error is not null) return Fail(input.Path, loaded.Error);
            var (snapshot, doc) = (loaded.Snapshot!, loaded.Document!);

            if (input.ExpectedSha256 is not null && !snapshot.Sha256.Equals(input.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                return Fail(input.Path, "Notebook changed since it was read; read it again and integrate its changes.");
            if (TextFileObservations.ExpectedSha256(abs) is { } observed && !snapshot.Sha256.Equals(observed, StringComparison.OrdinalIgnoreCase))
                return Fail(input.Path, "Notebook changed since it was read in this turn; read it again and integrate its changes.");
            var context = new SwarmCore.Agent.Runtime.AsyncLocalCurrentTurnContextAccessor().Current;
            var expected = context?.GetMissionWriteBaseAsync is { } getBase ? await getBase(abs, ct) : null;
            if (expected is not null && !snapshot.Sha256.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw AtomicTextFile.Conflict();

            var result = Apply(doc, operation, input);
            if (result.Error is not null) return Fail(input.Path, result.Error);

            var text = doc.Serialize();
            try { using var _ = JsonDocument.Parse(text); NotebookDocument.Parse(text); }
            catch (Exception e) when (e is JsonException or InvalidDataException)
            { return Fail(input.Path, "The edited notebook did not validate; the file was left unchanged. " + e.Message); }

            var bytes = snapshot.Encode(text);
            await AtomicTextFile.WriteAsync(abs, bytes, snapshot.Sha256, ct);
            var written = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            TextFileObservations.Observe(abs, written);
            return new Output
            {
                Path = input.Path,
                Succeeded = true,
                Operation = operation,
                CellIndex = result.Index,
                CellId = result.Id,
                CellType = result.Type,
                CellCount = doc.Cells.Count,
                Preview = result.Preview,
                Sha256 = written,
            };
        });

    private sealed record Applied(string? Error, int? Index = null, string? Id = null, string? Type = null, string? Preview = null);

    private static Applied Apply(NotebookDocument doc, string operation, Input input)
    {
        string? newType = null;
        if (input.CellType is not null)
        {
            newType = input.CellType.Trim().ToLowerInvariant();
            if (!CellTypes.Contains(newType)) return new($"Unknown cell type '{input.CellType}'. Use code, markdown or raw.");
        }

        var hasTarget = !string.IsNullOrWhiteSpace(input.CellId) || input.CellIndex is not null;
        int index;
        if (operation == "insert" && (!hasTarget || (string.IsNullOrWhiteSpace(input.CellId) && input.CellIndex == doc.Cells.Count))) index = -1;
        else
        {
            var located = Locate(doc, input.CellId, input.CellIndex);
            if (located.Error is not null) return new(located.Error);
            index = located.Index;
        }

        switch (operation)
        {
            case "replace":
            {
                if (input.Source is null) return new("replace needs source.");
                var cell = (JsonObject)doc.Cells[index]!;
                var old = NotebookDocument.GetSource(cell);
                NotebookDocument.SetSource(cell, input.Source);
                if (newType is not null && newType != NotebookDocument.CellType(cell)) ChangeType(cell, newType);
                else if (NotebookDocument.CellType(cell) == "code") ClearOutputs(cell);
                return new(null, index, NotebookDocument.CellId(cell), NotebookDocument.CellType(cell), Header(index, cell) + Diff(old, input.Source));
            }
            case "insert":
            {
                var type = newType ?? "code";
                var position = (input.Position ?? "after").Trim().ToLowerInvariant();
                if (position is not ("before" or "after")) return new($"Unknown position '{input.Position}'. Use before or after.");
                var at = index < 0 ? doc.Cells.Count : position == "before" ? index : index + 1;
                var cell = new JsonObject { ["cell_type"] = type };
                if (type == "code") { cell["execution_count"] = null; }
                if (doc.UsesCellIds) cell["id"] = doc.NewCellId();
                cell["metadata"] = new JsonObject();
                if (type == "code") cell["outputs"] = new JsonArray();
                NotebookDocument.SetSource(cell, input.Source ?? "");
                doc.Cells.Insert(at, cell);
                return new(null, at, NotebookDocument.CellId(cell), type, Header(at, cell) + Diff("", input.Source ?? ""));
            }
            case "delete":
            {
                var cell = (JsonObject)doc.Cells[index]!;
                var preview = Header(index, cell) + Diff(NotebookDocument.GetSource(cell), "");
                doc.Cells.RemoveAt(index);
                return new(null, index, NotebookDocument.CellId(cell), NotebookDocument.CellType(cell), preview);
            }
            default: // change_type
            {
                if (newType is null) return new("change_type needs cellType (code, markdown or raw).");
                var cell = (JsonObject)doc.Cells[index]!;
                var oldType = NotebookDocument.CellType(cell);
                var old = NotebookDocument.GetSource(cell);
                if (oldType == newType && input.Source is null) return new($"Cell {index} is already {newType}.");
                if (input.Source is not null) NotebookDocument.SetSource(cell, input.Source);
                if (oldType != newType) ChangeType(cell, newType);
                else if (newType == "code") ClearOutputs(cell);
                var preview = Header(index, cell) + $"~ type: {oldType} -> {newType}" + (input.Source is null ? "" : "\n" + Diff(old, input.Source));
                return new(null, index, NotebookDocument.CellId(cell), newType, preview);
            }
        }
    }

    private static (int Index, string? Error) Locate(NotebookDocument doc, string? id, int? index)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            for (var i = 0; i < doc.Cells.Count; i++)
                if (doc.Cells[i] is JsonObject c && NotebookDocument.CellId(c) == id)
                {
                    if (index is not null && index != i) return (-1, $"Cell id '{id}' is at index {i}, not {index}.");
                    return (i, null);
                }
            return (-1, $"No cell has id '{id}'. Read the notebook to get current ids.");
        }
        if (index is null) return (-1, "Name the cell with cellId or cellIndex.");
        if (index < 0 || index >= doc.Cells.Count) return (-1, $"Cell index {index} is out of range; the notebook has {doc.Cells.Count} cells.");
        return (index.Value, null);
    }

    private static void ClearOutputs(JsonObject cell)
    {
        NotebookDocument.SetSorted(cell, "outputs", new JsonArray());
        NotebookDocument.SetSorted(cell, "execution_count", null);
    }

    private static void ChangeType(JsonObject cell, string type)
    {
        cell["cell_type"] = type;
        if (type == "code")
        {
            cell.Remove("attachments");
            ClearOutputs(cell);
        }
        else
        {
            cell.Remove("outputs");
            cell.Remove("execution_count");
        }
    }

    private static string Header(int index, JsonObject cell)
    {
        var id = NotebookDocument.CellId(cell);
        return $"@@ cell {index}{(id is null ? "" : " id " + id)} ({NotebookDocument.CellType(cell)})\n";
    }

    private static string Diff(string oldText, string newText)
    {
        const int cap = 40;
        var oldLines = oldText.Length == 0 ? [] : oldText.Replace("\r\n", "\n").Split('\n');
        var newLines = newText.Length == 0 ? [] : newText.Replace("\r\n", "\n").Split('\n');
        var prefix = 0;
        while (prefix < oldLines.Length && prefix < newLines.Length && oldLines[prefix] == newLines[prefix]) prefix++;
        var suffix = 0;
        while (suffix < oldLines.Length - prefix && suffix < newLines.Length - prefix
               && oldLines[^(suffix + 1)] == newLines[^(suffix + 1)]) suffix++;
        var lines = new List<string>();
        for (var i = Math.Max(0, prefix - 2); i < prefix; i++) lines.Add(" " + oldLines[i]);
        for (var i = prefix; i < oldLines.Length - suffix; i++) lines.Add("-" + oldLines[i]);
        for (var i = prefix; i < newLines.Length - suffix; i++) lines.Add("+" + newLines[i]);
        for (var i = newLines.Length - suffix; i < Math.Min(newLines.Length, newLines.Length - suffix + 2); i++) lines.Add(" " + newLines[i]);
        if (lines.Count > cap)
        {
            var extra = lines.Count - cap;
            lines = lines.Take(cap).ToList();
            lines.Add($"... ({extra} more lines)");
        }
        return string.Join("\n", lines);
    }

    private static Output Fail(string path, string error) => new() { Path = path, Succeeded = false, Error = error };
}
