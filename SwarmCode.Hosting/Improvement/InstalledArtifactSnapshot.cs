using System.Security.Cryptography;

namespace SwarmCode.Hosting.Improvement;

/// <summary>Host-owned artifact identity. Directory symlinks are not traversed, matching the helper's pathlib walk.</summary>
internal static class InstalledArtifactSnapshot
{
    public static IReadOnlyDictionary<string, string> Capture(string root)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new Stack<DirectoryInfo>(); pending.Push(new(root));
        while (pending.TryPop(out var directory))
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo child)
                { if (child.LinkTarget is null) pending.Push(child); continue; }
                using var stream = new FileStream(entry.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                hashes.Add(Path.GetRelativePath(root, entry.FullName).Replace('\\', '/'), Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
            }
        return hashes;
    }
}
