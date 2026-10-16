namespace SwarmCode.App.Services;

public sealed record FolderPickerRequest(
    string Title,
    string? InitialDirectory = null);

/// <summary>Native "choose a folder" dialog. Desktop platforms implement it; platforms without
/// a folder concept (phones) register the no-op and callers fall back to the share sheet.</summary>
public interface IFolderPickerService
{
    /// <summary>True when this platform can show a folder dialog at all — lets callers pick a
    /// share-sheet fallback up front instead of misreading "no dialog" as "user cancelled".</summary>
    bool IsSupported { get; }

    /// <summary>Returns the chosen folder path, or null when the user cancels.</summary>
    Task<string?> PickFolderAsync(FolderPickerRequest request, CancellationToken ct = default);
}

public sealed class NoOpFolderPickerService : IFolderPickerService
{
    public bool IsSupported => false;

    public Task<string?> PickFolderAsync(FolderPickerRequest request, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
