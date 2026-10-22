using SwarmCore.Abstractions.LlmRouting;

namespace SwarmCode.App.Components.Pages;

public partial class Settings
{
    private IReadOnlyList<ModelFavorite> _defaultFavorites = [];
    private string? _defaultFavoritesError;
    private ModelFavorite? DefaultModelFavorite => AvailableLanes().FirstOrDefault(l => l.Name == _defaultModelLane) is { } lane
        && !string.IsNullOrWhiteSpace(_defaultModel)
        ? new(lane.Provider, ModelReasoningSelection.Split(_defaultModel.Trim()).Model, lane.Name) : null;
    private bool DefaultFavoriteAvailable(ModelFavorite favorite)
        => AvailableLanes().Any(l => l.Name == favorite.RouteName && l.Provider == favorite.Provider);

    private async Task LoadDefaultFavoritesAsync()
    {
        try
        {
            _defaultFavorites = await Task.Run(() => State.Preferences.Current.FavoriteModels?.ToArray() ?? []);
            _defaultFavoritesError = null;
        }
        catch (Exception) { _defaultFavoritesError = "Pinned models could not be loaded. Reopen Settings to retry."; }
    }

    private async Task ChangeDefaultFavoriteAsync(ModelFavoriteChange change)
    {
        await Task.Run(() => State.Preferences.SetModelFavorite(change.Favorite, change.Pinned));
        await LoadDefaultFavoritesAsync();
    }

    private async Task ChooseDefaultFavoriteAsync(ModelFavorite favorite)
    {
        if (!DefaultFavoriteAvailable(favorite)) throw new InvalidOperationException("This provider is unavailable.");
        _defaultModelLane = favorite.RouteName!;
        _defaultModel = favorite.Model;
        // This changes the settings draft only. The existing Save defaults action
        // retains its conflict baseline and is the sole writer of model defaults.
        await RefreshDefaultModelsAsync();
    }
}
