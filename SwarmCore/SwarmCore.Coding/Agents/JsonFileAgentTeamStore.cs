using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwarmCore.Coding.Agents;

/// <summary>
/// One JSON file per agent under a host directory, replaced atomically. The team writes only records its own
/// instance owns, so two host processes over one data directory never overwrite each other's agents.
/// </summary>
public sealed class JsonFileAgentTeamStore(string directory) : IAgentTeamStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Directory { get; } = Path.GetFullPath(directory);

    public async Task<IReadOnlyList<AgentRecord>> LoadAsync(CancellationToken ct)
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        var records = new List<AgentRecord>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(file);
                if (await JsonSerializer.DeserializeAsync<AgentRecord>(stream, Json, ct).ConfigureAwait(false) is { } record
                    && AgentIds.IsValid(record.Id) && Path.GetFileNameWithoutExtension(file) == record.Id)
                    records.Add(record);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { /* skip an unreadable record */ }
        }
        return records;
    }

    public async Task SaveAsync(AgentRecord record, CancellationToken ct)
    {
        var path = PathFor(record.Id);
        System.IO.Directory.CreateDirectory(Directory);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, record, Json, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    public Task DeleteAsync(string id, CancellationToken ct)
    {
        var path = PathFor(id);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string PathFor(string id)
    {
        if (!AgentIds.IsValid(id)) throw new ArgumentException("Invalid agent id.", nameof(id));
        return Path.Combine(Directory, id + ".json");
    }
}

/// <summary>Agent ids are conversation ids: 1-128 ASCII letters, digits, '-', '_' or '.', no leading '.', no "..".</summary>
public static class AgentIds
{
    public static bool IsValid(string? id)
        => !string.IsNullOrEmpty(id) && id.Length <= 128 && id[0] != '.' && !id.Contains("..", StringComparison.Ordinal)
           && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
