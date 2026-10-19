using Microsoft.Data.Sqlite;

namespace SwarmCode.Hosting.Tests;

public sealed class MacDataPathCompatibilityTests : TempDirTest
{
    [Fact]
    public void ExistingCatalystProfileWinsOverEmptyNativeBootstrap()
    {
        var current = Path.Combine(Tmp, "native");
        var legacy = Path.Combine(Tmp, "catalyst");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "preferences.json"), "{}");
        Assert.Equal(legacy, SwarmCodeDataPaths.SelectMacDataRoot(current, legacy));
    }

    [Fact]
    public void EstablishedNativeProfileKeepsItsAuthority()
    {
        var current = Path.Combine(Tmp, "native");
        var legacy = Path.Combine(Tmp, "catalyst");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(current, "preferences.json"), "{}");
        File.WriteAllText(Path.Combine(legacy, "preferences.json"), "{}");
        Assert.Equal(current, SwarmCodeDataPaths.SelectMacDataRoot(current, legacy));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CredentialOnlyProfileIsNotMistakenForAnEmptyBootstrap(bool nativeHasCredential)
    {
        var current = Path.Combine(Tmp, "native");
        var legacy = Path.Combine(Tmp, "catalyst");
        foreach (var (path, populated) in new[] { (current, nativeHasCredential), (legacy, true) })
        {
            Directory.CreateDirectory(path);
            using var db = new SqliteConnection($"Data Source={Path.Combine(path, "secrets.db")}");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE secrets(name TEXT);" + (populated ? "INSERT INTO secrets VALUES ('synthetic');" : "");
            command.ExecuteNonQuery();
        }
        Assert.Equal(nativeHasCredential ? current : legacy, SwarmCodeDataPaths.SelectMacDataRoot(current, legacy));
    }

    [Fact]
    public void SavedSessionsAreEvidenceOfAnExistingProfileWithoutPreferences()
    {
        var current = Path.Combine(Tmp, "native");
        var legacy = Path.Combine(Tmp, "catalyst");
        Directory.CreateDirectory(Path.Combine(legacy, "sessions"));
        File.WriteAllText(Path.Combine(legacy, "sessions", "synthetic-session.json"), "{}");
        Assert.Equal(legacy, SwarmCodeDataPaths.SelectMacDataRoot(current, legacy));
    }

    [Fact]
    public void DamagedNativeVaultIsNotSilentlyDiscardedDuringSelection()
    {
        var current = Path.Combine(Tmp, "native");
        var legacy = Path.Combine(Tmp, "catalyst");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "preferences.json"), "{}");
        File.WriteAllText(Path.Combine(current, "secrets.db"), "damaged database");
        Assert.Throws<SqliteException>(() => SwarmCodeDataPaths.SelectMacDataRoot(current, legacy));
    }
}
