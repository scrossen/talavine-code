using System.Text;
using System.Text.RegularExpressions;

namespace SwarmCore.Coding.Tools;

/// <summary>Shared repository traversal: directory skips, binary sniffing, glob matching.</summary>
internal static class RepoWalk
{
    public static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".vs", ".idea", "packages", "TestResults",
    };

    /// <summary>Enumerates files under <paramref name="start"/> (or the file itself), skipping noise directories.</summary>
    public static IEnumerable<string> EnumerateFiles(string start)
    {
        if (File.Exists(start)) { yield return start; yield break; }
        if (!Directory.Exists(start)) yield break;
        var stack = new Stack<string>();
        stack.Push(start);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(dir);
                dirs = Directory.GetDirectories(dir);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException) { continue; }
            Array.Sort(files, StringComparer.Ordinal);
            foreach (var f in files) yield return f;
            Array.Sort(dirs, StringComparer.Ordinal);
            for (var i = dirs.Length - 1; i >= 0; i--)
            {
                var name = System.IO.Path.GetFileName(dirs[i]);
                if (SkippedDirectories.Contains(name)) continue;
                try { if ((File.GetAttributes(dirs[i]) & FileAttributes.ReparsePoint) != 0) continue; }
                catch { continue; }
                stack.Push(dirs[i]);
            }
        }
    }

    public static string Relative(string root, string absolute) =>
        System.IO.Path.GetRelativePath(root, absolute).Replace('\\', '/');

    public static bool LooksBinary(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[8192];
            var n = fs.Read(buf, 0, buf.Length);
            return Array.IndexOf(buf, (byte)0, 0, n) >= 0;
        }
        catch { return true; }
    }

    /// <summary>Converts a glob (supports **, *, ?) to a regex over forward-slash relative paths.</summary>
    public static Regex GlobToRegex(string glob)
    {
        glob = glob.Replace('\\', '/').TrimStart('/');
        if (glob.StartsWith("./", StringComparison.Ordinal)) glob = glob[2..];
        var sb = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    i++;
                    if (i + 1 < glob.Length && glob[i + 1] == '/') { i++; sb.Append("(?:.*/)?"); }
                    else sb.Append(".*");
                }
                else sb.Append("[^/]*");
            }
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }
}
