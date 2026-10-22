using SwarmCode.Hosting.Projects;

namespace SwarmCode.Hosting.Tests;

public sealed class DefaultProjectLocationTests : TempDirTest
{
    private string DefaultFolder => Path.Combine(Tmp, "Documents", "Talavine.Code");
    private CodingAppLibraryStore Store => new(Paths, DefaultFolder);

    [Fact]
    public void Unset_folder_uses_default_without_writing_or_creating_it_until_creation()
    {
        Assert.Equal(DefaultFolder, Store.Read().AppsFolder);
        Assert.False(Directory.Exists(DefaultFolder));
        Assert.False(File.Exists(Path.Combine(Paths.RootDirectory, "apps.json")));
        var app = Store.Create("Garden", "Track my planting", DefaultFolder);
        Assert.Equal(Path.Combine(DefaultFolder, "Garden"), app.Root);
        Assert.True(Directory.Exists(app.Root));
        Assert.Equal(app, new CodingAppLibraryStore(Paths, DefaultFolder).Get(app.Id));
        Assert.Equal(DefaultFolder, Store.Read().AppsFolder);
    }

    [Fact]
    public void Changing_implicit_default_preserves_entries_and_rejects_stale_writers()
    {
        var a = Store; var b = Store; var observed = a.Read();
        var project = Directory.CreateDirectory(Path.Combine(Tmp, "existing-project")).FullName;
        var entry = b.Add(project);
        var custom = Directory.CreateDirectory(Path.Combine(Tmp, "custom")).FullName;
        a.SetFolder(custom, observed.AppsFolder);
        Assert.Equal(entry, b.Get(entry.Id));
        Assert.Throws<InvalidOperationException>(() => b.Create("Stale", "Keep my description", DefaultFolder));
        Assert.False(Directory.Exists(DefaultFolder));
        Assert.Throws<InvalidOperationException>(() => b.SetFolder(project, observed.AppsFolder));
        Assert.Equal(custom, Store.Read().AppsFolder);
        Directory.Delete(custom); // A custom location must never silently become the default.
        Assert.Throws<DirectoryNotFoundException>(() => b.Create("Missing", "Keep this too", custom));
        Assert.Equal(custom, Store.Read().AppsFolder);
        Assert.False(Directory.Exists(custom));
    }

    [Fact]
    public void New_projects_provision_default_parent_but_never_overwrite_or_create_missing_custom_locations()
    {
        var project = Store.CreateProject(DefaultFolder, "Example");
        var file = Path.Combine(project, "keep.txt"); File.WriteAllText(file, "keep");
        Assert.Throws<IOException>(() => Store.CreateProject(DefaultFolder, "Example"));
        Assert.Equal("keep", File.ReadAllText(file));
        Assert.Throws<DirectoryNotFoundException>(() => Store.CreateProject(Path.Combine(Tmp, "missing-custom"), "Example"));
        Assert.Throws<ArgumentException>(() => Store.CreateProject(DefaultFolder, "../escape"));
    }

    [Fact]
    public void Default_is_under_the_current_users_documents_without_customer_specific_paths()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents)) documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
        Assert.Equal(Path.GetFullPath(Path.Combine(documents, "Talavine.Code")), ProjectFolders.DefaultParentDirectory);
    }
}
