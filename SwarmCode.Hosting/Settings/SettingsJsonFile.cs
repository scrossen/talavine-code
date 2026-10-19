using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SwarmCode.Hosting.Settings;

/// <summary>Fresh reads, an exclusive cross-process lease and atomic replacement. Invalid existing data fails closed.</summary>
internal static class SettingsJsonFile
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, PropertyNameCaseInsensitive = true };
    internal static JsonObject Read(string path)
    {
        var bytes = ReadSnapshot(path);
        if (bytes is null) return new();
        using var stream = new MemoryStream(bytes);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return JsonNode.Parse(reader.ReadToEnd()) as JsonObject
            ?? throw new InvalidDataException("Settings must be a JSON object.");
    }
    internal static string Revision(string path)
        => ReadSnapshot(path) is { } bytes ? Convert.ToHexString(SHA256.HashData(bytes)) : "missing";

    private static byte[]? ReadSnapshot(string path)
    {
        // Writers replace a complete file atomically. A reader must allow that replacement,
        // and briefly retry a Windows sharing lock without converting it into empty settings.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var buffer = new MemoryStream();
                file.CopyTo(buffer);
                return buffer.ToArray();
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (Exception ex) when (IsTransientSharing(ex) && attempt < 10)
            { Thread.Sleep(10); }
        }
    }
    internal static T Mutate<T>(string path, Func<JsonObject, T> edit, string? revision = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var lease = Lease(path);
        if (revision is not null && Revision(path) != revision) throw new InvalidOperationException("Settings changed or were deleted. Read them again before saving.");
        var root = Read(path);
        var result = edit(root);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(tmp, root.ToJsonString(Json), Encoding.UTF8); ReplaceSnapshot(tmp, path); }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
        return result;
    }
    private static bool IsTransientSharing(Exception error)
        => error is IOException && (error.HResult & 0xffff) is 32 or 33
            || error is UnauthorizedAccessException && (error.HResult & 0xffff) == 5;

    private static void ReplaceSnapshot(string temporary, string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { File.Move(temporary, path, true); return; }
            catch (Exception ex) when (IsTransientSharing(ex) && attempt < 10) { Thread.Sleep(10); }
        }
    }

    internal static FileStream Lease(string path)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try { return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < until) { Thread.Sleep(25); }
        }
    }
    internal static JsonNode? Get(JsonObject root, string key) => root.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
    internal static void Set(JsonObject root, string key, JsonNode? value)
    {
        var actual = root.Select(p => p.Key).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
        root[actual] = value?.DeepClone();
    }
    internal static void Merge(JsonObject root, JsonObject changes)
    {
        foreach (var pair in changes)
        {
            if (pair.Value is JsonObject nested && Get(root, pair.Key) is JsonObject target) Merge(target, nested);
            else Set(root, pair.Key, pair.Value);
        }
    }
}
