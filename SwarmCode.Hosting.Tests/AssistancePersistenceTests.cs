using SwarmCore.Abstractions.Collaboration;
namespace SwarmCode.Hosting.Tests;
public sealed class AssistancePersistenceTests : TempDirTest
{
    [Fact]
    public async Task Field_writer_preserves_other_edits_rejects_stale_choices_and_does_not_recreate_deleted_session()
    {
        var a = new WorkspaceSessionCatalog(Paths); var b = new WorkspaceSessionCatalog(Paths);
        var session = await a.CreateAsync(Tmp);
        var options = new TurnAssistanceOptions { GetOpinionsFirst = true, RecommendOpinionModels = false,
            OpinionModels = [new("pinned", "Provider", "model", "lane")] };
        await b.RenameAsync(session.Id, "Preserve this title");
        await a.SetAssistanceAsync(session.Id, options, 0);
        var saved = (await b.GetAsync(session.Id))!;
        Assert.Equal("Preserve this title", saved.Title); Assert.True(saved.Assistance.GetOpinionsFirst);
        Assert.Equal("model", Assert.Single(saved.Assistance.OpinionModels).Model);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.SetAssistanceAsync(session.Id, new(), 0));
        await b.SetAssistanceAsync(session.Id, new(), saved.AssistanceRevision);
        Assert.False((await new WorkspaceSessionCatalog(Paths).GetAsync(session.Id))!.Assistance.Enabled);
        await b.DeleteAsync(session.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.SetAssistanceAsync(session.Id, options, 2));
        Assert.Null(await a.GetAsync(session.Id));
    }
    [Fact]
    public async Task Preferences_retry_a_short_sharing_lock_without_substituting_default_permissions()
    {
        var store = new AppPreferencesStore(Paths);
        store.Update(p => { p.Theme = "dark"; p.PermissionMode = "ask"; });
        using var opened = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            using var file = new FileStream(Paths.PreferencesPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            opened.Set(); Thread.Sleep(40);
        });
        Assert.True(opened.Wait(TimeSpan.FromSeconds(3)));
        var actual = store.Current;
        await holder;
        Assert.Equal("dark", actual.Theme); Assert.Equal("ask", actual.PermissionMode);
        var original = await File.ReadAllBytesAsync(Paths.PreferencesPath);
        var edited = false;
        var notified = false;
        store.Changed += () => notified = true;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        using (var locked = new FileStream(Paths.PreferencesPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => store.Current);
            Assert.Throws<IOException>(() => store.Update(p => { edited = true; p.PermissionMode = "allow"; }));
        }
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3), "A persistent sharing lock must fail within the bounded retry window.");
        Assert.False(edited);
        Assert.False(notified);
        Assert.Equal(original, await File.ReadAllBytesAsync(Paths.PreferencesPath));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(Paths.PreferencesPath)!, Path.GetFileName(Paths.PreferencesPath) + ".*.tmp"));
    }

    [Fact]
    public async Task Readers_and_atomic_preference_replacements_preserve_valid_snapshots()
    {
        var store = new AppPreferencesStore(Paths);
        store.Update(p => p.PermissionMode = "ask");
        var writer = Task.Run(() => { for (var i = 0; i < 60; i++) store.Update(p => p.Theme = i % 2 == 0 ? "light" : "dark"); });
        var reader = Task.Run(() => { for (var i = 0; i < 120; i++) Assert.Equal("ask", store.Current.PermissionMode); });
        await Task.WhenAll(writer, reader);
    }

    [Fact]
    public async Task Atomic_replacement_survives_a_bounded_windows_reader_that_denies_delete_sharing()
    {
        var store = new AppPreferencesStore(Paths);
        store.Update(p => p.PermissionMode = "ask");
        using var opened = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            // This is the Windows handle shape that permits another reader but rejects
            // MoveFile(..., replaceExisting): DELETE is not shared for the old snapshot.
            using var file = new FileStream(Paths.PreferencesPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            opened.Set();
            Thread.Sleep(250);
        });

        Assert.True(opened.Wait(TimeSpan.FromSeconds(3)));
        await Task.Run(() => store.Update(p => p.Theme = "dark"));
        await holder;

        var current = store.Current;
        Assert.Equal("ask", current.PermissionMode);
        Assert.Equal("dark", current.Theme);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(Paths.PreferencesPath)!, Path.GetFileName(Paths.PreferencesPath) + ".*.tmp"));
    }

    [WindowsJobFileFact]
    public async Task Atomic_replacement_completes_with_a_delete_sharing_reader_still_open()
    {
        var store = new AppPreferencesStore(Paths);
        store.Update(p => p.PermissionMode = "ask");
        using var reader = new FileStream(Paths.PreferencesPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var writer = Task.Run(() => store.Update(p => p.Theme = "dark"));
        try { await writer.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { reader.Dispose(); await writer; }
        Assert.Equal("ask", store.Current.PermissionMode);
        Assert.Equal("dark", store.Current.Theme);
    }

    [WindowsJobFileFact]
    public async Task Persistent_reader_denial_preserves_preferences_cleans_temp_does_not_notify_and_allows_recovery()
    {
        var store = new AppPreferencesStore(Paths);
        store.Update(p => { p.PermissionMode = "ask"; p.Theme = "light"; });
        var original = await File.ReadAllBytesAsync(Paths.PreferencesPath);
        var notifications = 0;
        store.Changed += () => notifications++;

        using (var reader = new FileStream(Paths.PreferencesPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var writer = Task.Run(() => store.Update(p => p.Theme = "dark"));
            Exception? error;
            try { error = await Record.ExceptionAsync(() => writer.WaitAsync(TimeSpan.FromSeconds(3))); }
            finally { reader.Dispose(); await Record.ExceptionAsync(() => writer); }

            var denied = Assert.IsType<IOException>(error);
            Assert.True(SwarmCore.Abstractions.Storage.TransientFileLock.IsSharingViolation(denied));
            Assert.Equal(original, await File.ReadAllBytesAsync(Paths.PreferencesPath));
            Assert.Equal(0, notifications);
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(Paths.PreferencesPath)!, Path.GetFileName(Paths.PreferencesPath) + ".*.tmp"));
            Assert.Empty(Directory.EnumerateFiles(Paths.RootDirectory, "*.replace-backup"));
        }

        store.Update(p => p.Theme = "dark");
        Assert.Equal("ask", store.Current.PermissionMode);
        Assert.Equal("dark", store.Current.Theme);
        Assert.Equal(1, notifications);
    }
}
