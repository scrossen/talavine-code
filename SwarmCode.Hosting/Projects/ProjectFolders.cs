namespace SwarmCode.Hosting.Projects;

/// <summary>Creates an empty project without adopting or overwriting an existing directory.</summary>
public static class ProjectFolders
{
    /// <summary>Default location for user-created apps/projects; internal app data has its own root.</summary>
    public static string DefaultParentDirectory
    {
        get
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents))
                documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
            return Path.GetFullPath(Path.Combine(documents, "Talavine.Code"));
        }
    }

    public static string Create(string parentDirectory, string name)
        => Create(parentDirectory, name, createParent: false);

    internal static string Create(string parentDirectory, string name, bool createParent)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".."
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException("Enter a project name without path separators.", nameof(name));
        var parent = Path.GetFullPath(parentDirectory);
        if (createParent) Directory.CreateDirectory(parent);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("Choose an existing location for the project.");
        var root = Path.Combine(parent, name);
        if (Path.Exists(root) || new DirectoryInfo(root).LinkTarget is not null)
            throw new IOException("A file or folder with that name already exists. Choose another name or open the existing folder.");
        Directory.CreateDirectory(root);
        return root;
    }
}
