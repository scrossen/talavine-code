using System.Text;

namespace SwarmCore.Coding.Memory;

/// <summary>A bounded slice of the repository memory index.</summary>
public sealed record RepoMemoryIndex(string RelativePath, string Content, int IncludedLines, int TotalLines, bool Truncated, int TopicFileCount);

/// <summary>One matching line in repository memory.</summary>
public sealed record RepoMemorySearchHit(string Path, int Line, string Text, int Score);

/// <summary>A topic file and its first heading.</summary>
public sealed record RepoMemoryTopic(string Path, string? Title, long Bytes);

/// <summary>A bounded read of one memory file.</summary>
public sealed record RepoMemoryReadResult(string Path, int StartLine, int EndLine, int TotalLines, bool Truncated, string Content);

/// <summary>
/// Read-only access to a repository's memory files, fenced to the index file at the repository root and
/// <c>*.md</c> files under the topic directory. Paths that climb out of the repository, absolute paths, and
/// files reached through a symbolic link or junction are refused. Pure managed code; the same on Windows and macOS.
/// </summary>
public static class RepoMemoryFiles
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private const int MaxTopicFiles = 2_000;
    private const int MaxHitText = 240;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "that", "this", "from", "into", "are", "was", "how", "what", "when", "where", "why",
        "does", "not", "but", "use", "using", "about", "our", "its", "it's", "you", "your", "can", "should", "would",
    };

    /// <summary>
    /// Normalizes a model- or user-supplied relative path and returns the full path when it names the index file or
    /// a markdown file inside the topic directory. Returns null (with <paramref name="problem"/>) otherwise.
    /// The target need not exist (proposals may create a new topic file).
    /// </summary>
    public static string? ResolvePath(string repositoryRoot, string? relativePath, RepoMemoryOptions options, out string normalized, out string? problem)
    {
        normalized = string.Empty;
        problem = null;
        if (string.IsNullOrWhiteSpace(relativePath)) { problem = "A memory file path is required."; return null; }
        var rel = relativePath.Trim().Replace('\\', '/');
        while (rel.StartsWith("./", StringComparison.Ordinal)) rel = rel[2..];
        if (rel.Length == 0 || rel.Length > 260 || rel.Any(char.IsControl) || rel.Contains(':') || rel.StartsWith('/') || Path.IsPathRooted(rel))
        { problem = "Use a path relative to the repository root, such as MEMORY.md or memory/topic.md."; return null; }
        var segments = rel.Split('/');
        if (segments.Any(s => s.Length == 0 || s == "." || s == ".."))
        { problem = "The path must not contain empty, '.' or '..' segments."; return null; }

        var topicDir = (options.TopicDirectory ?? "memory").Trim().Trim('/', '\\').Replace('\\', '/');
        var isIndex = segments.Length == 1 && string.Equals(rel, options.IndexFileName, PathComparison);
        var isTopic = topicDir.Length > 0
                      && rel.StartsWith(topicDir + "/", PathComparison)
                      && rel.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                      && rel.Length > topicDir.Length + 4;
        if (!isIndex && !isTopic)
        { problem = $"Only {options.IndexFileName} and {topicDir}/*.md are repository memory files."; return null; }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
        { problem = "The path leaves the repository."; return null; }
        if (TraversesLink(root, full))
        { problem = "Memory files reached through a symbolic link or junction are not read or written."; return null; }
        normalized = rel;
        return full;
    }

    /// <summary>True when any existing component between the root and the target is a reparse point (link/junction).</summary>
    internal static bool TraversesLink(string root, string full)
    {
        var current = full;
        while (current.Length > root.Length)
        {
            try
            {
                if (File.Exists(current) || Directory.Exists(current))
                {
                    var attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
            current = Path.GetDirectoryName(current) ?? root;
        }
        return false;
    }

    /// <summary>The index file bounded to <see cref="RepoMemoryOptions.MaxIndexLines"/> and <see cref="RepoMemoryOptions.MaxIndexCharacters"/>, cut at a line boundary.</summary>
    public static RepoMemoryIndex? LoadIndex(string repositoryRoot, RepoMemoryOptions options)
    {
        var topics = ListTopics(repositoryRoot, options).Count;
        var path = ResolvePath(repositoryRoot, options.IndexFileName, options, out var rel, out _);
        if (path is null || !File.Exists(path)) return topics == 0 ? null : new RepoMemoryIndex(options.IndexFileName, string.Empty, 0, 0, false, topics);
        var lines = ReadLines(path, options.MaxFileBytes, out var cutBySize);
        var maxLines = Math.Max(1, options.MaxIndexLines);
        var maxChars = Math.Max(200, options.MaxIndexCharacters);
        var sb = new StringBuilder();
        var included = 0;
        foreach (var line in lines)
        {
            if (included >= maxLines) break;
            var add = line.Length + 1;
            if (sb.Length + add > maxChars)
            {
                if (included == 0) { sb.Append(line[..Math.Min(line.Length, maxChars)]).Append('\n'); included = 1; }
                break;
            }
            sb.Append(line).Append('\n');
            included++;
        }
        return new RepoMemoryIndex(rel, sb.ToString().TrimEnd(), included, lines.Count, included < lines.Count || cutBySize, topics);
    }

    /// <summary>Topic files under the topic directory (recursive, bounded), excluding links.</summary>
    public static IReadOnlyList<RepoMemoryTopic> ListTopics(string repositoryRoot, RepoMemoryOptions options)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        var topicDir = (options.TopicDirectory ?? "").Trim().Trim('/', '\\');
        if (topicDir.Length == 0) return [];
        var dir = Path.Combine(root, topicDir.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(dir) || TraversesLink(root, dir)) return [];
        var result = new List<RepoMemoryTopic>();
        try
        {
            var enumeration = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden };
            foreach (var file in Directory.EnumerateFiles(dir, "*.md", enumeration).Order(StringComparer.Ordinal))
            {
                if (result.Count >= MaxTopicFiles) break;
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (ResolvePath(root, rel, options, out _, out _) is null) continue;
                var info = new FileInfo(file);
                result.Add(new RepoMemoryTopic(rel, FirstHeading(file), info.Length));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result;
    }

    /// <summary>Lexical search over the index and topic files. An empty query lists the topic files instead.</summary>
    public static IReadOnlyList<RepoMemorySearchHit> Search(string repositoryRoot, string? query, RepoMemoryOptions options, int? limit = null)
    {
        var max = Math.Clamp(limit ?? options.MaxSearchResults, 1, Math.Max(1, options.MaxSearchResults));
        var terms = Terms(query);
        if (terms.Count == 0) return [];
        var phrase = (query ?? "").Trim().ToLowerInvariant();
        var files = new List<string>();
        var index = ResolvePath(repositoryRoot, options.IndexFileName, options, out var indexRel, out _);
        if (index is not null && File.Exists(index)) files.Add(indexRel);
        files.AddRange(ListTopics(repositoryRoot, options).Select(t => t.Path));

        var hits = new List<RepoMemorySearchHit>();
        foreach (var rel in files)
        {
            var full = ResolvePath(repositoryRoot, rel, options, out _, out _);
            if (full is null || !File.Exists(full) || new FileInfo(full).Length > options.MaxFileBytes) continue;
            var fileBonus = terms.Count(t => rel.Contains(t, StringComparison.OrdinalIgnoreCase));
            var lines = ReadLines(full, options.MaxFileBytes, out _);
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.Length == 0) continue;
                var lower = line.ToLowerInvariant();
                var matched = terms.Count(t => lower.Contains(t, StringComparison.Ordinal));
                if (matched == 0) continue;
                // Multi-word queries need at least two terms on the line unless the file name carries the rest.
                if (terms.Count >= 2 && matched + Math.Min(1, fileBonus) < 2) continue;
                var score = matched * 10 + (phrase.Length > 3 && lower.Contains(phrase, StringComparison.Ordinal) ? 15 : 0) + fileBonus * 3
                            + (line.TrimStart().StartsWith('#') ? 2 : 0);
                hits.Add(new RepoMemorySearchHit(rel, i + 1, Clip(line.Trim(), MaxHitText), score));
            }
        }
        return hits.OrderByDescending(h => h.Score).ThenBy(h => h.Path, StringComparer.Ordinal).ThenBy(h => h.Line).Take(max).ToList();
    }

    /// <summary>Reads lines [<paramref name="startLine"/>, ...] of one memory file within the character budget.</summary>
    public static RepoMemoryReadResult Read(string repositoryRoot, string relativePath, RepoMemoryOptions options, int? startLine = null, int? maxLines = null)
    {
        var full = ResolvePath(repositoryRoot, relativePath, options, out var rel, out var problem)
                   ?? throw new ArgumentException(problem);
        if (!File.Exists(full)) throw new FileNotFoundException($"{rel} does not exist. Search first to find the right topic file.");
        if (new FileInfo(full).Length > options.MaxFileBytes) throw new InvalidDataException($"{rel} is larger than the memory read limit.");
        var lines = ReadLines(full, options.MaxFileBytes, out _);
        var start = Math.Clamp(startLine ?? 1, 1, Math.Max(1, lines.Count));
        var wanted = Math.Clamp(maxLines ?? 400, 1, 5_000);
        var budget = Math.Max(500, options.MaxReadCharacters);
        var sb = new StringBuilder();
        var end = start - 1;
        for (var i = start - 1; i < lines.Count && end - start + 1 < wanted; i++)
        {
            if (sb.Length + lines[i].Length + 1 > budget && end >= start) break;
            sb.Append(lines[i]).Append('\n');
            end = i + 1;
        }
        return new RepoMemoryReadResult(rel, start, Math.Max(start, end), lines.Count, end < lines.Count, sb.ToString());
    }

    internal static List<string> Terms(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var terms = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in query.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.') current.Append(ch);
            else Flush();
        }
        Flush();
        return terms.Distinct(StringComparer.Ordinal).Take(12).ToList();

        void Flush()
        {
            var term = current.ToString().Trim('.', '-', '_');
            current.Clear();
            if (term.Length >= 2 && !StopWords.Contains(term)) terms.Add(term);
        }
    }

    internal static List<string> ReadLines(string path, long maxBytes, out bool cutBySize)
    {
        cutBySize = false;
        var lines = new List<string>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        long read = 0;
        while (reader.ReadLine() is { } line)
        {
            read += line.Length + 1;
            if (read > maxBytes) { cutBySize = true; break; }
            lines.Add(line);
        }
        return lines;
    }

    private static string? FirstHeading(string path)
    {
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            for (var i = 0; i < 40 && reader.ReadLine() is { } line; i++)
                if (line.StartsWith('#')) return Clip(line.TrimStart('#').Trim(), 120);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..(max - 3)] + "...";
}
