#if DEBUG
using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmCode.Hosting;
using SwarmCore.Secrets;

namespace SwarmCode.App.Platforms.macOS;

/// <summary>Local-only diagnostic: report counts, never credential names, values or session text.</summary>
internal static class MacProfileVerification
{
    public static async Task<int> RunAsync(string? receiptPath = null)
    {
        try
        {
            var paths = new SwarmCodeDataPaths();
            var keyStore = new SwarmCore.Secrets.Apple.MacKeychainKeyStore(
                NullLogger<SwarmCore.Secrets.Apple.MacKeychainKeyStore>.Instance, DesktopHost.MacKeychainServiceName);
            var protector = new EncryptionRequiredSecretProtector(new EnvelopeSecretProtector(keyStore, NullLogger.Instance));
            using var receipt = receiptPath is null ? null : JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath));
            var receiptName = receipt?.RootElement.GetProperty("credentialName").GetString();
            bool? receiptVerified = receiptPath is null ? null : false;
            var credentialValues = new List<string>();
            var credentials = 0;
            var protectedFields = 0;
            var failedFields = 0;
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = paths.SecretsDbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT name, protected_value, protected_username, notes FROM secrets;";
                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    credentials++;
                    for (var column = 1; column < 4; column++)
                    {
                        if (reader.IsDBNull(column) || reader.GetString(column).Length == 0) continue;
                        protectedFields++;
                        // All plaintext stays in this signed native process and
                        // is discarded immediately. Never serialize the result.
                        var value = protector.Unprotect(reader.GetString(column));
                        if (value.Length == 0) failedFields++;
                        else if (column == 1 && receipt is not null) credentialValues.Add(value);
                        if (column == 1 && receipt is not null && reader.GetString(0) == receiptName)
                        {
                            var challenge = receipt.RootElement.GetProperty("challenge").GetString()!;
                            var original = (await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(receiptPath!)!, "challenge.txt"))).Trim();
                            var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(value), Encoding.UTF8.GetBytes(original));
                            var received = Convert.FromHexString(receipt.RootElement.GetProperty("response").GetString()!);
                            receiptVerified = value.Length > 0 && challenge == original && CryptographicOperations.FixedTimeEquals(expected, received);
                        }
                    }
                }
            }
            var conversations = new FileConversationStore(paths);
            var transcripts = new FileAgentSessionStore(paths);
            var presentation = new FileTranscriptJournal(paths);
            var sessions = await conversations.ListRecentAsync(int.MaxValue);
            var turns = 0;
            var messages = 0;
            var entries = 0;
            foreach (var session in sessions)
            {
                turns += (await conversations.LoadTurnsAsync(session.Id)).Count;
                messages += (await transcripts.LoadAsync(session.Id)).Count;
                entries += presentation.Load(session.Id).Entries.Count;
            }
            var transcriptContainsCredentialValue = false;
            if (receipt is not null && receipt.RootElement.TryGetProperty("conversationId", out var conversation))
            {
                var id = conversation.GetString();
                if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid conversation identity.");
                foreach (var relative in new[] { $"transcripts/{id}.jsonl", $"presentation/{id}.jsonl", $"jobs/{id}.json" })
                {
                    var path = Path.Combine(paths.SessionsDirectory, relative);
                    if (!File.Exists(path)) continue;
                    var persisted = await File.ReadAllTextAsync(path);
                    transcriptContainsCredentialValue |= credentialValues.Any(value => persisted.Contains(value, StringComparison.Ordinal)
                        || persisted.Contains(JsonSerializer.Serialize(value)[1..^1], StringComparison.Ordinal));
                }
            }
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                profile = paths.RootDirectory, keychainAvailable = keyStore.IsAvailable,
                credentials, protectedFields, failedFields, sessions = sessions.Count, turns,
                transcriptMessages = messages, presentationEntries = entries, credentialReceiptVerified = receiptVerified, transcriptContainsCredentialValue,
            }));
            return keyStore.IsAvailable && failedFields == 0 && receiptVerified is not false && !transcriptContainsCredentialValue ? 0 : 1;
        }
        catch (Exception ex)
        {
            // Exceptions can contain private source text; return the type only.
            Console.WriteLine(JsonSerializer.Serialize(new { failed = true, errorType = ex.GetType().Name }));
            return 1;
        }
    }
}
#endif
