using Microsoft.Data.Sqlite;
using System.Text.Json;
using SwarmCore.Coding.Routing;
using SwarmCore.PersonalMemory;

namespace SwarmCode.Hosting.Memory;

/// <summary>Append-only, profile-scoped evidence. SQLite commits row identity and payload atomically across hosts.
/// No detached aggregate is ever written back. SQLite work runs on a worker thread.</summary>
public sealed class SqliteModelResponseMemory(SwarmCodeDataPaths paths) : IModelResponseMemory
{
    private static readonly PersonalMemoryWriteGate Writes = new();

    public async Task AppendAsync(ModelResponseObservation observation, CancellationToken ct = default)
    {
        // Await contention before scheduling SQLite work. Blocking many worker threads
        // in SQLite's busy wait can starve the writer that must release its transaction.
        var path = Path.GetFullPath(paths.ModelResponseMemoryPath);
        var key = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? path.ToUpperInvariant() : path;
        using var lease = await Writes.AcquireAsync(key, ct).ConfigureAwait(false);
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (!Valid(observation))
                throw new ArgumentException("Model response observation identity is incomplete.");
            Directory.CreateDirectory(paths.MemoryDirectory);
            using var connection = Open();
            using var setup = connection.CreateCommand();
            setup.CommandText = "CREATE TABLE IF NOT EXISTS ModelResponses (ScopeId TEXT NOT NULL, Id TEXT NOT NULL, Payload TEXT NOT NULL, PRIMARY KEY(ScopeId, Id))";
            setup.ExecuteNonQuery();
            using var transaction = connection.BeginTransaction();
            if (observation.SupersedesId is { } priorId)
            {
                using var prior = connection.CreateCommand();
                prior.Transaction = transaction;
                prior.CommandText = "SELECT Payload FROM ModelResponses WHERE ScopeId=$scope AND Id=$id";
                prior.Parameters.AddWithValue("$scope", observation.ScopeId);
                prior.Parameters.AddWithValue("$id", priorId);
                var previous = prior.ExecuteScalar() is string previousPayload
                    ? JsonSerializer.Deserialize<ModelResponseObservation>(previousPayload) : null;
                if (previous is null || previous.Provider != observation.Provider || previous.Model != observation.Model
                    || previous.TaskFingerprint != observation.TaskFingerprint || previous.ObservedUtc > observation.ObservedUtc)
                    throw new InvalidOperationException("A correction must reference an existing observation of the same request and model.");
                using var competing = connection.CreateCommand();
                competing.Transaction = transaction;
                competing.CommandText = "SELECT COUNT(*) FROM ModelResponses WHERE ScopeId=$scope AND Id<>$id AND json_extract(Payload,'$.SupersedesId')=$prior";
                competing.Parameters.AddWithValue("$scope", observation.ScopeId);
                competing.Parameters.AddWithValue("$id", observation.Id);
                competing.Parameters.AddWithValue("$prior", priorId);
                if (Convert.ToInt64(competing.ExecuteScalar()) != 0)
                    throw new InvalidOperationException("This observation already has a correction; review the current revision.");
            }
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO ModelResponses(ScopeId,Id,Payload) VALUES($scope,$id,$payload)";
            var payload = JsonSerializer.Serialize(observation);
            insert.Parameters.AddWithValue("$scope", observation.ScopeId);
            insert.Parameters.AddWithValue("$id", observation.Id);
            insert.Parameters.AddWithValue("$payload", payload);
            insert.ExecuteNonQuery();
            using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = "SELECT Payload FROM ModelResponses WHERE ScopeId=$scope AND Id=$id";
            read.Parameters.AddWithValue("$scope", observation.ScopeId);
            read.Parameters.AddWithValue("$id", observation.Id);
            var stored = JsonSerializer.Deserialize<ModelResponseObservation>((string)read.ExecuteScalar()!);
            if (stored != observation) throw new InvalidOperationException("A different model response observation already owns this identity.");
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
        }, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<ModelResponseObservation>> ReadAsync(string scopeId, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<ModelResponseObservation>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(paths.ModelResponseMemoryPath)) return [];
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Payload FROM ModelResponses WHERE ScopeId=$scope ORDER BY rowid DESC LIMIT 10000";
            command.Parameters.AddWithValue("$scope", scopeId);
            using var reader = command.ExecuteReader();
            var result = new List<ModelResponseObservation>();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var row = JsonSerializer.Deserialize<ModelResponseObservation>(reader.GetString(0))
                    ?? throw new InvalidDataException("Model response history contains an invalid row.");
                if (row.ScopeId != scopeId || !Valid(row))
                    throw new InvalidDataException("Model response history scope or schema does not match.");
                result.Add(row);
            }
            return result;
        }, ct);

    private static bool Valid(ModelResponseObservation row)
        => !string.IsNullOrWhiteSpace(row.Id) && !string.IsNullOrWhiteSpace(row.ScopeId)
            && !string.IsNullOrWhiteSpace(row.Provider) && ModelResponseLearning.IsExactModel(row.Model)
            && row.TaskFingerprint is { Length: 64 } && row.TaskFingerprint.All(Uri.IsHexDigit)
            && (row.SupersedesId is null || !string.IsNullOrWhiteSpace(row.SupersedesId) && row.SupersedesId != row.Id)
            && row.SchemaVersion == 1 && Enum.IsDefined(row.Outcome) && !string.IsNullOrWhiteSpace(row.EvidenceKind);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = paths.ModelResponseMemoryPath, DefaultTimeout = 5, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
}
