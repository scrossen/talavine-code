using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCode.App.Components.Pages;

public partial class Session
{
    private IReadOnlyList<ModelFavorite> _modelFavorites = [];
    private bool _favoriteBusy;
    private string? _favoriteError;
    private ModelFavorite? CurrentModelFavorite => Lanes.FirstOrDefault(l => l.Name == _lane) is { } lane
        && !string.IsNullOrWhiteSpace(_model)
        ? new(lane.Provider, ModelReasoningSelection.Split(_model).Model, lane.Name) : null;
    private bool FavoriteAvailable(ModelFavorite favorite)
        => Lanes.Any(l => l.Name == favorite.RouteName && l.Provider == favorite.Provider);

    private async Task LoadModelFavoritesAsync()
    {
        try
        {
            var favorites = await Task.Run(() => App.Preferences.Current.FavoriteModels?.ToArray() ?? []);
            if (_disposed) return;
            _modelFavorites = favorites; _favoriteError = null;
        }
        catch (Exception) { _favoriteError = "Pinned models could not be loaded. Reopen to retry."; }
    }

    private async Task ChangeModelFavoriteAsync(ModelFavoriteChange change)
    {
        await Task.Run(() => App.Preferences.SetModelFavorite(change.Favorite, change.Pinned));
        await LoadModelFavoritesAsync();
    }

    private async Task SelectModelFavoriteAsync(ModelFavorite favorite)
    {
        if (!FavoriteAvailable(favorite)) throw new InvalidOperationException("This provider is unavailable.");
        var sessionId = SessionId;
        var catalog = App.Sessions;
        _favoriteBusy = true;
        try
        {
            // Persist provider and exact model together, using the existing user-selection lane.
            await catalog.SetUserLaneAsync(sessionId, favorite.RouteName!, favorite.Model);
            if (_disposed || SessionId != sessionId) return;
            _session = await catalog.GetAsync(sessionId) ?? throw new InvalidOperationException("The conversation no longer exists.");
            if (_disposed || SessionId != sessionId) return;
            _lane = favorite.RouteName!;
            await LoadModelsAsync();
        }
        finally { _favoriteBusy = false; }
    }
}
