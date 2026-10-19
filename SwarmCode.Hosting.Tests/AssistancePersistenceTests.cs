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
        using var locked = new FileStream(Paths.PreferencesPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => store.Current);
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
}
