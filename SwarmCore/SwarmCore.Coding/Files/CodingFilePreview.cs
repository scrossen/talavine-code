using System.Text;
using System.Text.Json;

namespace SwarmCore.Coding.Files;

public enum CodingFilePreviewKind { Markdown, Text, Image, Pdf, Html, Unsupported }
public sealed record CodingFilePreview(string Path, string Name, CodingFilePreviewKind Kind, string? Text, string? DataUrl, long Bytes);
public sealed record CodingFileItem(string Path, string Name, bool IsDirectory);

/// <summary>Bounded local file previews. HTML is data; hosts must render it in a sandbox without scripts.</summary>
public static class CodingFilePreviews
{
    public static IReadOnlyList<string> References(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxTextBytes) return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            var paths = new List<string>();
            Visit(document.RootElement, 0);
            return paths.Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
            void Visit(JsonElement element, int depth)
            {
                if (depth > 8 || paths.Count >= 8) return;
                if (element.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in element.EnumerateObject())
                    {
                        if (property.Name is "path" or "file_path" or "filePath" or "filename" && property.Value.ValueKind == JsonValueKind.String)
                        {
                            if (property.Value.GetString() is { Length: > 0 } path) paths.Add(path);
                        }
                        else if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) Visit(property.Value, depth + 1);
                    }
                }
                else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Visit(item, depth + 1);
            }
        }
        catch (JsonException) { return []; }
    }
    public const int MaxBytes = 20 * 1024 * 1024;
    public const int MaxTextBytes = 2 * 1024 * 1024;

    public static async Task<CodingFilePreview> ReadAsync(string path, CancellationToken ct = default)
    {
        path = System.IO.Path.GetFullPath(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 8192, true);
        var length = stream.Length;
        var name = System.IO.Path.GetFileName(path);
        if (length > MaxBytes) return new(path, name, CodingFilePreviewKind.Unsupported, "This file is too large for an inline preview. Open it in its default app.", null, length);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBytes) throw new IOException("The file grew beyond the preview limit. Refresh or open it in its default app.");
            buffer.Write(chunk, 0, read);
        }
        var bytes = buffer.ToArray();
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        var mime = extension switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", ".bmp" => "image/bmp", ".svg" => "image/svg+xml", ".pdf" => "application/pdf", _ => null };
        if (mime is not null) return new(path, name, extension == ".pdf" ? CodingFilePreviewKind.Pdf : CodingFilePreviewKind.Image,
            null, $"data:{mime};base64,{Convert.ToBase64String(bytes)}", bytes.Length);
        if (bytes.Length > MaxTextBytes) return new(path, name, CodingFilePreviewKind.Unsupported, "This text file exceeds the 2 MB inline preview limit.", null, bytes.Length);
        string text;
        try
        {
            using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }
        catch (DecoderFallbackException) { return new(path, name, CodingFilePreviewKind.Unsupported, "This format has no inline preview. Open it in its default app.", null, bytes.Length); }
        if (text.Contains('\0')) return new(path, name, CodingFilePreviewKind.Unsupported, "This format has no inline preview. Open it in its default app.", null, bytes.Length);
        var kind = extension switch { ".md" or ".markdown" or ".mdx" => CodingFilePreviewKind.Markdown, ".html" or ".htm" => CodingFilePreviewKind.Html, _ => CodingFilePreviewKind.Text };
        if (extension == ".json")
        {
            try { using var json = JsonDocument.Parse(text); text = JsonSerializer.Serialize(json.RootElement, new JsonSerializerOptions { WriteIndented = true }); }
            catch (JsonException) { /* Show malformed JSON as source, with all evidence preserved. */ }
        }
        return new(path, name, kind, text, null, bytes.Length);
    }

    public static IReadOnlyList<CodingFileItem> ListDirectory(string path)
        => new DirectoryInfo(path).EnumerateFileSystemInfos().Where(i => i.Name != ".git")
            .Select(i => new CodingFileItem(i.FullName, i.Name, i is DirectoryInfo))
            .OrderByDescending(i => i.IsDirectory).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Take(1000).ToArray();
}
