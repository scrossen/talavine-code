using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCode.Hosting.Settings;
using SwarmCore.Coding.Projects;

namespace SwarmCode.Hosting.Projects;

/// <summary>App-local metadata only. Field-owned mutations under a cross-process lease; no project data is deleted.</summary>
public sealed class CodingAppLibraryStore
{
    private readonly SwarmCodeDataPaths paths;
    private readonly string _defaultAppsFolder;
    public CodingAppLibraryStore(SwarmCodeDataPaths paths) : this(paths, ProjectFolders.DefaultParentDirectory) { }
    internal CodingAppLibraryStore(SwarmCodeDataPaths paths, string defaultAppsFolder)
    { this.paths = paths; _defaultAppsFolder = CodingAppPaths.Normalize(defaultAppsFolder); }

    private string FilePath => Path.Combine(paths.RootDirectory, "apps.json");
    private string Folder(JsonObject json) => json["appsFolder"]?.GetValue<string>() ?? _defaultAppsFolder;
    public CodingAppLibrary Read()
    {
        var json = SettingsJsonFile.Read(FilePath);
        return new(Folder(json), ReadEntries(json["apps"]));
    }

    // Older catalogs marked imported folders with this exact description. Read the
    // legacy shape in place; do not rewrite a snapshot over another window's edits.
    private static CodingAppProject[] ReadEntries(JsonNode? rows) => rows is null ? []
        : rows.AsArray().Select(row =>
        {
            if (row is null) throw new InvalidDataException("The app library contains an invalid entry.");
            var entry = row.Deserialize<CodingAppProject>(SettingsJsonFile.Json)!;
            return row["kind"] is null && entry.Description == "Existing project"
                ? entry with { Kind = CodingProjectKind.Project } : entry;
        }).ToArray();

    public void SetFolder(string folder, string? expectedFolder)
    {
        var full = CodingAppPaths.Normalize(folder);
        expectedFolder = expectedFolder is null ? null : CodingAppPaths.Normalize(expectedFolder);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("Choose an existing folder for your apps.");
        SettingsJsonFile.Mutate(FilePath, json =>
        {
            // A legacy caller may have observed the raw unset value. Current UI sees
            // the computed default; neither path replaces a newer explicit choice.
            if (!(expectedFolder is null && json["appsFolder"] is null)
                && !string.Equals(Folder(json), expectedFolder, CodingAppPaths.Comparison))
                throw new InvalidOperationException("Your apps folder changed in another window. Reload before changing it.");
            json["appsFolder"] = full;
            return true;
        });
    }

    public CodingAppProject Create(string name, string description, string expectedFolder)
    {
        name = CodingAppPaths.FolderName(name);
        if (string.IsNullOrWhiteSpace(description)) throw new ArgumentException("Describe the app you want to build.");
        expectedFolder = CodingAppPaths.Normalize(expectedFolder);
        return SettingsJsonFile.Mutate(FilePath, json =>
        {
            var folder = Folder(json);
            if (!string.Equals(folder, expectedFolder, CodingAppPaths.Comparison)) throw new InvalidOperationException("Your apps folder changed. Reload before creating an app.");
            // Only the implicit default may be created automatically. A missing custom
            // location must remain a visible error rather than silently switching folders.
            if (json["appsFolder"] is null) Directory.CreateDirectory(folder);
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Your apps folder is missing. Restore it or choose another folder.");
            var root = CodingAppPaths.Within(folder, name);
            if (Directory.Exists(root) || File.Exists(root)) throw new InvalidOperationException("That folder already exists. Choose another app name or use Add project.");
            var apps = Apps(json);
            Directory.CreateDirectory(root);
            var app = new CodingAppProject(Guid.NewGuid().ToString("N"), name, description.Trim(), root, DateTime.UtcNow);
            apps.Add(JsonSerializer.SerializeToNode(app, SettingsJsonFile.Json));
            return app;
        });
    }

    /// <summary>Create at the chosen new-project location, provisioning only the default parent.</summary>
    public string CreateProject(string parent, string name)
        => ProjectFolders.Create(parent, name, CodingAppPaths.Same(parent, _defaultAppsFolder));

    public CodingAppProject Add(string folder)
    {
        var root = CodingAppPaths.Normalize(folder);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("This project folder is missing. Choose its current location.");
        return SettingsJsonFile.Mutate(FilePath, json =>
        {
            var apps = Apps(json);
            var existing = ReadEntries(apps).FirstOrDefault(a => CodingAppPaths.Same(a.Root, root));
            if (existing is not null) return existing;
            var name = Path.GetFileName(root);
            var app = new CodingAppProject(Guid.NewGuid().ToString("N"), string.IsNullOrWhiteSpace(name) ? root : name, "Project folder", root, DateTime.UtcNow, CodingProjectKind.Project);
            apps.Add(JsonSerializer.SerializeToNode(app, SettingsJsonFile.Json));
            return app;
        });
    }

    public CodingAppProject Get(string id) => Read().Apps.SingleOrDefault(a => a.Id == id)
        ?? throw new InvalidOperationException("This entry is no longer in your library. Refresh My Apps and Projects.");

    public void Remove(string id) => SettingsJsonFile.Mutate(FilePath, json =>
    {
        var apps = Apps(json);
        var row = apps.FirstOrDefault(n => n?["id"]?.GetValue<string>() == id);
        if (row is not null) apps.Remove(row);
        return true;
    });

    private static JsonArray Apps(JsonObject json)
    {
        if (json["apps"] is JsonArray apps) return apps;
        if (json["apps"] is not null) throw new InvalidDataException("The app library could not be read.");
        json["apps"] = new JsonArray();
        return (JsonArray)json["apps"]!;
    }
}
