using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SwarmCore.Coding.Tools;

/// <summary>
/// A parsed Jupyter notebook (.ipynb) that re-serializes in the file's own style: key order, indentation,
/// <c>ensure_ascii</c> escaping, line endings and trailing newline are detected from the original text, so a
/// Jupyter-written notebook round-trips byte for byte and an edit changes only the cells it touches.
/// </summary>
public sealed partial class NotebookDocument
{
    private readonly string? _indent;
    private readonly string _newline;
    private readonly bool _trailingNewline;
    private readonly bool _ensureAscii;
    private readonly bool _compactSpaced;

    private NotebookDocument(JsonObject root, JsonArray cells, string? indent, string newline, bool trailingNewline, bool ensureAscii, bool compactSpaced)
    {
        Root = root;
        Cells = cells;
        _indent = indent;
        _newline = newline;
        _trailingNewline = trailingNewline;
        _ensureAscii = ensureAscii;
        _compactSpaced = compactSpaced;
    }

    public JsonObject Root { get; }
    public JsonArray Cells { get; }

    public int NbFormat => Root["nbformat"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;
    public int NbFormatMinor => Root["nbformat_minor"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;

    /// <summary>nbformat 4.5 introduced required cell ids.</summary>
    public bool UsesCellIds => NbFormat > 4 || (NbFormat == 4 && NbFormatMinor >= 5);

    /// <summary>Parses notebook text; throws <see cref="InvalidDataException"/> with a reason when it is not a notebook.</summary>
    public static NotebookDocument Parse(string text)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow }); }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        { throw new InvalidDataException("Not a valid Jupyter notebook: the file is not valid JSON (" + e.Message + ")."); }
        if (node is not JsonObject root) throw new InvalidDataException("Not a valid Jupyter notebook: the top level is not a JSON object.");
        if (root["cells"] is not JsonArray cells) throw new InvalidDataException("Not a valid Jupyter notebook: there is no \"cells\" array (nbformat 3 and older notebooks are not supported).");
        if (root["nbformat"] is not JsonValue fmt || !fmt.TryGetValue<int>(out var major))
            throw new InvalidDataException("Not a valid Jupyter notebook: \"nbformat\" is missing or not an integer.");
        if (major < 4) throw new InvalidDataException($"Not a valid Jupyter notebook: nbformat {major} is not supported (4 or later required).");
        for (var i = 0; i < cells.Count; i++)
            if (cells[i] is not JsonObject cell || cell["cell_type"] is not JsonValue)
                throw new InvalidDataException($"Not a valid Jupyter notebook: cell {i} has no cell_type.");

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var trailing = text.EndsWith('\n');
        var indent = DetectIndent(text);
        var ensureAscii = IsAscii(text) && EscapedNonAscii().IsMatch(text);
        var compactSpaced = indent is null && text.Contains(", ", StringComparison.Ordinal);
        return new NotebookDocument(root, cells, indent, newline, trailing, ensureAscii, compactSpaced);
    }

    /// <summary>Serializes in the original file's style.</summary>
    public string Serialize()
    {
        var sb = new StringBuilder();
        Write(sb, Root, 0);
        if (_trailingNewline) sb.Append(_newline);
        return sb.ToString();
    }

    public static string CellType(JsonObject cell) => cell["cell_type"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    public static string? CellId(JsonObject cell) => cell["id"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Cell source as one string (nbformat allows a string or a list of lines).</summary>
    public static string GetSource(JsonObject cell) => JoinText(cell["source"]);

    /// <summary>Stores source as a list of lines, each keeping its newline except the last, as Jupyter writes it.</summary>
    public static void SetSource(JsonObject cell, string source) => cell["source"] = SplitLines(source);

    public static JsonArray SplitLines(string text)
    {
        var lines = new JsonArray();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            lines.Add(JsonValue.Create(text[start..(i + 1)]));
            start = i + 1;
        }
        if (start < text.Length) lines.Add(JsonValue.Create(text[start..]));
        return lines;
    }

    /// <summary>Joins an nbformat multiline string (a string or a list of strings).</summary>
    public static string JoinText(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray a => string.Concat(a.Select(x => x is JsonValue xv && xv.TryGetValue<string>(out var s) ? s : "")),
        _ => "",
    };

    /// <summary>A fresh 8-hex-digit id (nbformat's own format) that no cell uses yet.</summary>
    public string NewCellId()
    {
        var used = Cells.OfType<JsonObject>().Select(CellId).Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
        while (true)
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            if (!used.Contains(id)) return id;
        }
    }

    /// <summary>Adds <paramref name="key"/> before the first existing key that sorts after it (Jupyter writes sorted keys).</summary>
    public static void SetSorted(JsonObject obj, string key, JsonNode? value)
    {
        if (obj.ContainsKey(key)) { obj[key] = value; return; }
        var index = 0;
        foreach (var pair in obj)
        {
            if (string.CompareOrdinal(pair.Key, key) > 0) break;
            index++;
        }
        obj.Insert(index, key, value);
    }

    private static string? DetectIndent(string text)
    {
        var nl = text.IndexOf('\n');
        if (nl < 0) return null;
        var i = nl + 1;
        var start = i;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
        return i > start ? text[start..i] : "";
    }

    private static bool IsAscii(string text)
    {
        foreach (var c in text) if (c > 0x7E && c != '\u007F') return false;
        return true;
    }

    [GeneratedRegex(@"(?<!\\)(?:\\\\)*\\u(?!00[0-7][0-9a-fA-F])[0-9a-fA-F]{4}")]
    private static partial Regex EscapedNonAscii();

    private void Write(StringBuilder sb, JsonNode? node, int level)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject obj:
                if (obj.Count == 0) { sb.Append("{}"); break; }
                sb.Append('{');
                var firstKey = true;
                foreach (var (key, value) in obj)
                {
                    if (!firstKey) sb.Append(_indent is null && _compactSpaced ? ", " : ",");
                    firstKey = false;
                    NewLine(sb, level + 1);
                    WriteString(sb, key);
                    sb.Append(_indent is null && !_compactSpaced ? ":" : ": ");
                    Write(sb, value, level + 1);
                }
                NewLine(sb, level);
                sb.Append('}');
                break;
            case JsonArray arr:
                if (arr.Count == 0) { sb.Append("[]"); break; }
                sb.Append('[');
                for (var i = 0; i < arr.Count; i++)
                {
                    if (i > 0) sb.Append(_indent is null && _compactSpaced ? ", " : ",");
                    NewLine(sb, level + 1);
                    Write(sb, arr[i], level + 1);
                }
                NewLine(sb, level);
                sb.Append(']');
                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                WriteString(sb, value.GetValue<string>());
                break;
            default:
                // Numbers, booleans and null keep their original token text.
                sb.Append(node.ToJsonString());
                break;
        }
    }

    private void NewLine(StringBuilder sb, int level)
    {
        if (_indent is null) return;
        sb.Append(_newline);
        for (var i = 0; i < level; i++) sb.Append(_indent);
    }

    // Python json.dumps escaping (what Jupyter uses), with ensure_ascii detected from the file.
    private void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || (_ensureAscii && c > 0x7E)) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
