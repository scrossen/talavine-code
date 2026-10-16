using System.ComponentModel;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SwarmCore.Agent.Catalog;
using SwarmCore.Missions;

namespace SwarmCore.Coding.Tools;

/// <summary>Reads a Jupyter notebook as cells with summarized outputs (never base64 payloads).</summary>
public sealed partial class NotebookReadTool
{
    public const string ToolId = "notebook.read";
    internal const long MaxFileBytes = 64L * 1024 * 1024;

    public sealed class Input
    {
        [Description("Notebook path (.ipynb). Relative paths resolve against the repository root.")]
        public required string Path { get; set; }

        [Description("First cell index to return (0-based). Default 0.")]
        public int? StartIndex { get; set; }

        [Description("Maximum cells to return. Default all.")]
        public int? MaxCells { get; set; }

        [Description("Include summarized outputs. Default true.")]
        public bool? IncludeOutputs { get; set; }

        [Description("Maximum output characters per cell. Default 2000, max 50000.")]
        public int? MaxOutputChars { get; set; }
    }

    public sealed class Cell
    {
        public required int Index { get; init; }
        public string? Id { get; init; }
        [Description("code, markdown or raw.")]
        public required string Type { get; init; }
        public required string Source { get; init; }
        public int? ExecutionCount { get; init; }
        [Description("One entry per output: stream/text/plain/error text (truncated); rich outputs as \"[image/png output]\".")]
        public List<string>? Outputs { get; init; }
        public bool OutputsTruncated { get; init; }
    }

    public sealed class Output
    {
        public required string Path { get; init; }
        public required bool Succeeded { get; init; }
        public string? Error { get; init; }
        [Description("nbformat version, e.g. \"4.5\".")]
        public string? NbFormat { get; init; }
        public string? Kernel { get; init; }
        public string? Language { get; init; }
        public int CellCount { get; init; }
        public List<Cell>? Cells { get; init; }
        [Description("True when the requested range left cells out.")]
        public bool Truncated { get; init; }
        public string? Sha256 { get; init; }
    }

    private readonly ISandboxPathResolver _sandbox;

    public NotebookReadTool(ISandboxPathResolver sandbox) { _sandbox = sandbox; }

    public ToolDescriptor Descriptor => ToolDescriptor.Create<Input, Output>(
        toolId: ToolId,
        enforcesMissionResourceBoundary: true,
        ownerAgentId: "coding",
        description: "Read a Jupyter notebook (.ipynb): kernel, language, nbformat, and each cell's index, id, type, source, execution count and summarized outputs (text truncated; images/HTML reported as \"[image/png output]\"). Use cell ids or indexes with notebook.edit.",
        cost: ToolCostClass.Cheap,
        sideEffect: ToolSideEffect.ReadOnly,
        keywords: ["notebook", "jupyter", "ipynb", "cells", "read", "code"],
        invoke: async (input, ct) =>
        {
            var abs = _sandbox.Resolve(input.Path, SwarmCore.Agent.Continual.MissionEffect.Inspect);
            await MissionPrimitiveFence.ValidateAsync(ct);
            var loaded = await NotebookFile.LoadAsync(abs, input.Path, ct);
            if (loaded.Error is not null) return Fail(input.Path, loaded.Error);
            var (snapshot, doc) = (loaded.Snapshot!, loaded.Document!);
            TextFileObservations.Observe(abs, snapshot.Sha256);

            var start = Math.Max(0, input.StartIndex ?? 0);
            var take = input.MaxCells is > 0 ? input.MaxCells.Value : int.MaxValue;
            var includeOutputs = input.IncludeOutputs ?? true;
            var cap = Math.Clamp(input.MaxOutputChars ?? 2000, 100, 50_000);
            var cells = new List<Cell>();
            for (var i = start; i < doc.Cells.Count && cells.Count < take; i++)
            {
                var cell = (JsonObject)doc.Cells[i]!;
                List<string>? outputs = null;
                var truncated = false;
                if (includeOutputs && cell["outputs"] is JsonArray raw) (outputs, truncated) = Summarize(raw, cap);
                cells.Add(new Cell
                {
                    Index = i,
                    Id = NotebookDocument.CellId(cell),
                    Type = NotebookDocument.CellType(cell),
                    Source = NotebookDocument.GetSource(cell),
                    ExecutionCount = cell["execution_count"] is JsonValue ec && ec.TryGetValue<int>(out var n) ? n : null,
                    Outputs = outputs,
                    OutputsTruncated = truncated,
                });
            }

            var meta = doc.Root["metadata"] as JsonObject;
            var kernelspec = meta?["kernelspec"] as JsonObject;
            var languageInfo = meta?["language_info"] as JsonObject;
            return new Output
            {
                Path = input.Path,
                Succeeded = true,
                NbFormat = $"{doc.NbFormat}.{doc.NbFormatMinor}",
                Kernel = Str(kernelspec?["display_name"]) ?? Str(kernelspec?["name"]),
                Language = Str(languageInfo?["name"]) ?? Str(kernelspec?["language"]),
                CellCount = doc.Cells.Count,
                Cells = cells,
                Truncated = start > 0 || start + cells.Count < doc.Cells.Count,
                Sha256 = snapshot.Sha256,
            };
        });

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static Output Fail(string path, string error) => new() { Path = path, Succeeded = false, Error = error };

    /// <summary>One line per output, text capped at <paramref name="cap"/> characters across the cell.</summary>
    internal static (List<string> Outputs, bool Truncated) Summarize(JsonArray outputs, int cap)
    {
        var list = new List<string>();
        var remaining = cap;
        var truncated = false;
        foreach (var node in outputs)
        {
            if (node is not JsonObject output) continue;
            var text = Describe(output);
            if (remaining <= 0) { truncated = true; list.Add("[" + (outputs.Count - list.Count) + " more outputs omitted]"); break; }
            if (text.Length > remaining)
            {
                text = text[..remaining] + $"... [{text.Length - remaining} more chars]";
                truncated = true;
                remaining = 0;
            }
            else remaining -= text.Length;
            list.Add(text);
        }
        return (list, truncated);
    }

    private static string Describe(JsonObject output)
    {
        var type = Str(output["output_type"]) ?? "unknown";
        switch (type)
        {
            case "stream":
                return $"[{Str(output["name"]) ?? "stream"}] " + NotebookDocument.JoinText(output["text"]);
            case "error":
                var traceback = output["traceback"] is JsonArray tb
                    ? string.Join("\n", tb.Select(t => Str(t) ?? ""))
                    : "";
                var head = $"[error] {Str(output["ename"])}: {Str(output["evalue"])}";
                return Ansi().Replace(traceback.Length > 0 ? head + "\n" + traceback : head, "");
            case "execute_result":
            case "display_data":
            case "update_display_data":
                var sb = new StringBuilder();
                if (output["data"] is JsonObject data)
                {
                    foreach (var (mime, value) in data)
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        if (mime == "text/plain") sb.Append(NotebookDocument.JoinText(value));
                        else sb.Append('[').Append(mime).Append(" output]");
                    }
                }
                return sb.Length == 0 ? $"[{type}]" : sb.ToString();
            default:
                return $"[{type} output]";
        }
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex Ansi();
}

/// <summary>Shared loading for the notebook tools: size limit, strict text decoding and notebook validation.</summary>
internal static class NotebookFile
{
    internal sealed record Loaded(AtomicTextFile.Snapshot? Snapshot, NotebookDocument? Document, string? Error);

    public static async Task<Loaded> LoadAsync(string abs, string displayPath, CancellationToken ct)
    {
        if (!abs.EndsWith(".ipynb", StringComparison.OrdinalIgnoreCase))
            return new(null, null, "Not a Jupyter notebook path: the file must have the .ipynb extension.");
        var info = new FileInfo(abs);
        if (!info.Exists) return new(null, null, "File not found.");
        if (info.Length > NotebookReadTool.MaxFileBytes) return new(null, null, "Notebook is larger than the 64 MB limit.");
        AtomicTextFile.Snapshot snapshot;
        try { snapshot = AtomicTextFile.Decode(await File.ReadAllBytesAsync(abs, ct)); }
        catch (Exception e) when (e is DecoderFallbackException or InvalidDataException)
        { return new(null, null, "Unsupported or binary text encoding: " + e.Message); }
        try { return new(snapshot, NotebookDocument.Parse(snapshot.Content), null); }
        catch (InvalidDataException e) { return new(snapshot, null, e.Message); }
    }
}
