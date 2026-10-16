using System.Text.Json;
using System.Text.RegularExpressions;

namespace SwarmCore.Coding.Ide;

/// <summary>
/// A file location to open in the person's editor. <see cref="SearchText"/> lets a host find the line later (an
/// edit's new text) when the tool result carries no line number.
/// </summary>
public sealed record EditorTarget(string Path, int? Line = null, int? Column = null, string? SearchText = null)
{
    /// <summary>"path", "path:12" or "path:12:3" for labels.</summary>
    public string Label => Line is { } line ? Column is { } column ? $"{Path}:{line}:{column}" : $"{Path}:{line}" : Path;
}

/// <summary>Parses <c>path:line[:col]</c> references (including <c>path#L12</c> and Windows drive paths).</summary>
public static partial class FileReferenceParser
{
    // A path with a file extension, optionally followed by :line[:col], :Lline[-Lend] or #Lline. Drive letters and
    // both separators are allowed; whitespace, quotes, and URL schemes are not (a code span with them is prose).
    [GeneratedRegex(@"^(?<path>(?:[A-Za-z]:[\\/])?[^\s:*?""<>|]*[^\s:*?""<>|/\\.]\.[A-Za-z][A-Za-z0-9_+-]{0,11})(?:(?::|#)L?(?<line>\d{1,7})(?:-L?\d{1,7})?(?::(?<col>\d{1,5}))?)?$")]
    private static partial Regex Reference();

    public static bool TryParse(string? text, out EditorTarget target)
    {
        target = null!;
        var trimmed = text?.Trim().Trim('`');
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 1024 || trimmed.Contains("://", StringComparison.Ordinal)) return false;
        var match = Reference().Match(trimmed);
        if (!match.Success) return false;
        int? line = match.Groups["line"].Success && int.TryParse(match.Groups["line"].Value, out var l) && l > 0 ? l : null;
        int? column = line is not null && match.Groups["col"].Success && int.TryParse(match.Groups["col"].Value, out var c) && c > 0 ? c : null;
        target = new EditorTarget(match.Groups["path"].Value, line, column);
        return true;
    }
}

/// <summary>
/// File locations a coding tool call touched, read structurally from its input and result JSON (no per-tool
/// branches): <c>path</c>-like properties, match objects carrying a path and a line, and string arrays of
/// files. Paths are returned as the tool reported them; hosts resolve and fence them with <see cref="EditorPathPolicy"/>.
/// </summary>
public static class ToolFileReferences
{
    private const int MaxJsonChars = 2 * 1024 * 1024;
    private static readonly string[] PathKeys = ["path", "file_path", "filePath", "file", "filename", "notebook_path", "notebookPath"];
    private static readonly string[] LineKeys = ["line", "lineNumber", "line_number", "startLine", "start_line"];
    private static readonly string[] FileListKeys = ["files", "paths", "matches", "entries", "results", "changes"];
    private static readonly string[] SearchKeys = ["newText", "new_text", "new_string", "newString"];

    public static IReadOnlyList<EditorTarget> Extract(string? inputJson, string? resultJson, int max = 8)
    {
        var found = new List<EditorTarget>();
        // The result first: it carries resolved paths and line numbers (grep matches); the input names the target.
        Visit(Parse(resultJson), found, max, 0);
        var input = Parse(inputJson);
        if (input is { } root && root.ValueKind == JsonValueKind.Object && FindPath(root) is { } inputPath)
        {
            var search = SearchKeys.Select(k => String(root, k)).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
            var line = Int(root, LineKeys);
            var existing = found.FindIndex(t => SamePath(t.Path, inputPath));
            if (existing < 0) found.Insert(0, new EditorTarget(inputPath, line, null, search));
            else if (found[existing].Line is null && (line is not null || search is not null))
                found[existing] = found[existing] with { Line = line, SearchText = search };
        }
        else Visit(input, found, max, 0);
        return found
            .GroupBy(t => (t.Path.Replace('\\', '/'), t.Line))
            .Select(g => g.First())
            .Take(max)
            .ToArray();
    }

    private static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxJsonChars) return null;
        var trimmed = json.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is not ('{' or '[')) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException) { return null; }
    }

    private static void Visit(JsonElement? element, List<EditorTarget> found, int max, int depth)
    {
        if (element is not { } e || depth > 6 || found.Count >= max) return;
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                if (FindPath(e) is { } path)
                {
                    found.Add(new EditorTarget(path, Int(e, LineKeys), Int(e, ["column", "col", "character"])));
                    return;
                }
                foreach (var property in e.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Array && FileListKeys.Any(k => string.Equals(k, property.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        foreach (var item in property.Value.EnumerateArray())
                        {
                            if (found.Count >= max) return;
                            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } file)
                            {
                                if (FileReferenceParser.TryParse(file, out var parsed)) found.Add(parsed);
                            }
                            else Visit(item, found, max, depth + 1);
                        }
                    }
                    else if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) Visit(property.Value, found, max, depth + 1);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) Visit(item, found, max, depth + 1);
                break;
        }
    }

    private static string? FindPath(JsonElement e)
        => PathKeys.Select(k => String(e, k)).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && p.Length <= 1024 && !p.Contains('\n'));

    private static string? String(JsonElement e, string key)
    {
        foreach (var property in e.EnumerateObject())
            if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        return null;
    }

    private static int? Int(JsonElement e, string[] keys)
    {
        foreach (var property in e.EnumerateObject())
            if (keys.Any(k => string.Equals(k, property.Name, StringComparison.OrdinalIgnoreCase))
                && property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var value) && value > 0)
                return value;
        return null;
    }

    private static bool SamePath(string a, string b)
        => string.Equals(a.Replace('\\', '/').TrimStart('.', '/'), b.Replace('\\', '/').TrimStart('.', '/'), StringComparison.OrdinalIgnoreCase)
           || a.Replace('\\', '/').EndsWith("/" + b.Replace('\\', '/').TrimStart('.', '/'), StringComparison.OrdinalIgnoreCase)
           || b.Replace('\\', '/').EndsWith("/" + a.Replace('\\', '/').TrimStart('.', '/'), StringComparison.OrdinalIgnoreCase);
}
