namespace SwarmCore.Abstractions.Processes;

/// <summary>Locates a self-contained helper inside its containing signed app.</summary>
public static class MacAppBundleHelper
{
    public static string? Resolve(string helperName, IEnumerable<string?> anchors, ICollection<string>? probedPaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(helperName);
        if (helperName is "." or ".." || helperName.IndexOfAny(['/', '\\']) >= 0)
            throw new ArgumentException("A helper name must be a single filename.", nameof(helperName));

        foreach (var anchor in anchors.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct())
        {
            var directory = new DirectoryInfo(Path.GetFullPath(anchor!));
            while (directory is not null)
            {
                if (directory.Name == "Contents" && directory.Parent?.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var candidate = Path.Combine(directory.FullName, "Helpers", helperName + ".bundle", "Contents", "MacOS", helperName);
                    probedPaths?.Add(candidate);
                    if (File.Exists(candidate))
                        return candidate;
                    break;
                }
                directory = directory.Parent;
            }
        }
        return null;
    }
}
