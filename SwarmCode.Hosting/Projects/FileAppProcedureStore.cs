using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwarmCode.Hosting.Settings;
using SwarmCore.Agent.Procedures;

namespace SwarmCode.Hosting.Projects;

/// <summary>Host persistence for the shared procedure engine. CAS protects revisions across independent hosts.</summary>
public sealed class FileAppProcedureStore(SwarmCodeDataPaths paths) : IAgentProcedureStore
{
    private string DirectoryPath => Path.Combine(paths.RootDirectory, "app-procedures");
    private string FilePath(AgentProcedureKey key) => Path.Combine(DirectoryPath,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(key)))) + ".json");

    public Task<AgentProcedureHistory?> GetAsync(AgentProcedureKey key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var json = SettingsJsonFile.Read(FilePath(key));
        return Task.FromResult(json["history"]?.Deserialize<AgentProcedureHistory>(SettingsJsonFile.Json));
    }
    public Task<bool> CompareExchangeAsync(AgentProcedureKey key, long expectedVersion, AgentProcedureHistory replacement, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (replacement.Key != key || replacement.Version != expectedVersion + 1) throw new ArgumentException("Invalid procedure revision.");
        return Task.FromResult(SettingsJsonFile.Mutate(FilePath(key), json =>
        {
            var current = json["history"]?.Deserialize<AgentProcedureHistory>(SettingsJsonFile.Json);
            if ((current?.Version ?? 0) != expectedVersion) return false;
            json["history"] = JsonSerializer.SerializeToNode(replacement, SettingsJsonFile.Json);
            return true;
        }));
    }
    public Task<IReadOnlyList<AgentProcedureHistory>> ListAsync(string profileId, string agentId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<AgentProcedureHistory> rows = !Directory.Exists(DirectoryPath) ? [] : Directory.EnumerateFiles(DirectoryPath, "*.json")
            .Select(p => SettingsJsonFile.Read(p)["history"]?.Deserialize<AgentProcedureHistory>(SettingsJsonFile.Json))
            .Where(h => h?.Key.ProfileId == profileId && h.Key.AgentId == agentId).Cast<AgentProcedureHistory>().ToArray();
        return Task.FromResult(rows);
    }

    public IDisposable AcquireRunLease(string appId)
    {
        if (!Guid.TryParseExact(appId, "N", out _)) throw new ArgumentException("Invalid app identity.");
        Directory.CreateDirectory(DirectoryPath);
        try { return new FileStream(Path.Combine(DirectoryPath, appId + ".running"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new InvalidOperationException("This app is already starting or running in another Talavine Code window."); }
    }
}
