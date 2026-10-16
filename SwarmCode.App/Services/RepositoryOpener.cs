namespace SwarmCode.App.Services;

/// <summary>Folder picker + workspace open flow shared by the sidebar and the home page.</summary>
public sealed class RepositoryOpener(IFolderPickerService picker, AppState state)
{
    /// <summary>Shows the native folder dialog and opens the chosen repository. False when cancelled or failed.</summary>
    public async Task<bool> PickAndOpenAsync()
    {
        var chosen = await picker.PickFolderAsync(new FolderPickerRequest("Open a repository", state.RepositoryRoot));
        return !string.IsNullOrWhiteSpace(chosen) && await state.OpenWorkspaceAsync(chosen);
    }

    public Task<bool> OpenAsync(string path) => state.OpenWorkspaceAsync(path);
}
