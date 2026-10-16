using System.Text.Json;
using SwarmCore.Services.Abstractions;

namespace SwarmCore.Services.Credentials;

/// <summary>
/// Default <see cref="ICredentialStore"/>: a JSON file of secrets kept apart from service
/// definitions. Plaintext-on-disk, local-single-user posture (matches how each host already
/// stores its own local settings); OS-level encryption is a sensible follow-up if a host
/// needs it. Every host needs essentially this same store, so it lives here instead of
/// being reimplemented per app — hosts with existing bespoke implementations can keep them.
/// </summary>
public sealed class JsonFileCredentialStore : ICredentialStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, string> _secrets;

    public JsonFileCredentialStore(string filePath)
    {
        _path = filePath;
        _secrets = Load(_path);
    }

    private static Dictionary<string, string> Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
        }
        catch { /* start fresh on corruption */ }
        return new();
    }

    private void Save()
    {
        lock (_lock)
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(_secrets));
        }
    }

    public Task SetAsync(string name, string secret, CancellationToken ct = default)
    {
        lock (_lock) _secrets[name] = secret;
        Save();
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string name, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(_secrets.GetValueOrDefault(name));
    }

    public Task<bool> HasAsync(string name, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(_secrets.ContainsKey(name));
    }

    public Task DeleteAsync(string name, CancellationToken ct = default)
    {
        lock (_lock) { if (!_secrets.Remove(name)) return Task.CompletedTask; }
        Save();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListNamesAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<string>>(
                _secrets.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList());
        }
    }
}
