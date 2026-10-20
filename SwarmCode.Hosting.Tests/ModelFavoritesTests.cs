using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCode.Hosting.Tests;

public sealed class ModelFavoritesTests : TempDirTest
{
    [Fact]
    public async Task Independent_windows_merge_membership_changes_and_preserve_other_preferences()
    {
        var a = new AppPreferencesStore(Paths); var b = new AppPreferencesStore(Paths);
        var first = new ModelFavorite("P", "same", "route-a"); var second = new ModelFavorite("P", "same", "route-b");
        a.Update(p => { p.Theme = "dark"; p.PermissionMode = "ask"; });
        _ = a.Current;
        await Task.WhenAll(Task.Run(() => a.SetModelFavorite(first, true)), Task.Run(() => b.SetModelFavorite(second, true)));
        a.SetModelFavorite(first, true); // idempotent, no duplicate or reorder
        Assert.Equal(2, b.Current.FavoriteModels.Count);
        b.SetModelFavorite(first, false);
        a.Update(p => p.Zoom = 1.25);
        var restored = new AppPreferencesStore(Paths).Current;
        Assert.Equal(second, Assert.Single(restored.FavoriteModels));
        Assert.Equal("dark", restored.Theme); Assert.Equal("ask", restored.PermissionMode); Assert.Equal(1.25, restored.Zoom);
        a.SetModelFavorite(second, false);
        Assert.Empty(new AppPreferencesStore(Paths).Current.FavoriteModels);
    }

    [Fact]
    public async Task Favorite_selection_saves_exact_provider_and_model_without_changing_other_sessions()
    {
        var sessions = new WorkspaceSessionCatalog(Paths);
        var one = await sessions.CreateAsync(Tmp); var two = await sessions.CreateAsync(Tmp);
        var favorite = new ModelFavorite("Provider", "custom-model", "route");
        new AppPreferencesStore(Paths).SetModelFavorite(favorite, true);
        Assert.False((await sessions.GetAsync(one.Id))!.Selection!.UserSelected);
        await sessions.SetUserLaneAsync(one.Id, favorite.RouteName!, favorite.Model);
        var selected = (await sessions.GetAsync(one.Id))!;
        Assert.Equal("route", selected.Selection!.Route); Assert.Equal("custom-model", selected.ModelChoices["route"]);
        Assert.True(selected.Selection.UserSelected);
        Assert.False((await sessions.GetAsync(two.Id))!.Selection!.UserSelected);
        await sessions.DeleteAsync(one.Id);
        Assert.Single(new AppPreferencesStore(Paths).Current.FavoriteModels);
    }

    [Fact]
    public void Unreadable_preferences_are_never_overwritten_by_a_pin()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.PreferencesPath)!);
        File.WriteAllText(Paths.PreferencesPath, "{bad data");
        Assert.ThrowsAny<Exception>(() => new AppPreferencesStore(Paths).SetModelFavorite(new("P", "M", "R"), true));
        Assert.Equal("{bad data", File.ReadAllText(Paths.PreferencesPath));
    }
}
