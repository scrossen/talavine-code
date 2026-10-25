using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SwarmCore.Secrets;

/// <summary>
/// <see cref="ISecretVault"/> over one SQLite database file. Every value, login username, and
/// note is protected by the <see cref="ISecretProtector"/> before it is written, so the file
/// holds envelope ciphertext bound to this OS user and device; the database and ordinary
/// backups of it stay unencrypted, matching the Swarm family's data-at-rest posture. Names,
/// kinds, sites, masked username hints, and timestamps are plain so a host can list what
/// exists without unlocking anything.
///
/// Opening a connection per operation keeps the vault safe to share between the console
/// host, the desktop app, and the MCP bridge on the same data directory; SQLite's file
/// locking (WAL, busy timeout) serializes writers. All work runs off the caller's thread so a
/// UI dispatcher never blocks on the database. A failed schema initialization (for example a
/// transient lock while another process creates the file) is retried on the next call.
/// </summary>
public sealed class SqliteSecretVault : ISecretVault, IConditionalSecretVault
{
    private const int SchemaVersion = 2;

    private readonly string _connectionString;
    private readonly ISecretProtector _protector;
    private readonly ILogger<SqliteSecretVault> _logger;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private volatile bool _initialized;

    public SqliteSecretVault(string databasePath, ISecretProtector protector, ILogger<SqliteSecretVault>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("A database path is required.", nameof(databasePath));
        DatabasePath = Path.GetFullPath(databasePath);
        _protector = protector;
        _logger = logger ?? NullLogger<SqliteSecretVault>.Instance;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
    }

    public string DatabasePath { get; }

    public async Task<SecretEntry> SetAsync(SecretWriteRequest request, CancellationToken ct = default)
        => (await WriteAsync(request, replace: true, ct).ConfigureAwait(false)).Entry;

    public async Task<bool> TryCreateAsync(SecretWriteRequest request, CancellationToken ct = default)
        => (await WriteAsync(request, replace: false, ct).ConfigureAwait(false)).Written;

    private async Task<(SecretEntry Entry, bool Written)> WriteAsync(SecretWriteRequest request, bool replace, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.Value)) throw new ArgumentException("A secret value is required.", nameof(request));
        var name = SecretNames.Normalize(request.Name);
        var kind = string.IsNullOrWhiteSpace(request.Kind) ? SecretKinds.Generic : request.Kind.Trim().ToLowerInvariant();
        var site = string.IsNullOrWhiteSpace(request.Site) ? null : request.Site.Trim();
        var notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        var username = string.IsNullOrWhiteSpace(request.Username) ? null : request.Username.Trim();

        // Protect before touching the database so plaintext never sits in a command parameter longer than needed.
        // Notes are protected too: password-manager exports put recovery codes and TOTP seeds there.
        var protectedValue = _protector.Protect(request.Value);
        var protectedUsername = username is null ? null : _protector.Protect(username);
        var protectedNotes = notes is null ? null : _protector.Protect(notes);
        var hint = SecretNames.MaskUsername(username);

        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            using var connection = Open();
            var now = Stamp(DateTime.UtcNow);
            bool written;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO secrets(name, kind, protected_value, protected_username, username_hint, site, notes, created_utc, updated_utc, last_used_utc)
                    VALUES ($name, $kind, $value, $username, $hint, $site, $notes, $now, $now, NULL)
                    """ + (replace ? """
                    ON CONFLICT(name) DO UPDATE SET
                        kind = excluded.kind,
                        protected_value = excluded.protected_value,
                        protected_username = excluded.protected_username,
                        username_hint = excluded.username_hint,
                        site = excluded.site,
                        notes = excluded.notes,
                        updated_utc = excluded.updated_utc;
                    """ : "ON CONFLICT(name) DO NOTHING;");
                cmd.Parameters.AddWithValue("$name", name);
                cmd.Parameters.AddWithValue("$kind", kind);
                cmd.Parameters.AddWithValue("$value", protectedValue);
                cmd.Parameters.AddWithValue("$username", (object?)protectedUsername ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$hint", (object?)hint ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$site", (object?)site ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$notes", (object?)protectedNotes ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$now", now);
                written = cmd.ExecuteNonQuery() == 1;
            }
            return (ReadEntry(connection, name) ?? throw new InvalidOperationException("The secret was not persisted."), written);
        }, ct).ConfigureAwait(false);
    }

    public async Task<bool> TrySetAsync(SecretWriteRequest request, SecretEntry? expected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (expected is null) return await TryCreateAsync(request, ct).ConfigureAwait(false);
        var name = SecretNames.Normalize(request.Name);
        if (name != expected.Name) throw new ArgumentException("The secret identity changed.");
        if (string.IsNullOrEmpty(request.Value)) throw new ArgumentException("A secret value is required.");
        var value = _protector.Protect(request.Value);
        var username = string.IsNullOrWhiteSpace(request.Username) ? null : _protector.Protect(request.Username.Trim());
        var notes = string.IsNullOrWhiteSpace(request.Notes) ? null : _protector.Protect(request.Notes.Trim());
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            using var connection = Open(); using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                UPDATE secrets SET kind=$kind, protected_value=$value, protected_username=$username,
                    username_hint=$hint, site=$site, notes=$notes, updated_utc=$now
                WHERE name=$name AND created_utc=$created AND updated_utc=$updated;
                """;
            cmd.Parameters.AddWithValue("$name",name); cmd.Parameters.AddWithValue("$value",value);
            cmd.Parameters.AddWithValue("$kind",string.IsNullOrWhiteSpace(request.Kind) ? SecretKinds.Generic : request.Kind.Trim().ToLowerInvariant());
            cmd.Parameters.AddWithValue("$username",(object?)username ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$hint",(object?)SecretNames.MaskUsername(request.Username) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$site",(object?)request.Site ?? DBNull.Value); cmd.Parameters.AddWithValue("$notes",(object?)notes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created",Stamp(expected.CreatedUtc)); cmd.Parameters.AddWithValue("$updated",Stamp(expected.UpdatedUtc));
            cmd.Parameters.AddWithValue("$now",Stamp(DateTime.UtcNow));
            return cmd.ExecuteNonQuery() == 1;
        },ct).ConfigureAwait(false);
    }

    public async Task<bool> TryDeleteAsync(SecretEntry expected, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            using var connection = Open(); using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM secrets WHERE name=$name AND created_utc=$created AND updated_utc=$updated;";
            cmd.Parameters.AddWithValue("$name",expected.Name); cmd.Parameters.AddWithValue("$created",Stamp(expected.CreatedUtc)); cmd.Parameters.AddWithValue("$updated",Stamp(expected.UpdatedUtc));
            return cmd.ExecuteNonQuery() == 1;
        },ct).ConfigureAwait(false);
    }

    public async Task<string?> GetValueAsync(string name, CancellationToken ct = default)
    {
        if (!SecretNames.TryNormalize(name, out var normalized)) return null;
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT protected_value FROM secrets WHERE name = $name;";
            cmd.Parameters.AddWithValue("$name", normalized);
            var stored = cmd.ExecuteScalar() as string;
            if (stored is null) return null;
            var value = _protector.Unprotect(stored);
            if (value.Length == 0)
            {
                _logger.LogWarning("Secret '{Name}' exists but could not be unprotected on this host; it needs re-entry.", normalized);
                return null;
            }
            TouchUsed(connection, normalized);
            return value;
        }, ct).ConfigureAwait(false);
    }

    public async Task<SecretLogin?> GetLoginAsync(string name, CancellationToken ct = default)
    {
        if (!SecretNames.TryNormalize(name, out var normalized)) return null;
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT protected_value, protected_username FROM secrets WHERE name = $name;";
            cmd.Parameters.AddWithValue("$name", normalized);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            var password = _protector.Unprotect(reader.GetString(0));
            if (password.Length == 0)
            {
                _logger.LogWarning("Secret '{Name}' exists but could not be unprotected on this host; it needs re-entry.", normalized);
                return null;
            }
            var username = reader.IsDBNull(1) ? null : _protector.Unprotect(reader.GetString(1));
            reader.Close();
            TouchUsed(connection, normalized);
            return new SecretLogin(normalized, string.IsNullOrEmpty(username) ? null : username, password);
        }, ct).ConfigureAwait(false);
    }

    public async Task<string?> GetNotesAsync(string name, CancellationToken ct = default)
    {
        if (!SecretNames.TryNormalize(name, out var normalized)) return null;
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT notes FROM secrets WHERE name = $name;";
            cmd.Parameters.AddWithValue("$name", normalized);
            if (cmd.ExecuteScalar() is not string stored) return null;
            var notes = UnprotectNotes(stored);
            if (notes is not null) TouchUsed(connection, normalized);
            return notes;
        }, ct).ConfigureAwait(false);
    }

    public async Task<SecretEntry?> GetEntryAsync(string name, CancellationToken ct = default)
    {
        if (!SecretNames.TryNormalize(name, out var normalized)) return null;
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            using var connection = Open();
            return ReadEntry(connection, normalized);
        }, ct).ConfigureAwait(false);
    }

    public async Task<bool> HasAsync(string name, CancellationToken ct = default)
        => await GetEntryAsync(name, ct).ConfigureAwait(false) is not null;

    public async Task<bool> DeleteAsync(string name, CancellationToken ct = default)
    {
        if (!SecretNames.TryNormalize(name, out var normalized)) return false;
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM secrets WHERE name = $name;";
            cmd.Parameters.AddWithValue("$name", normalized);
            return cmd.ExecuteNonQuery() > 0;
        }, ct).ConfigureAwait(false);
    }

    public async Task<bool> TrySetSiteAsync(string name, string site, bool onlyWhenUnset = false, CancellationToken ct = default)
    {
        if (!SecretNames.TryNormalize(name, out var normalized) || string.IsNullOrWhiteSpace(site)) return false;
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            using var connection = Open();
            using var cmd = connection.CreateCommand();
            // One statement: a concurrent first use or edit cannot replace a site set in between.
            cmd.CommandText = onlyWhenUnset
                ? "UPDATE secrets SET site = $site, updated_utc = $now WHERE name = $name AND (site IS NULL OR trim(site) = '');"
                : "UPDATE secrets SET site = $site, updated_utc = $now WHERE name = $name;";
            cmd.Parameters.AddWithValue("$name", normalized);
            cmd.Parameters.AddWithValue("$site", site.Trim());
            cmd.Parameters.AddWithValue("$now", Stamp(DateTime.UtcNow));
            return cmd.ExecuteNonQuery() == 1;
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SecretEntry>> ListAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = EntryColumns + " FROM secrets ORDER BY name;";
            using var reader = cmd.ExecuteReader();
            var list = new List<SecretEntry>();
            while (reader.Read()) list.Add(Map(reader));
            return (IReadOnlyList<SecretEntry>)list;
        }, ct).ConfigureAwait(false);
    }

    private const string EntryColumns =
        "SELECT name, kind, site, username_hint, notes, created_utc, updated_utc, last_used_utc, protected_value";

    private SecretEntry? ReadEntry(SqliteConnection connection, string name)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = EntryColumns + " FROM secrets WHERE name = $name;";
        cmd.Parameters.AddWithValue("$name", name);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    private SecretEntry Map(SqliteDataReader r) => new(
        Name: r.GetString(0),
        Kind: r.GetString(1),
        Site: r.IsDBNull(2) ? null : r.GetString(2),
        UsernameHint: r.IsDBNull(3) ? null : r.GetString(3),
        // Metadata only: notes stay protected until GetNotesAsync asks for them.
        HasNotes: !r.IsDBNull(4),
        CreatedUtc: Parse(r.GetString(5)),
        UpdatedUtc: Parse(r.GetString(6)),
        LastUsedUtc: r.IsDBNull(7) ? null : Parse(r.GetString(7)),
        IsEncrypted: EnvelopeSecretProtector.IsEnvelope(r.GetString(8)));

    /// <summary>Notes written before schema 2 were plain text; protected notes carry the envelope or plaintext marker.</summary>
    private string? UnprotectNotes(string stored)
    {
        if (stored.StartsWith(EnvelopeSecretProtector.EnvelopeMarker, StringComparison.Ordinal) || stored.StartsWith(EnvelopeSecretProtector.PlaintextMarker, StringComparison.Ordinal))
        {
            var notes = _protector.Unprotect(stored);
            return notes.Length == 0 ? null : notes;
        }
        return stored;
    }

    private static void TouchUsed(SqliteConnection connection, string name)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE secrets SET last_used_utc = $now WHERE name = $name;";
        cmd.Parameters.AddWithValue("$now", Stamp(DateTime.UtcNow));
        cmd.Parameters.AddWithValue("$name", name);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            await Task.Run(Initialize, ct).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    private void Initialize()
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using var connection = Open();
        using (var journal = connection.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode = WAL;";
            journal.ExecuteScalar();
        }
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS vault_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS secrets(
                name TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                protected_value TEXT NOT NULL,
                protected_username TEXT,
                username_hint TEXT,
                site TEXT,
                notes TEXT,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                last_used_utc TEXT);
            CREATE INDEX IF NOT EXISTS ix_secrets_site ON secrets(site);
            INSERT INTO vault_meta(key, value) VALUES ('schema_version', $version)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$version", SchemaVersion.ToString(CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    private static string Stamp(DateTime utc) => utc.ToString("O", CultureInfo.InvariantCulture);

    private static DateTime Parse(string stamp)
        => DateTime.Parse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
