namespace SwarmCore.Abstractions.FileSync;

/// <summary>Portable path rules for explicitly mapped replicas. Home paths are expanded only by the receiver.</summary>
public static class SyncPathPolicy
{
    /// <summary>Suggests a receiver path without guessing its username. Users can override this per peer.</summary>
    public static string SuggestDestination(string sourceDirectory, string sourceHome, bool sourceWindows, bool targetWindows)
    {
        var path = sourceDirectory.Replace('\\', '/').TrimEnd('/');
        var home = sourceHome.Replace('\\', '/').TrimEnd('/');
        var comparison = sourceWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var inHome = path.Equals(home, comparison) || path.StartsWith(home + "/", comparison);
        if (sourceWindows == targetWindows)
            return inHome ? "~/" + path[home.Length..].TrimStart('/') : sourceDirectory;
        if (sourceWindows)
        {
            var relative = inHome ? path[home.Length..].TrimStart('/')
                : path.Length >= 2 && path[1] == ':' ? path[2..].TrimStart('/') : path.TrimStart('/');
            return "~/" + relative;
        }
        // Default drive-to-Mac-home rule: C:\Projects <-> ~/Projects.
        var suffix = inHome ? path[home.Length..].TrimStart('/') : path.TrimStart('/');
        return "C:\\" + suffix.Replace('/', '\\');
    }

    public static bool IsValidDestination(string? path, bool windows)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(['\0', '\r', '\n']) >= 0) return false;
        var home = path.StartsWith("~/", StringComparison.Ordinal);
        if (!home && (windows
            ? !(path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/')
            : !path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal))) return false;
        if (!windows && path.Contains('\\')) return false;
        var tail = home ? path[2..] : windows ? path[3..] : path[1..];
        return tail.Split(['/', '\\']).All(p => p is not "." and not ".." && !p.Contains(':')
            && (!windows || p.IndexOfAny(['<', '>', '"', '|', '?', '*']) < 0));
    }

    public static string ResolveDestination(string path, string home, bool windows)
    {
        if (!IsValidDestination(path, windows))
            throw new ArgumentException(windows ? "Choose an absolute Windows folder or ~/folder." : "Choose an absolute Mac folder or ~/folder.");
        return path.StartsWith("~/", StringComparison.Ordinal)
            ? home.TrimEnd('/', '\\') + (windows ? "\\" : "/") + path[2..].Replace('/', windows ? '\\' : '/')
            : path;
    }

    public static string NormalizeRelative(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/')
            || normalized.Split('/').Any(p => p.Length == 0 || p is "." or ".." || p.IndexOfAny([':', '\0']) >= 0))
            throw new ArgumentException("A sync path must stay inside its selected folder.");
        return normalized;
    }
}
