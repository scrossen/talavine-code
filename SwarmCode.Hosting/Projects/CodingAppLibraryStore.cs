using System.Text.Json;
using System.Text.Json.Nodes;
using SwarmCode.Hosting.Settings;
using SwarmCore.Coding.Projects;

namespace SwarmCode.Hosting.Projects;

/// <summary>App-local metadata only. Field-owned mutations under a cross-process lease; no project data is deleted.</summary>
public sealed class CodingAppLibraryStore(SwarmCodeDataPaths paths)
{
    private string FilePath => Path.Combine(paths.RootDirectory, "apps.json");
    public CodingAppLibrary Read()
    {
        var json = SettingsJsonFile.Read(FilePath);
        return new(json["appsFolder"]?.GetValue<string>(), json["apps"]?.Deserialize<CodingAppProject[]>(SettingsJsonFile.Json) ?? []);
    }

    public void SetFolder(string folder, string? expectedFolder)
    {
        var full = CodingAppPaths.Normalize(folder);
        expectedFolder = expectedFolder is null ? null : CodingAppPaths.Normalize(expectedFolder);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("Choose an existing folder for your apps.");
        SettingsJsonFile.Mutate(FilePath, json =>
        {
            if (!string.Equals(json["appsFolder"]?.GetValue<string>(), expectedFolder, CodingAppPaths.Comparison))
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
            var folder = json["appsFolder"]?.GetValue<string>();
            if (!string.Equals(folder, expectedFolder, CodingAppPaths.Comparison)) throw new InvalidOperationException("Your apps folder changed. Reload before creating an app.");
            if (folder is null || !Directory.Exists(folder)) throw new DirectoryNotFoundException("Choose your apps folder first.");
            var root = CodingAppPaths.Within(folder, name);
            if (Directory.Exists(root) || File.Exists(root)) throw new InvalidOperationException("That folder already exists. Choose another app name or use Add an app.");
            var apps = Apps(json);
            Directory.CreateDirectory(root);
            var app = new CodingAppProject(Guid.NewGuid().ToString("N"), name, description.Trim(), root, DateTime.UtcNow);
            apps.Add(JsonSerializer.SerializeToNode(app, SettingsJsonFile.Json));
            return app;
        });
    }

    public CodingAppProject Add(string folder)
    {
        var root = CodingAppPaths.Normalize(folder);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("This app folder is missing. Choose its current location.");
        return SettingsJsonFile.Mutate(FilePath, json =>
        {
            var apps = Apps(json);
            var existing = apps.Deserialize<CodingAppProject[]>(SettingsJsonFile.Json)!.FirstOrDefault(a => CodingAppPaths.Same(a.Root, root));
            if (existing is not null) return existing;
            var name = Path.GetFileName(root);
            var app = new CodingAppProject(Guid.NewGuid().ToString("N"), string.IsNullOrWhiteSpace(name) ? root : name, "Existing project", root, DateTime.UtcNow);
            apps.Add(JsonSerializer.SerializeToNode(app, SettingsJsonFile.Json));
            return app;
        });
    }

    public CodingAppProject Get(string id) => Read().Apps.SingleOrDefault(a => a.Id == id)
        ?? throw new InvalidOperationException("This app is no longer in your library. Refresh My apps.");

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
