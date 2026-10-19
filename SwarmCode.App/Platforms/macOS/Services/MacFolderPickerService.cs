using AppKit;
using Foundation;
using SwarmCode.App.Services;

namespace SwarmCode.App.Platforms.macOS.Services;

public sealed class MacFolderPickerService : IFolderPickerService
{
    public bool IsSupported => true;
    public Task<string?> PickFolderAsync(FolderPickerRequest request, CancellationToken ct = default)
        => DesktopPlatform.InvokeAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var panel = NSOpenPanel.OpenPanel;
            panel.CanChooseFiles = false;
            panel.CanChooseDirectories = true;
            panel.AllowsMultipleSelection = false;
            panel.CanCreateDirectories = true;
            panel.Prompt = "Choose";
            panel.Message = request.Title ?? "Choose a folder";
            if (!string.IsNullOrWhiteSpace(request.InitialDirectory))
            {
                var directory = Path.GetFullPath(request.InitialDirectory);
                if (!Directory.Exists(directory)) directory = Path.GetDirectoryName(directory);
                if (directory is not null && Directory.Exists(directory)) panel.DirectoryUrl = NSUrl.FromFilename(directory);
            }
            return panel.RunModal() == 1 ? panel.Url?.Path : null;
        });
}
