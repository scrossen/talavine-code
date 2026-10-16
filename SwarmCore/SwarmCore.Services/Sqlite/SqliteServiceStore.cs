using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SwarmCore.Services.Abstractions;

namespace SwarmCore.Services.Sqlite;

public sealed class SqliteServiceStoreOptions
{
    public string? ConnectionString { get; set; }
    public string? DatabasePath { get; set; }
    public string Table { get; set; } = "services";
    /// <summary>Require the exact loaded baseline for updates; reject competing writers and delete-then-save.</summary>
    public bool RequireReadBaseline { get; set; }

    internal string Resolve() =>
        !string.IsNullOrWhiteSpace(ConnectionString) ? ConnectionString!
        : $"Data Source={(string.IsNullOrWhiteSpace(DatabasePath) ? "services.db" : DatabasePath)}";
}

/// <summary>SQLite-backed <see cref="IServiceStore"/> (one row per service, definition stored as JSON).</summary>
public sealed class SqliteServiceStore : IServiceStore, IAsyncDisposable, IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SqliteServiceStoreOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _conn;
    private bool _init;
    private sealed record Baseline(string Id, string Json);
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ServiceDefinition, Baseline> _baselines = new();

    public SqliteServiceStore(SqliteServiceStoreOptions options) => _options = options;

    private async Task<SqliteConnection> ConnAsync(CancellationToken ct)
    {
        if (_conn is { State: ConnectionState.Open }) return _conn;
        _conn = new SqliteConnection(_options.Resolve());
        await _conn.OpenAsync(ct).ConfigureAwait(false);
        return _conn;
    }

    private string Table => "\"" + _options.Table + "\"";

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_init) return;
            var conn = await ConnAsync(ct).ConfigureAwait(false);
            await Exec(conn, $@"CREATE TABLE IF NOT EXISTS {Table} (
                id TEXT PRIMARY KEY, name TEXT NOT NULL, kind INTEGER NOT NULL,
                json TEXT NOT NULL, updated_utc TEXT NOT NULL);", ct).ConfigureAwait(false);
            if (_options.RequireReadBaseline)
                await Exec(conn, $"CREATE UNIQUE INDEX IF NOT EXISTS \"{_options.Table}_unique_name\" ON {Table}(name COLLATE NOCASE);", ct).ConfigureAwait(false);
            _init = true;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(ServiceDefinition service, CancellationToken ct = default)
    {
        await EnsureInit(ct).ConfigureAwait(false);
        service.UpdatedUtc = DateTime.UtcNow;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = await ConnAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            _baselines.TryGetValue(service, out var baseline);
            if (_options.RequireReadBaseline && baseline is not null && baseline.Id != service.Id)
                throw new InvalidOperationException("A service's identity cannot change after loading.");
            var json = JsonSerializer.Serialize(service, Json);
            cmd.CommandText = _options.RequireReadBaseline
                ? baseline is null
                    ? $"INSERT INTO {Table} (id,name,kind,json,updated_utc) VALUES ($id,$name,$kind,$json,$u);"
                    : $"UPDATE {Table} SET name=$name,kind=$kind,json=$json,updated_utc=$u WHERE id=$id AND json=$baseline;"
                : $@"INSERT INTO {Table} (id,name,kind,json,updated_utc)
                VALUES ($id,$name,$kind,$json,$u)
                ON CONFLICT(id) DO UPDATE SET name=excluded.name, kind=excluded.kind, json=excluded.json, updated_utc=excluded.updated_utc;";
            cmd.Parameters.AddWithValue("$id", service.Id);
            cmd.Parameters.AddWithValue("$name", service.Name);
            cmd.Parameters.AddWithValue("$kind", (int)service.Kind);
            cmd.Parameters.AddWithValue("$json", json);
            cmd.Parameters.AddWithValue("$u", service.UpdatedUtc.ToString("o"));
            if (_options.RequireReadBaseline && baseline is not null) cmd.Parameters.AddWithValue("$baseline", baseline.Json);
            if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("The service changed or was deleted. Reload it before saving.");
            _baselines.AddOrUpdate(service, new(service.Id, json));
        }
        finally { _gate.Release(); }
    }

    public Task<ServiceDefinition?> GetAsync(string id, CancellationToken ct = default) => QueryOne("id", id, ct);
    public Task<ServiceDefinition?> GetByNameAsync(string name, CancellationToken ct = default) => QueryOne("name", name, ct);

    private async Task<ServiceDefinition?> QueryOne(string col, string value, CancellationToken ct)
    {
        await EnsureInit(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = await ConnAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT json FROM {Table} WHERE {col} = $v COLLATE NOCASE LIMIT 1";
            cmd.Parameters.AddWithValue("$v", value);
            var json = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            return json is null ? null : Read(json);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ServiceDefinition>> ListAsync(CancellationToken ct = default)
    {
        await EnsureInit(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = await ConnAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT json FROM {Table} ORDER BY name";
            var list = new List<ServiceDefinition>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var svc = Read(reader.GetString(0));
                if (svc is not null) list.Add(svc);
            }
            return list;
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await EnsureInit(ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var conn = await ConnAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DELETE FROM {Table} WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureInit(CancellationToken ct) { if (!_init) await InitializeAsync(ct).ConfigureAwait(false); }

    private ServiceDefinition? Read(string json)
    {
        var service = JsonSerializer.Deserialize<ServiceDefinition>(json, Json);
        if (service is not null) _baselines.AddOrUpdate(service, new(service.Id, json));
        return service;
    }

    private static async Task Exec(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() { if (_conn is not null) await _conn.DisposeAsync().ConfigureAwait(false); _gate.Dispose(); }
    public void Dispose() { _conn?.Dispose(); _gate.Dispose(); }
}
