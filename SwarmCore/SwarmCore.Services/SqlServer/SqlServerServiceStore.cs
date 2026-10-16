using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SwarmCore.Services.Abstractions;

namespace SwarmCore.Services.SqlServer;

/// <summary>
/// Configuration for the SQL Server-backed service catalog store. Mirrors
/// <c>SqlServerVibeStoreOptions</c> (SwarmCore.Vibes): a static connection string for
/// single-database hosts, or a per-operation <see cref="ConnectionStringFactory"/> for a
/// multi-tenant host (e.g. CRM) where each tenant has its own database.
/// </summary>
public sealed class SqlServerServiceStoreOptions
{
    /// <summary>A static SQL Server connection string. Used when <see cref="ConnectionStringFactory"/> is not set.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// A factory that returns the connection string to use for the current operation.
    /// When set, this WINS over <see cref="ConnectionString"/> — this is how a multi-tenant
    /// CRM injects the active tenant's database connection string per request.
    /// </summary>
    public Func<string>? ConnectionStringFactory { get; set; }

    /// <summary>Name of the table holding service definitions.</summary>
    public string Table { get; set; } = "swarm_services";

    public string ResolveConnectionString()
    {
        var cs = ConnectionStringFactory?.Invoke();
        if (string.IsNullOrWhiteSpace(cs)) cs = ConnectionString;
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException(
                "No SQL Server connection string is available. Set SqlServerServiceStoreOptions.ConnectionString or ConnectionStringFactory.");
        return cs!;
    }
}

/// <summary>
/// SQL Server implementation of <see cref="IServiceStore"/>. Unlike the single-connection
/// <c>SqliteServiceStore</c>, this store opens a pooled <see cref="SqlConnection"/> per
/// operation from the resolved connection string and disposes it, so it is safe for
/// per-tenant / per-request use in a multi-tenant CRM backed by Azure SQL — one row per
/// service, the definition stored as JSON, matching the SQLite store's shape exactly.
/// </summary>
public sealed class SqlServerServiceStore : IServiceStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SqlServerServiceStoreOptions _options;

    /// <summary>Connection strings whose table has already been ensured this process.
    /// Keeps init lazy + idempotent + per-tenant (each tenant database is ensured once).</summary>
    private readonly ConcurrentDictionary<string, byte> _ensured = new(StringComparer.Ordinal);

    public SqlServerServiceStore(SqlServerServiceStoreOptions options) =>
        _options = options ?? throw new ArgumentNullException(nameof(options));

    private string Table => "[" + _options.Table + "]";

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(_options.ResolveConnectionString());
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await EnsureTableAsync(conn, ct).ConfigureAwait(false);
    }

    private async Task EnsureTableAsync(SqlConnection conn, CancellationToken ct)
    {
        var key = conn.ConnectionString;
        if (_ensured.ContainsKey(key)) return;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = '{_options.Table}')
CREATE TABLE {Table} (
    id NVARCHAR(450) PRIMARY KEY,
    name NVARCHAR(450) NOT NULL,
    kind INT NOT NULL,
    json NVARCHAR(MAX) NOT NULL,
    updated_utc NVARCHAR(64) NOT NULL
);";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _ensured.TryAdd(key, 1);
    }

    private async Task EnsureInitAsync(SqlConnection conn, CancellationToken ct)
    {
        if (!_ensured.ContainsKey(conn.ConnectionString))
            await EnsureTableAsync(conn, ct).ConfigureAwait(false);
    }

    public async Task SaveAsync(ServiceDefinition service, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        service.UpdatedUtc = DateTime.UtcNow;
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await EnsureInitAsync(conn, ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
MERGE {Table} AS target
USING (SELECT @id AS id) AS src ON target.id = src.id
WHEN MATCHED THEN UPDATE SET name=@name, kind=@kind, json=@json, updated_utc=@updated
WHEN NOT MATCHED THEN INSERT (id, name, kind, json, updated_utc) VALUES (@id, @name, @kind, @json, @updated);";
        cmd.Parameters.AddWithValue("@id", service.Id);
        cmd.Parameters.AddWithValue("@name", service.Name);
        cmd.Parameters.AddWithValue("@kind", (int)service.Kind);
        cmd.Parameters.AddWithValue("@json", JsonSerializer.Serialize(service, Json));
        cmd.Parameters.AddWithValue("@updated", service.UpdatedUtc.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public Task<ServiceDefinition?> GetAsync(string id, CancellationToken ct = default) => QueryOneAsync("id", id, ct);
    public Task<ServiceDefinition?> GetByNameAsync(string name, CancellationToken ct = default) => QueryOneAsync("name", name, ct);

    private async Task<ServiceDefinition?> QueryOneAsync(string column, string value, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await EnsureInitAsync(conn, ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT TOP 1 json FROM {Table} WHERE {column} = @v";
        cmd.Parameters.AddWithValue("@v", value);
        var json = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        return json is null ? null : JsonSerializer.Deserialize<ServiceDefinition>(json, Json);
    }

    public async Task<IReadOnlyList<ServiceDefinition>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await EnsureInitAsync(conn, ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT json FROM {Table} ORDER BY name";
        var list = new List<ServiceDefinition>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var svc = JsonSerializer.Deserialize<ServiceDefinition>(reader.GetString(0), Json);
            if (svc is not null) list.Add(svc);
        }
        return list;
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await EnsureInitAsync(conn, ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {Table} WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
