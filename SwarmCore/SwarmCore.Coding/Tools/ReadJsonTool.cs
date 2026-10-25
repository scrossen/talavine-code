using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCore.Agent.Catalog;
using SwarmCore.Agent.Continual;
using SwarmCore.Missions;

namespace SwarmCore.Coding.Tools;

/// <summary>Deterministic, bounded JSON inspection. Projection never executes an expression or assumes an element is an object.</summary>
public sealed class ReadJsonTool(ISandboxPathResolver sandbox)
{
    public const string ToolId = "fs.read_json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed class Input
    {
        [Description("JSON file path within the current authorized workspace/file scope.")]
        public required string Path { get; set; }
        [Description("RFC 6901 JSON Pointer to select a value, e.g. /publications or /rows/0. Empty selects the root. No code or expressions.")]
        public string? Pointer { get; set; }
        [Description("Optional relative JSON Pointers projected from each selected array element (or the one selected value), e.g. ['/title','/status']. Missing paths and primitive/null elements are reported safely.")]
        public List<string>? Fields { get; set; }
        [Description("Maximum selected array elements to return. Default 100; range 1–1000.")]
        public int? MaxItems { get; set; }
        [Description("Maximum serialized result characters. Default 16000; range 2048–100000. Oversized values are omitted, never returned as broken JSON.")]
        public int? MaxOutputChars { get; set; }
        [Description("Maximum file bytes to parse. Default 4 MB; range 4–16777216. Larger files require a narrower source file.")]
        public int? MaxBytes { get; set; }
    }

    public sealed record Output
    {
        public required string Path { get; init; }
        public bool Succeeded { get; init; }
        public string? Error { get; init; }
        public string? Hint { get; init; }
        public long ByteCount { get; init; }
        public string? Sha256 { get; init; }
        public string? SelectedKind { get; init; }
        public int TotalItems { get; init; }
        public int ReturnedItems => Values.Count;
        public bool Truncated { get; init; }
        public JsonArray Values { get; init; } = [];
    }

    public ToolDescriptor Descriptor => ToolDescriptor.Create<Input, Output>(ToolId, "coding",
        "Inspect a JSON file without running a shell/script. Returns a complete-file SHA256 and bounded valid JSON. " +
        "Select with an RFC 6901 Pointer; optionally project Fields across mixed arrays, safely reporting missing fields on non-object elements. " +
        "Prefer this for deterministic ledger/config/data inspection; use fs.read_file for non-JSON text.",
        ToolCostClass.Cheap, ToolSideEffect.ReadOnly, ReadAsync,
        keywords: ["json", "read", "inspect", "file", "project", "ledger"], enforcesMissionResourceBoundary: true);

    private async Task<Output> ReadAsync(Input input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var cap = input.MaxOutputChars ?? 16000;
        var bytes = input.MaxBytes ?? 4 * 1024 * 1024;
        var take = input.MaxItems ?? 100;
        Output Fail(string error, string hint, string? sha = null, long length = 0)
            => Fit(new() { Path = input.Path ?? "", Error = error, Hint = hint, Sha256 = sha, ByteCount = length }, Math.Clamp(cap, 2048, 100000));
        if (string.IsNullOrWhiteSpace(input.Path) || input.Path.Length > 4096)
            return Fail("invalid-path", "Provide a nonempty authorized file path of at most 4096 characters.");
        if (cap is < 2048 or > 100000 || bytes is < 4 or > 16 * 1024 * 1024 || take is < 1 or > 1000)
            return Fail("invalid-limit", "Use the documented MaxBytes, MaxItems and MaxOutputChars bounds.");
        if (!TryPointer(input.Pointer ?? "", out var pointer) || input.Fields?.Count > 32)
            return Fail("invalid-projection", "Use an RFC 6901 Pointer and at most 32 relative field pointers.");
        var fields = new List<(string Name, string[] Segments)>();
        foreach (var field in input.Fields ?? [])
        {
            if (field is null || !TryPointer(field, out var segments))
                return Fail("invalid-projection", "Fields must be RFC 6901 pointers; use ~0 for tilde and ~1 for slash.");
            if (!fields.Any(f => f.Name == field)) fields.Add((field, segments));
        }
        var absolute = sandbox.Resolve(input.Path, MissionEffect.Inspect);
        await MissionPrimitiveFence.ValidateAsync(ct);
        FileStream opened;
        try { opened = new FileStream(absolute, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        { return Fail("file-not-found", "The requested JSON file is missing; locate its current path with code.glob or fs.list_files."); }
        await using var stream = opened;
        var length = stream.Length;
        if (length > bytes) return Fail("file-too-large", "The complete file exceeds MaxBytes; increase the bound up to 16 MB or inspect a narrower source file.", length: length);
        var contents = new byte[(int)length];
        await stream.ReadExactlyAsync(contents, ct);
        if (stream.Length != length) return Fail("file-changed", "The file size changed during inspection; read its current state again.");
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(contents));
        string text;
        try { text = AtomicTextFile.Decode(contents).Content; }
        catch (Exception ex) when (ex is System.Text.DecoderFallbackException or InvalidDataException)
        { return Fail("unsupported-encoding", "The file is not supported text JSON.", sha, length); }
        TextFileObservations.Observe(absolute, sha);
        JsonDocument document;
        try { document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 48 }); }
        catch (JsonException) { return Fail("invalid-json", "The file is malformed JSON or exceeds depth 48; no script ran and no file was changed.", sha, length); }
        using (document)
        {
            if (!TrySelect(document.RootElement, pointer, out var selected))
                return Fail("pointer-not-found", "The selected pointer does not exist in this JSON shape.", sha, length);
            var total = selected.ValueKind == JsonValueKind.Array ? selected.GetArrayLength() : 1;
            var values = new JsonArray();
            var used = 0;
            foreach (var element in selected.ValueKind == JsonValueKind.Array ? selected.EnumerateArray() : new[] { selected }.AsEnumerable())
            {
                ct.ThrowIfCancellationRequested();
                if (values.Count >= take) break;
                JsonNode? value;
                if (fields.Count == 0) value = JsonNode.Parse(element.GetRawText());
                else
                {
                    var projected = new JsonObject(); var missing = new JsonArray();
                    foreach (var (name, segments) in fields)
                    {
                        if (TrySelect(element, segments, out var found)) projected[name] = JsonNode.Parse(found.GetRawText());
                        else { projected[name] = null; missing.Add(name); }
                    }
                    value = new JsonObject { ["sourceKind"] = element.ValueKind.ToString().ToLowerInvariant(), ["fields"] = projected, ["missingFields"] = missing };
                }
                var size = JsonSerializer.Serialize(value, Json).Length;
                if (used + size > cap - 1024) break;
                used += size + 1; values.Add(value);
            }
            return Fit(new() { Path = input.Path, Succeeded = true, ByteCount = length, Sha256 = sha,
                SelectedKind = selected.ValueKind.ToString().ToLowerInvariant(), TotalItems = total, Values = values,
                Truncated = values.Count < total, Hint = values.Count < total ? "Some values were omitted by the item/output limit. Select a narrower Pointer or Fields for more detail." : null }, cap);
        }
    }

    private static Output Fit(Output output, int cap)
    {
        while (JsonSerializer.Serialize(output, Json).Length > cap && output.Values.Count > 0)
        { output.Values.RemoveAt(output.Values.Count - 1); output = output with { Truncated = true }; }
        // A pathological path must not defeat the result bound. The full path is retained in the invocation receipt.
        return JsonSerializer.Serialize(output, Json).Length <= cap ? output
            : output with { Path = output.Path.Length <= 128 ? output.Path : output.Path[..128], Hint = "Output metadata was bounded; the invocation retains the complete requested path.", Truncated = true };
    }

    private static bool TryPointer(string pointer, out string[] segments)
    {
        segments = [];
        if (pointer.Length > 1024 || pointer.Length > 0 && pointer[0] != '/') return false;
        if (pointer.Length == 0) return true;
        var raw = pointer[1..].Split('/');
        foreach (var part in raw)
            for (var i = 0; i < part.Length; i++)
                if (part[i] == '~' && (++i >= part.Length || part[i] is not ('0' or '1'))) return false;
        segments = raw.Select(s => s.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();
        return true;
    }

    private static bool TrySelect(JsonElement current, string[] segments, out JsonElement selected)
    {
        foreach (var segment in segments)
        {
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment, out var property)) current = property;
            else if (current.ValueKind == JsonValueKind.Array && (segment == "0" || segment.Length > 0 && segment[0] != '0')
                && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && index >= 0 && index < current.GetArrayLength()) current = current[index];
            else { selected = default; return false; }
        }
        selected = current; return true;
    }
}
