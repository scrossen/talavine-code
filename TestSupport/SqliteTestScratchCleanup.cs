using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
#if TEST_SCRATCH_HARNESS
namespace SwarmCore.Testing.Harness;
#else
using SwarmCore.Testing;
namespace Talavine.TestSupport;
#endif

internal static class SqliteTestScratchCleanup
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        OwnedTestScratch.BeforeDelete += directory =>
        {
            foreach (var path in OwnedTestScratch.EnumerateFilesWithoutLinks(directory).Where(p => p.EndsWith(".db", StringComparison.OrdinalIgnoreCase))) Clear(path);
        };
        TestTempRoot.BeforeDeleteFile += Clear;
    }
    private static void Clear(string path)
    {
        // These are the existing fixture/profile pool keys; unrelated databases stay open.
        foreach (var value in new[] { $"Data Source={path}", $"DataSource={path}", $"Data Source={path};Mode=ReadOnly",
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString(),
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString(),
            new SqliteConnectionStringBuilder { DataSource = path, Cache = SqliteCacheMode.Shared, Mode = SqliteOpenMode.ReadWriteCreate }.ToString(),
            // A timeout is part of the pool identity when supplied explicitly.
            new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 30 }.ToString(),
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private }.ToString(),
            // Preserve the identity of explicitly configured private-cache reader/writer pools.
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, DefaultTimeout = 30 }.ToString(),
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private, DefaultTimeout = 30 }.ToString() })
        {
            using var connection = new SqliteConnection(value);
            SqliteConnection.ClearPool(connection);
        }
    }
}
