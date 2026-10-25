using System.Diagnostics;
using System.Text.Json.Nodes;
using SwarmCode.Hosting.Settings;

namespace SwarmCode.Hosting.Tests;

public sealed class SettingsSnapshotTests : TempDirTest
{
    [Fact]
    public void Absent_optional_documents_read_promptly_with_and_without_the_parent_directory()
    {
        var elapsed = Stopwatch.StartNew();
        for (var i = 0; i < 4; i++)
        {
            if (i == 2) Directory.CreateDirectory(Paths.RootDirectory);
            Assert.Empty(SettingsJsonFile.Read(Paths.UserConfigPath));
            Assert.Equal("missing", SettingsJsonFile.Revision(Paths.UserConfigPath));
        }
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1), "Absence must not consume the sharing-violation retry window.");
    }

    [Fact]
    public async Task Independent_preference_writers_preserve_other_fields_and_explicit_false_and_null()
    {
        var first = new AppPreferencesStore(Paths);
        var second = new AppPreferencesStore(Paths);
        first.Update(p => { p.PermissionMode = "ask"; p.ShowReasoning = true; p.EditorCommand = "editor"; });
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var writer = Task.Run(() => first.Update(p =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            p.Theme = "dark";
        }));
        Task? competitor = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            competitor = Task.Run(() =>
            {
                started.SetResult();
                second.Update(p => { p.ShowReasoning = false; p.EditorCommand = null; });
            });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            await Task.WhenAll(competitor is null ? [writer] : [writer, competitor]);
        }
        var saved = new AppPreferencesStore(Paths).Current;
        Assert.Equal("ask", saved.PermissionMode);
        Assert.Equal("dark", saved.Theme);
        Assert.False(saved.ShowReasoning);
        Assert.Null(saved.EditorCommand);
    }

    [Fact]
    public void Revisioned_edits_reject_competing_changes_and_deletion_before_the_callback()
    {
        var store = Seed();
        var revision = SettingsJsonFile.Revision(Paths.PreferencesPath);
        store.Update(p => p.Zoom = 1.2);
        var edited = false;
        Assert.Throws<InvalidOperationException>(() => SettingsJsonFile.Mutate(Paths.PreferencesPath, root => edited = true, revision));
        Assert.False(edited);
        Assert.Equal(1.2, store.Current.Zoom);
        revision = SettingsJsonFile.Revision(Paths.PreferencesPath);
        File.Delete(Paths.PreferencesPath);
        Assert.Throws<InvalidOperationException>(() => SettingsJsonFile.Mutate(Paths.PreferencesPath, root => edited = true, revision));
        Assert.False(edited);
        Assert.False(File.Exists(Paths.PreferencesPath));
    }

    [Fact]
    public void Deletion_after_the_checked_baseline_is_not_recreated_by_replacement()
    {
        Seed();
        var revision = SettingsJsonFile.Revision(Paths.PreferencesPath);
        Assert.ThrowsAny<IOException>(() => SettingsJsonFile.Mutate(Paths.PreferencesPath, root =>
        {
            File.Delete(Paths.PreferencesPath);
            root["theme"] = "dark";
            return true;
        }, revision));
        Assert.False(File.Exists(Paths.PreferencesPath));
        AssertNoStagedFiles();
    }

    [Fact]
    public void First_creation_does_not_overwrite_a_competing_creation_after_the_missing_baseline()
    {
        const string winner = """{"permissionMode":"ask","theme":"light"}""";
        Assert.ThrowsAny<IOException>(() => SettingsJsonFile.Mutate(Paths.PreferencesPath, root =>
        {
            File.WriteAllText(Paths.PreferencesPath, winner);
            root["theme"] = "dark";
            return true;
        }, "missing"));
        Assert.Equal(winner, File.ReadAllText(Paths.PreferencesPath));
        AssertNoStagedFiles();
    }

    [Fact]
    public void Partial_replacement_failure_restores_the_checked_original_and_remains_a_failed_edit()
    {
        var store = Seed();
        var original = File.ReadAllBytes(Paths.PreferencesPath);
        var failure = new IOException("Simulated ERROR_UNABLE_TO_MOVE_REPLACEMENT_2", unchecked((int)0x80070499));
        var failingStore = new AppPreferencesStore(Paths, (temporary, destination, backup) =>
        {
            File.Move(destination, backup);
            throw failure;
        });
        var notifications = 0;
        failingStore.Changed += () => notifications++;
        Assert.Same(failure, Assert.Throws<IOException>(() => failingStore.Update(p => p.Theme = "dark")));
        Assert.Equal(0, notifications);
        Assert.Equal(original, File.ReadAllBytes(Paths.PreferencesPath));
        Assert.Equal("ask", store.Current.PermissionMode);
        Assert.Equal("light", store.Current.Theme);
        AssertNoStagedFiles();
        store.Update(p => p.Theme = "dark");
        Assert.Equal("dark", store.Current.Theme);
    }

    [Fact]
    public void Uncertain_replacement_retains_both_snapshots_and_blocks_fresh_reads_and_writes_after_lease_release()
    {
        Seed();
        var original = File.ReadAllBytes(Paths.PreferencesPath);
        const string winner = """{"permissionMode":"ask","theme":"system"}""";
        var failure = Assert.Throws<IOException>(() => SettingsJsonFile.Mutate(Paths.PreferencesPath, root =>
        {
            root["theme"] = "dark";
            return true;
        }, replaceExisting: (temporary, destination, backup) =>
        {
            File.Move(destination, backup);
            File.WriteAllText(destination, winner);
            throw new IOException("Simulated partial replacement with a competing name");
        }));
        Assert.IsType<AggregateException>(failure.InnerException);
        Assert.Equal(winner, File.ReadAllText(Paths.PreferencesPath));
        var backup = Assert.Single(Directory.EnumerateFiles(Paths.RootDirectory, "*.replace-backup"));
        Assert.Equal(original, File.ReadAllBytes(backup));
        var temporary = Assert.Single(Directory.EnumerateFiles(Paths.RootDirectory, "*.tmp"));
        Assert.Equal("dark", JsonNode.Parse(File.ReadAllText(temporary))!["theme"]!.GetValue<string>());
        Assert.Throws<InvalidDataException>(() => SettingsJsonFile.Read(Paths.PreferencesPath));
        Assert.Throws<InvalidDataException>(() => SettingsJsonFile.Revision(Paths.PreferencesPath));
        File.Delete(Paths.PreferencesPath);
        var independent = new AppPreferencesStore(Paths);
        Assert.Throws<InvalidDataException>(() => independent.Current);
        var edited = false;
        Assert.Throws<InvalidDataException>(() => independent.Update(p => edited = true));
        Assert.False(edited);
        Assert.False(File.Exists(Paths.PreferencesPath));
        Assert.Equal(original, File.ReadAllBytes(backup));
        Assert.True(File.Exists(temporary));
    }

    [Fact]
    public void Reading_current_preferences_inside_an_edit_does_not_reenter_the_file_lease()
    {
        var store = Seed();
        store.Update(p => p.Theme = store.Current.PermissionMode == "ask" ? "dark" : "system");
        Assert.Equal("dark", store.Current.Theme);
    }

    [Fact]
    public void First_use_callback_reads_missing_preferences_and_revision_without_reentering_the_writer_lease()
    {
        var store = new AppPreferencesStore(Paths);
        var elapsed = Stopwatch.StartNew();
        store.Update(p =>
        {
            var current = store.Current;
            Assert.Null(current.PermissionMode);
            Assert.Equal("missing", SettingsJsonFile.Revision(Paths.PreferencesPath));
            p.Theme = current.Theme == "system" ? "dark" : "light";
        });
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1), "A synchronous callback already owns the live writer lease.");
        Assert.Equal("dark", store.Current.Theme);
    }

    [Fact]
    public void Owned_writer_lease_does_not_bypass_a_recovery_marker_inside_the_callback()
    {
        var store = Seed();
        var marker = Paths.PreferencesPath + ".fixture.replace-backup";
        store.Update(p =>
        {
            File.Copy(Paths.PreferencesPath, marker);
            try
            {
                Assert.Throws<InvalidDataException>(() => store.Current);
                Assert.Throws<InvalidDataException>(() => SettingsJsonFile.Revision(Paths.PreferencesPath));
            }
            finally { File.Delete(marker); }
            p.Theme = "dark";
        });
        Assert.Equal("dark", store.Current.Theme);
    }

    [Fact]
    public async Task Captured_execution_context_cannot_authorize_a_read_after_the_writer_lease_ends()
    {
        ExecutionContext? captured = null;
        var store = new AppPreferencesStore(Paths);
        store.Update(p => { captured = ExecutionContext.Capture(); p.Theme = "dark"; });
        Assert.NotNull(captured);
        File.Delete(Paths.PreferencesPath);
        using var entered = new ManualResetEventSlim();
        using var lease = SettingsJsonFile.Lease(Paths.PreferencesPath);
        AppPreferences? actual = null;
        var reader = Task.Run(() => ExecutionContext.Run(captured, _ =>
        {
            entered.Set();
            actual = store.Current;
        }, null));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            await Task.WhenAny(reader, Task.Delay(100));
            Assert.False(reader.IsCompleted, "Captured context must not reuse an expired writer lease.");
            File.WriteAllText(Paths.PreferencesPath, """{"permissionMode":"ask","theme":"light"}""");
        }
        finally { lease.Dispose(); await reader; }
        Assert.Equal("ask", actual!.PermissionMode);
        Assert.Equal("light", actual.Theme);
    }

    [Fact]
    public void Recovery_does_not_restore_a_backup_that_differs_from_the_checked_baseline()
    {
        Seed();
        const string competitor = """{"permissionMode":"ask","theme":"system"}""";
        Assert.Throws<IOException>(() => SettingsJsonFile.Mutate(Paths.PreferencesPath, root =>
        {
            root["theme"] = "dark";
            return true;
        }, replaceExisting: (temporary, destination, backup) =>
        {
            File.WriteAllText(destination, competitor);
            File.Move(destination, backup);
            throw new IOException("Simulated partial replacement after an uncoordinated edit");
        }));
        Assert.False(File.Exists(Paths.PreferencesPath));
        var backup = Assert.Single(Directory.EnumerateFiles(Paths.RootDirectory, "*.replace-backup"));
        Assert.Equal(competitor, File.ReadAllText(backup));
        Assert.Throws<InvalidDataException>(() => new AppPreferencesStore(Paths).Current);
    }

    [WindowsJobFileFact]
    public void Successful_replace_with_failed_backup_cleanup_fails_closed_without_notifying()
    {
        Seed();
        string? retainedBackup = null;
        var store = new AppPreferencesStore(Paths, (temporary, destination, backup) =>
        {
            File.Replace(temporary, destination, backup);
            retainedBackup = backup;
            // Windows refuses deletion of a read-only backup after the actual commit.
            File.SetAttributes(backup, File.GetAttributes(backup) | FileAttributes.ReadOnly);
        });
        var notifications = 0;
        store.Changed += () => notifications++;
        try
        {
            var failure = Assert.Throws<IOException>(() => store.Update(p => p.Theme = "dark"));
            Assert.IsType<UnauthorizedAccessException>(failure.InnerException);
            Assert.Equal(0, notifications);
            Assert.NotNull(retainedBackup);
            Assert.True(File.Exists(retainedBackup));
            Assert.Equal("dark", JsonNode.Parse(File.ReadAllText(Paths.PreferencesPath))!["theme"]!.GetValue<string>());
            Assert.Throws<InvalidDataException>(() => new AppPreferencesStore(Paths).Current);
            Assert.Throws<InvalidDataException>(() => SettingsJsonFile.Revision(Paths.PreferencesPath));
        }
        finally
        {
            if (retainedBackup is not null && File.Exists(retainedBackup))
                File.SetAttributes(retainedBackup, File.GetAttributes(retainedBackup) & ~FileAttributes.ReadOnly);
        }
    }

    private AppPreferencesStore Seed()
    {
        var store = new AppPreferencesStore(Paths);
        store.Update(p => { p.PermissionMode = "ask"; p.Theme = "light"; });
        return store;
    }

    private void AssertNoStagedFiles()
    {
        Assert.Empty(Directory.EnumerateFiles(Paths.RootDirectory, "*.tmp"));
        Assert.Empty(Directory.EnumerateFiles(Paths.RootDirectory, "*.replace-backup"));
    }
}
