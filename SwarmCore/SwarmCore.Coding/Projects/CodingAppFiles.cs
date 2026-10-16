namespace SwarmCore.Coding.Projects;

public sealed record CodingAppFile(string Name, string RelativePath, bool IsDirectory);

/// <summary>Bounded browsing of the real source tree, with physical containment at every read.</summary>
public static class CodingAppFiles
{
    private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase)
        { ".git", "node_modules", "bin", "obj", ".venv", "venv", ".DS_Store" };
    public static IReadOnlyList<CodingAppFile> List(string root, string relative = ".")
        => Directory.EnumerateFileSystemEntries(CodingAppPaths.Within(root, relative))
            .Where(p => !Hidden.Contains(Path.GetFileName(p)) && !Sensitive(p))
            .Take(501).Select(p => new CodingAppFile(Path.GetFileName(p), Path.GetRelativePath(root, p), Directory.Exists(p)))
            .OrderByDescending(p => p.IsDirectory).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public static string Read(string root, string relative)
    {
        var path = CodingAppPaths.Within(root, relative);
        if (Sensitive(path)) throw new InvalidOperationException("Open credential and key files directly in your own editor.");
        using var stream = File.OpenRead(path);
        if (stream.Length > 256 * 1024) throw new InvalidOperationException("This file is too large to preview. Open it in your editor.");
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        if (text.Contains('\0')) throw new InvalidOperationException("This file has no text preview. Open it in your editor.");
        return text;
    }

    private static bool Sensitive(string path) => Path.GetFileName(path).StartsWith(".env", StringComparison.OrdinalIgnoreCase)
        || new[] { ".pfx", ".p12", ".key", ".pem" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
        || Path.GetFileName(path).Equals("appsettings.Development.json", StringComparison.OrdinalIgnoreCase);
}
