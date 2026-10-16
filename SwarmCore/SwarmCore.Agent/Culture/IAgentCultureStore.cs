using System.Text.Json;
using System.Text.Json.Serialization;
using SwarmCore.Abstractions.Processes;

namespace SwarmCore.Agent.Culture;

/// <summary>
/// Host-owned persistence for the shared <see cref="AgentCultureProfile"/>.
/// The desktop app saves it; worker hosts read it. Null means nothing has been
/// saved yet, and the charter falls back to its defaults.
/// </summary>
public interface IAgentCultureStore
{
    Task<AgentCultureProfile?> GetAsync(CancellationToken ct = default);

    Task SaveAsync(AgentCultureProfile profile, CancellationToken ct = default);
}

/// <summary>
/// Resolves a default principal when no culture has been saved — typically the
/// profile owner's contact card or a tenant's company name. Hosts register
/// one; without it the charter says "the person you work for".
/// </summary>
public interface IAgentCulturePrincipalResolver
{
    Task<AgentCulturePrincipal?> ResolveAsync(CancellationToken ct = default);
}

public sealed record AgentCulturePrincipal(string Name, AgentCulturePrincipalKind Kind);

/// <summary>No stored culture; the charter renders its defaults.</summary>
public sealed class NullAgentCultureStore : IAgentCultureStore
{
    public Task<AgentCultureProfile?> GetAsync(CancellationToken ct = default) =>
        Task.FromResult<AgentCultureProfile?>(null);

    public Task SaveAsync(AgentCultureProfile profile, CancellationToken ct = default) =>
        throw new InvalidOperationException("This host does not persist an agent culture profile.");
}

/// <summary>Where the culture file lives inside a profile directory.</summary>
public static class AgentCulturePaths
{
    public static string GetPath(string profileRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileRoot);
        return Path.Combine(Path.GetFullPath(profileRoot), "agents", "culture.json");
    }
}

/// <summary>
/// One JSON document per profile, published atomically so a worker host
/// reading while the app saves always sees a complete old or new document.
/// Reads are cached against the file's last-write time.
/// </summary>
public sealed class FileAgentCultureStore : IAgentCultureStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _gate = new();
    private DateTime _cachedWriteUtc;
    private AgentCultureProfile? _cached;
    private bool _cacheValid;

    public FileAgentCultureStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public string FilePath => _path;

    public Task<AgentCultureProfile?> GetAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                _cached = null;
                _cacheValid = true;
                _cachedWriteUtc = default;
                return Task.FromResult<AgentCultureProfile?>(null);
            }

            var writeUtc = File.GetLastWriteTimeUtc(_path);
            if (_cacheValid && writeUtc == _cachedWriteUtc)
            {
                return Task.FromResult(_cached);
            }

            _cached = AtomicJsonFile.TryRead<AgentCultureProfile>(_path, out var value, JsonOptions)
                ? value?.Normalize()
                : null;
            _cachedWriteUtc = writeUtc;
            _cacheValid = true;
            return Task.FromResult(_cached);
        }
    }

    public Task SaveAsync(AgentCultureProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ct.ThrowIfCancellationRequested();
        var normalized = profile.Normalize() with { UpdatedUtc = DateTimeOffset.UtcNow };
        lock (_gate)
        {
            AtomicJsonFile.Write(_path, normalized, JsonOptions);
            _cached = normalized;
            _cachedWriteUtc = File.GetLastWriteTimeUtc(_path);
            _cacheValid = true;
        }

        return Task.CompletedTask;
    }
}
